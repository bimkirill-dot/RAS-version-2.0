using System.Globalization;
using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Копии базы (PRINCIPLES.md, раздел 8; DECISIONS.md модуля, «Копии базы — раз в день, лесенкой»). Раз в час — проверка,
/// менялась ли база с прошлой копии (отпечаток цельной копии SQLite). Менялась — копия дня «clashes-ГГГГ-ММ-ДД.zip»
/// (сжата в 7 раз): на этот компьютер (последние 3 — на случай, если сервер недоступен) и на сервер, там — лесенкой
/// (Logic.BackupLadder: ежедневные, еженедельные, ежемесячные). За день копия переписывается — копия дня = база на конец дня.
/// Не менялась (ночь, выходные) — копий нет. Сервер недоступен — копия на компьютере, причина — в журнале и в «Настройках».
/// </summary>
public sealed class BackupService : BackgroundService
{
    /// <summary>Сколько копий дня держать на этом компьютере.</summary>
    public const int LocalKeep = 3;
    private const string Prefix = "clashes-";
    private const string StateFile = "последняя копия.txt";

    private readonly Database _db;
    private readonly SiteOptions _options;
    private readonly ILogger<BackupService> _log;
    private readonly object _lock = new(), _stateLock = new();
    private string _localHash, _serverHash;
    private bool _stateRead, _tidied;

    public BackupService(Database db, SiteOptions options, ILogger<BackupService> log)
    {
        _db = db;
        _options = options;
        _log = log;
    }

