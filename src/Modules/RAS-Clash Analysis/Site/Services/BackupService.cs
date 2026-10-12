using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Копии базы (PRINCIPLES.md, раздел 8): каждый час — на этот компьютер и на сервер («Ежечасно»), первая копия дня —
/// ещё и в «Ежедневно». Сервер недоступен — копия остаётся на компьютере, причина — в журнале и на главной странице.
/// Копия делается средствами SQLite (BackupDatabase): цельная, даже если в этот момент идёт запись.
/// </summary>
public sealed class BackupService : BackgroundService
{
    private readonly Database _db;
    private readonly SiteOptions _options;
    private readonly ILogger<BackupService> _log;

    public BackupService(Database db, SiteOptions options, ILogger<BackupService> log)
    {
        _db = db;
        _options = options;
        _log = log;
    }

    public DateTime? LastServerCopy { get; private set; }
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
        var now = DateTime.Now;
        string local;
        try
        {
            Directory.CreateDirectory(_options.LocalBackups);
            local = Path.Combine(_options.LocalBackups, $"clashes-{now:yyyy-MM-dd-HH}.db");
            var temp = local + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            using (var source = _db.Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }
            File.Move(temp, local, overwrite: true);
            Prune(_options.LocalBackups, _options.HourlyBackups);
        }
        catch (Exception ex)
        {
            LastError = "Копия базы не сделана: " + ex.Message;
            _log.LogError(ex, "Копия базы не сделана");
            return;
        }

        try
        {
            CopyTo(local, Path.Combine(_options.Backups, "Ежечасно"), _options.HourlyBackups);
            var daily = Path.Combine(_options.Backups, "Ежедневно");
            if (!File.Exists(Path.Combine(daily, $"clashes-{now:yyyy-MM-dd}.db")))
                CopyTo(local, daily, _options.NightlyBackups, $"clashes-{now:yyyy-MM-dd}.db");
            LastServerCopy = now;
            LastError = null;
            // раз в час — и знак, что сайт работает: пропал без записи — видно, до какого часа был жив
            _log.LogInformation("Копия базы сделана: на этом компьютере и на сервере");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            LastError = "Копия на сервер не записана (" + ex.Message + "). Копия есть на этом компьютере: " + local;
            _log.LogWarning("Копия базы на сервер не записана (на этом компьютере — сделана): {Reason}", ex.Message);
        }
    }

    /// <summary>Через временный файл и переименование: недописанная копия не выглядит целой.</summary>
    private static void CopyTo(string file, string folder, int keep, string name = null)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name ?? Path.GetFileName(file));
        File.Copy(file, target + ".tmp", overwrite: true);
        File.Move(target + ".tmp", target, overwrite: true);
        Prune(folder, keep);
    }

    /// <summary>Оставить последние keep копий (только свои файлы «clashes-*.db»).</summary>
    private static void Prune(string folder, int keep)
    {
        foreach (var old in Directory.GetFiles(folder, "clashes-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Math.Max(keep, 1)))
            File.Delete(old);
    }
}