    /// <summary>Когда копия последний раз записана на сервер (и после перезапуска сайта — из «последняя копия.txt»).</summary>
    public DateTime? LastServerCopy
    {
        // без _lock: «Настройки» не ждут, пока идёт копия на сервер
        get { ReadState(); return _lastServerCopy; }
        private set => _lastServerCopy = value;
    }
    private DateTime? _lastServerCopy;
    public string LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stop);
            while (!stop.IsCancellationRequested)
            {
                await Task.Run(BackupNow, stop);
                await Task.Delay(TimeSpan.FromHours(1), stop);
            }
        }
        catch (OperationCanceledException)
        {
            // сайт останавливается
        }
    }

    public void BackupNow()
    {
        lock (_lock)
        {
            ReadState();
            var now = DateTime.Now;
            var name = $"{Prefix}{now:yyyy-MM-dd}.zip";
            var local = Path.Combine(_options.LocalBackups, name);
            string hash;
            try
            {
                TidyLocal();
                string snapshot;
                using (var source = _db.Open()) snapshot = DatabaseCopy.Snapshot(source, _options.LocalBackups);
                try
                {
                    hash = DatabaseCopy.Hash(snapshot);
                    if (hash != _localHash)
                    {
                        DatabaseCopy.Zip(snapshot, local);
                        _localHash = hash;
                        WriteState();
                        foreach (var old in Days(_options.LocalBackups).OrderByDescending(d => d.Day).Skip(LocalKeep)) File.Delete(old.File);
                    }
                }
                finally
                {
                    File.Delete(snapshot);
                }
            }
            catch (Exception ex)
            {
                LastError = "Копия базы не сделана: " + ex.Message;
                _log.LogError(ex, "Копия базы не сделана");
                return;
            }

            // на сервере уже эта база — ничего не делать (и не будить сеть каждый час)
            if (hash == _serverHash && LastError == null) return;
            try
            {
                Directory.CreateDirectory(_options.Backups);
                TidyServer();
                if (hash != _serverHash)
                {
                    // свежая копия лежит на компьютере под именем сегодняшнего дня — её и на сервер
                    var source = File.Exists(local) ? local : Days(_options.LocalBackups).OrderByDescending(d => d.Day).First().File;
                    var target = Path.Combine(_options.Backups, Path.GetFileName(source));
                    File.Copy(source, target + ".tmp", overwrite: true);
                    File.Move(target + ".tmp", target, overwrite: true);
                    _serverHash = hash;
                    LastServerCopy = now;
                    WriteState();
                    _log.LogInformation("Копия базы сделана: {Name} — на этом компьютере и на сервере", Path.GetFileName(source));
                }
                PruneServer();
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LastError = "Копия на сервер не записана (" + ex.Message + "). Копия есть на этом компьютере: " + local;
                _log.LogWarning("Копия базы на сервер не записана (на этом компьютере — сделана): {Reason}", ex.Message);
            }
        }
    }

    /// <summary>На сервере — лесенкой: последние ежедневные, по последней копии недели и месяца.</summary>
    private void PruneServer()
    {
        var copies = Days(_options.Backups);
        var keep = BackupLadder.Keep(copies.Select(c => c.Day), _options.DailyBackups, _options.WeeklyBackups, _options.MonthlyBackups);
        foreach (var old in copies.Where(c => !keep.Contains(c.Day)))
        {
            File.Delete(old.File);
            _log.LogInformation("Старая копия базы удалена с сервера: {Name}", Path.GetFileName(old.File));
        }
    }

    /// <summary>Копии дня в папке: «clashes-ГГГГ-ММ-ДД.zip».</summary>
    private static List<(DateTime Day, string File)> Days(string folder)
    {
        var list = new List<(DateTime, string)>();
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.GetFiles(folder, Prefix + "*.zip"))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)[Prefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                list.Add((day, file));
        return list;
    }

    // ---- что сделано в прошлый раз: отпечатки копий на компьютере и на сервере (переживают перезапуск сайта) ----

    private string StatePath => Path.Combine(_options.LocalBackups, StateFile);

    private void ReadState()
    {
        lock (_stateLock)
        {
            if (_stateRead) return;
            _stateRead = true;
            ReadStateFile();
        }
    }

    private void ReadStateFile()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            foreach (var line in File.ReadAllLines(StatePath))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var (key, value) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
                if (key == "компьютер") _localHash = value;
                else if (key == "сервер") _serverHash = value;
                else if (key == "на сервере" && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) _lastServerCopy = t;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log.LogWarning("Не прочитано, какие копии базы уже сделаны ({Reason}) — копия будет сделана заново", ex.Message);
        }
    }

    private void WriteState()
    {
        Directory.CreateDirectory(_options.LocalBackups);
        File.WriteAllLines(StatePath, new[]
        {
            "# Отпечатки последних копий базы: совпадает с базой — новая копия не нужна. Удалить файл — копия будет сделана заново.",
            "компьютер = " + _localHash,
            "сервер = " + _serverHash,
            "на сервере = " + _lastServerCopy?.ToString("s", CultureInfo.InvariantCulture),
        });
    }

    // ---- прежний порядок копий (до 21.10.2026): почасовые .db, «Ежечасно», «Ежедневно», «перед обновлением …»,
    //      копии «clashes.db.до-…» рядом с базой — переводятся в новый один раз, при первой копии ----

    private void TidyLocal()
    {
        if (_tidied) return;
        _tidied = true;
        var folder = _options.LocalBackups;
        var before = DatabaseCopy.BeforeChangesOf(_db.File);
        Directory.CreateDirectory(before);
        foreach (var hourly in Directory.GetFiles(folder, Prefix + "*.db")) File.Delete(hourly);
        // папки «перед обновлением ГГГГ-ММ-ДД ЧЧММСС» (база с журналом WAL и, по ошибке, все прежние копии) — база архивом
        foreach (var dir in Directory.GetDirectories(folder, "перед обновлением *"))
        {
            var stamp = Path.GetFileName(dir)["перед обновлением ".Length..];
            var db = Path.Combine(dir, DatabaseCopy.Inside);
            if (File.Exists(db)) ZipOld(db, Path.Combine(before, $"{stamp} перед обновлением.zip"));
            Directory.Delete(dir, recursive: true);
        }
        // «clashes.db.до-версии-16-20261020-145457», «clashes.db.до-удаления-Проект-20261019-165726»
        var data = Path.GetDirectoryName(_db.File)!;
        foreach (var file in Directory.GetFiles(data, DatabaseCopy.Inside + ".до-*"))
        {
            var rest = Path.GetFileName(file)[(DatabaseCopy.Inside.Length + ".до-".Length)..];
            if (rest.Length > 16 && DateTime.TryParseExact(rest[^15..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                var what = rest[..^16];
                what = what.StartsWith("версии-", StringComparison.Ordinal) ? "перед переносом на версию " + what["версии-".Length..]
                     : what == "удаления-отчётов" ? "перед удалением отчётов"
                     : what.StartsWith("удаления-", StringComparison.Ordinal) ? $"перед удалением «{what["удаления-".Length..]}»"
                     : "перед " + what;
                ZipOld(file, Path.Combine(before, $"{when:yyyy-MM-dd HHmmss} {DatabaseCopy.SafeName(what)}.zip"));
            }
            File.Delete(file);
        }
        DatabaseCopy.PruneBeforeChanges(before);
    }

    /// <summary>Старая копия — архивом; рядом с ней может быть журнал WAL: копия читается через SQLite — с ним вместе.</summary>
    private static void ZipOld(string db, string zip)
    {
        if (File.Exists(zip)) return;
        string snapshot;
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            source.Open();
            snapshot = DatabaseCopy.Snapshot(source, Path.GetDirectoryName(zip)!);
        }
        try { DatabaseCopy.Zip(snapshot, zip); }
        finally { File.Delete(snapshot); }
    }

    private void TidyServer()
    {
        var hourly = Path.Combine(_options.Backups, "Ежечасно");
        var daily = Path.Combine(_options.Backups, "Ежедневно");
        if (Directory.Exists(daily))
        {
            foreach (var file in Directory.GetFiles(daily, Prefix + "*.db"))
            {
                var zip = Path.Combine(_options.Backups, Path.GetFileNameWithoutExtension(file) + ".zip");
                if (!File.Exists(zip)) DatabaseCopy.Zip(file, zip);
            }
            Directory.Delete(daily, recursive: true);
            _log.LogInformation("Копии базы на сервере: «Ежедневно» — архивами в общую папку, по новому порядку");
        }
        if (Directory.Exists(hourly))
        {
            Directory.Delete(hourly, recursive: true);
            _log.LogInformation("Копии базы на сервере: почасовые («Ежечасно») удалены — копии раз в день");
        }
    }
}
