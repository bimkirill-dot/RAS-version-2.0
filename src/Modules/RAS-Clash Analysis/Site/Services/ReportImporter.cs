using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Что получилось с одним файлом при проверке папки.</summary>
public sealed class ImportResult
{
    public string ProjectName { get; set; }
    public string File { get; set; }
    public bool Error { get; set; }
    public string Message { get; set; }
    public int New { get; set; }
    public int Again { get; set; }
    /// <summary>Вернулись после исправления.</summary>
    public int Reappeared { get; set; }
    public int Fixed { get; set; }
    /// <summary>Предупреждение (неполный отчёт, пропало подозрительно много); null — нет.</summary>
    public string Warning { get; set; }
    /// <summary>Пропало подозрительно много (ReportMerge.TooManyGone) — у отчёта «Вернуть активными».</summary>
    public bool Suspicious { get; set; }
    /// <summary>Файла больше нет в папке проекта — отчёт удалён с сайта.</summary>
    public bool Deleted { get; set; }
    /// <summary>Сколько коллизий удалено вместе с отчётом (были только в нём).</summary>
    public int RemovedClashes { get; set; }
    /// <summary>Выгружен заново в тот же прогон — заменил прежний вариант.</summary>
    public bool Replaced { get; set; }
    /// <summary>Отчёт старше уже загруженных по своим проверкам — только дополнил историю.</summary>
    public bool Older { get; set; }
    /// <summary>Проверки, впервые попавшие на сайт с этим отчётом, и их коллизии (не «новые» — их впервые проверили).</summary>
    public int NewTests { get; set; }
    public int NewTestClashes { get; set; }
    /// <summary>Все проверки отчёта — впервые: «первая проверка».</summary>
    public bool AllTestsNew { get; set; }
    /// <summary>Блок и день прогона — для истории сайта.</summary>
    public string Block { get; set; } = "";
    public DateTime? Day { get; set; }
}

/// <summary>
/// Загрузка отчётов из папок проектов (DECISIONS.md модуля: проект — папка с отчётами; проверка — из отчёта; дата — из XML).
/// Отчёт — XML в папке проекта и её подпапках (кроме папок снимков «…_files»). Тот же файл с тем же содержимым второй раз
/// не загружается; Navisworks перезаписал файл — это новый отчёт, а если день прогона тот же (выгрузили заново в ту же папку
/// прогона) — замена прежнего (DECISIONS.md, «Замена отчёта»); файла больше нет в папке — отчёт удаляется и с сайта
/// («Удаление отчёта — по папке»). Снимки копируются к себе: в папке их перезапишет следующий прогон.
/// </summary>
public sealed class ReportImporter
{
    /// <summary>Обход папок: недоступные подпапки пропускаются, а не обрывают проверку всего проекта.</summary>
    private static readonly EnumerationOptions Deep = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

    private readonly Database _db;
    private readonly SiteOptions _options;
    private readonly ILogger<ReportImporter> _log;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    /// <summary>Файлы, которые не прочитались, — чтобы не писать о них в журнал при каждой проверке (путь + содержимое).</summary>
    private readonly HashSet<string> _badFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ReportImporter(Database db, SiteOptions options, ILogger<ReportImporter> log)
    {
        _db = db;
        _options = options;
        _log = log;
    }

    public DateTime? LastScan { get; private set; }
    public IReadOnlyList<ImportResult> LastResults { get; private set; } = Array.Empty<ImportResult>();
    public bool Busy => _lock.CurrentCount == 0;

    /// <summary>Проверить папки всех проектов (или одного) и загрузить новые отчёты. Одна проверка за раз.</summary>
    public async Task<IReadOnlyList<ImportResult>> ScanAsync(long? projectId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var results = await Task.Run(() => Scan(projectId));
            LastScan = DateTime.Now;
            LastResults = results;
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сделать работу, пока не идёт проверка папок (и не начнётся новая), — удаление проекта.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<T> work)
    {
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<ImportResult> Scan(long? projectId)
    {
        var results = new List<ImportResult>();
        _backupDone = false;
        var projects = new List<(long Id, string Name, string Folder, bool Manual)>();
        using (var c = _db.Open())
        using (var cmd = ClashStore.Command(c, "SELECT Id, Name, Folder, ManualBlocks FROM Projects"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (projectId == null || r.GetInt64(0) == projectId)
                    projects.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0));

        foreach (var p in projects)
        {
            var start = results.Count;
            if (!Directory.Exists(p.Folder))
            {
                results.Add(new ImportResult { ProjectName = p.Name, File = p.Folder, Error = true, Message = "Папка отчётов не найдена или недоступна." });
                WriteHistory(p.Id, results.Skip(start).ToList());
                continue;
            }
            var candidates = new List<(string Path, string Hash, DateTime FileTime, ClashReport Report, ReportPlace Place, DateTime Date, string DateSource, long? Replaces)>();
            var files = ReportFiles(p.Folder).ToList();
            // блоки вручную: без блоков отчёты не загружаются; отчёт вне всех блоков — тоже, его папка — в предупреждении
            List<(string Name, string Folder)> blocks = null;
            if (p.Manual)
            {
                blocks = Blocks(p.Id);
                var outside = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                if (blocks.Count > 0)
                    foreach (var f in files)
                        if (ProjectBlocks.BlockOf(blocks, p.Folder, f) == null)
                            outside.Add(ProjectBlocks.Unassigned(blocks.Select(b => b.Folder), p.Folder, f));
                _outside[p.Id] = outside.ToList();
                if (blocks.Count == 0) continue;
            }
            // файлов больше нет в папке — удалить и с сайта (до загрузки новых: удалённый прогон мог быть последним)
            results.AddRange(RemoveMissing(p.Id, p.Name, p.Folder, files));
            foreach (var file in files)
            {
                string block = null;
                if (blocks != null && (block = ProjectBlocks.BlockOf(blocks, p.Folder, file)) == null) continue;
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (Known(p.Id, file, hash)) continue;
                    var badKey = file + "|" + hash;
                    if (_badFiles.Contains(badKey)) continue;
                    var fileTime = File.GetLastWriteTime(file);
                    ClashReport report;
                    try
                    {
                        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        report = ClashReportReader.Parse(reader, file, fileTime, w => _log.LogInformation("{File}: {Warning}", file, w));
                    }
                    catch (InvalidDataException ex)
                    {
                        _badFiles.Add(badKey);
                        _log.LogInformation("Не отчёт или не читается: {File} — {Reason}", file, ex.Message);
                        results.Add(new ImportResult { ProjectName = p.Name, File = file, Error = true, Message = ex.Message });
                        continue;
                    }
                    // критичность, блок и дата прогона — из названий папок (ReportPath); блок проекта с блоками вручную — его блок
                    var place = ReportPath.Parse(p.Folder, file);
                    if (block != null) place.Block = block;
                    var date = ReportPath.ReportDate(report, fileTime, place.RunDate, out var dateSource);
                    // тот же файл, тот же день прогона — выгрузили заново: замена прежнего отчёта
                    var previous = PreviousVariant(p.Id, file);
                    long? replaces = previous != null && previous.Value.Date.Date == date.Date ? previous.Value.Id : null;
                    if (replaces != null && LaterRuns(replaces.Value) is string blocker)
                    {
                        _badFiles.Add(badKey);
                        _log.LogWarning("Отчёт заменён, но не загружен: {File} — {Reason}", file, blocker);
                        results.Add(new ImportResult { ProjectName = p.Name, File = file, Error = true,
                            Message = "Файл выгружен заново, но " + blocker + " Заменить можно только последний отчёт проверки; иначе — удалить проект и добавить заново." });
                        continue;
                    }
                    candidates.Add((file, hash, fileTime, report, place, date, dateSource, replaces));
                }
                catch (IOException ex)
                {
                    // файл ещё пишет Navisworks или он занят — возьмём при следующей проверке
                    results.Add(new ImportResult { ProjectName = p.Name, File = file, Error = true, Message = "Файл занят или недоступен — повторим позже: " + ex.Message });
                }
                catch (UnauthorizedAccessException ex)
                {
                    results.Add(new ImportResult { ProjectName = p.Name, File = file, Error = true, Message = "Нет доступа: " + ex.Message });
                }
            }
            // замена отчётов — с копией базы перед первой из них (одна на проверку папок)
            if (candidates.Any(x => x.Replaces != null)) BackupOnce("замены-отчётов");
            // по дате отчёта: история коллизий складывается в том порядке, в каком шли прогоны
            foreach (var x in candidates.OrderBy(x => x.Date).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var result = Import(p.Id, x.Path, x.Hash, x.FileTime, x.Report, x.Place, x.Date, x.DateSource, x.Replaces);
                    result.ProjectName = p.Name;
                    results.Add(result);
                    _log.LogInformation("Загружен отчёт {File}: новых {New}, снова {Again}, появились снова {Reappeared}, исправлены {Fixed}",
                        x.Path, result.New, result.Again, result.Reappeared, result.Fixed);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Не удалось загрузить отчёт {File}", x.Path);
                    results.Add(new ImportResult { ProjectName = p.Name, File = x.Path, Error = true, Message = "Не удалось загрузить: " + ex.Message });
                }
            }
            WriteHistory(p.Id, results.Skip(start).ToList());
        }
        return results;
    }

    private static IEnumerable<string> ReportFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.xml", Deep)
            .Where(f => !Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar)
                .Any(d => d.EndsWith("_files", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Файл уже загружен в этом виде — сравнение только с последним загруженным вариантом файла: прогон может вернуть
    /// ровно то же, что было два прогона назад (коллизия вернулась), и это новый отчёт.
    /// </summary>
    private bool Known(long projectId, string path, string hash)
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT FileHash FROM Reports WHERE ProjectId = $p AND FilePath = $f ORDER BY Id DESC LIMIT 1",
            ("$p", projectId), ("$f", path));
        return cmd.ExecuteScalar() as string == hash;
    }

    /// <summary>
    /// Отчёты, файлов которых больше нет в папке проекта, — удалить и с сайта (DECISIONS.md, «Удаление отчёта — по папке»):
    /// всё, что они принесли, отменяется, как при замене, только без нового отчёта. С последних: удалить можно только
    /// последний отчёт своих проверок (LaterRuns) — по одному, от новых к старым, так удаляются и несколько прогонов подряд.
    /// Защита от недоступной папки: в папке нет ни одного отчёта или пропало больше половины — ничего не удаляется.
    /// </summary>
    private List<ImportResult> RemoveMissing(long projectId, string projectName, string folder, List<string> files)
    {
        var results = new List<ImportResult>();
        var stored = new List<(long Id, string Path, string Block, DateTime Date)>();
        using (var c = _db.Open())
        using (var cmd = ClashStore.Command(c, "SELECT Id, FilePath, Block, ReportDate FROM Reports WHERE ProjectId = $p ORDER BY ReportDate DESC, Id DESC", ("$p", projectId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) stored.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), Database.Time(r.GetString(3))));
        if (stored.Count == 0) return results;

        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        // только отчёты из этой папки проекта (папку проекта могли сменить — прежние не трогаем)
        var missing = stored.Where(x => !present.Contains(x.Path) && x.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !File.Exists(x.Path)).ToList();
        if (missing.Count == 0) return results;

        if (files.Count == 0 || missing.Count * 2 > stored.Count)
        {
            var why = files.Count == 0 ? "в папке проекта не найдено ни одного отчёта" : $"из папки пропало {missing.Count} отчётов из {stored.Count}";
            _log.LogWarning("Проект «{Name}»: {Why} — с сайта ничего не удалено", projectName, why);
            results.Add(new ImportResult { ProjectName = projectName, File = folder, Error = true,
                Message = $"{char.ToUpper(why[0])}{why[1..]} — похоже, папка недоступна или её переложили. С сайта ничего не удалено. "
                          + "Удалить проект целиком — «Удалить проект» в разделе «Проект»." });
            return results;
        }

        foreach (var (id, path, block, date) in missing)
        {
            if (LaterRuns(id) is string blocker)
            {
                results.Add(new ImportResult { ProjectName = projectName, File = path, Error = true,
                    Message = "Файла нет в папке, но на сайте отчёт оставлен: " + blocker + " Удалять прогоны можно с последнего." });
                continue;
            }
            BackupOnce("удаления-отчётов");
            int removed, comments;
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                var pending = UndoReport(c, tx, id);
                (removed, comments) = FinishReplace(c, tx, id, pending);
                tx.Commit();
            }
            var images = Path.Combine(_options.ImagesFolder, id.ToString());
            try
            {
                if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.LogWarning("Снимки удалённого отчёта не удалены: {Folder} — {Reason}", images, ex.Message);
            }
            _log.LogInformation("Отчёт удалён с сайта — файла нет в папке: {File}; коллизий только из него удалено {Removed}, комментариев у них {Comments}",
                path, removed, comments);
            results.Add(new ImportResult { ProjectName = projectName, File = path, Deleted = true, RemovedClashes = removed, Block = block, Day = date.Date,
                Message = "Файла нет в папке — удалён с сайта" + (removed > 0 ? $"; коллизий только из него — {removed}" : "") });
        }
        return results;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, List<string>> _outside = new();

    /// <summary>
    /// Папки проекта с отчётами вне всех блоков (блоки вручную) — по последней проверке папки: отчёты из них не загружены,
    /// на сайте — предупреждение «добавьте блок». «» — отчёты прямо в папке проекта.
    /// </summary>
    public IReadOnlyList<string> OutsideBlocks(long projectId) => _outside.TryGetValue(projectId, out var list) ? list : Array.Empty<string>();

    private List<(string Name, string Folder)> Blocks(long projectId)
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT Name, Folder FROM Blocks WHERE ProjectId = $p", ("$p", projectId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Отказы, уже записанные в историю сайта (проект|файл|причина): одна и та же причина при каждой проверке — одна запись.</summary>
    private readonly HashSet<string> _loggedRefusals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Итог проверки папки проекта — в историю сайта (DECISIONS.md, «История сайта»): загрузки по прогонам, замены, удаления,
    /// предупреждения, отказы (повтор той же причины — без новой записи; «файл занят» — не записывается). Ничего не
    /// изменилось — ничего не пишется. Сбой записи истории загрузку не ломает.
    /// </summary>
    private void WriteHistory(long projectId, List<ImportResult> list)
    {
        var prefix = projectId + "|";
        var refusals = list.Where(r => r.Error && !r.Message.StartsWith("Файл занят", StringComparison.Ordinal)).ToList();
        var keys = refusals.Select(r => prefix + r.File + "|" + r.Message).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loggedRefusals.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal) && !keys.Contains(k));
        var fresh = refusals.Where(r => _loggedRefusals.Add(prefix + r.File + "|" + r.Message)).ToList();
        var done = list.Where(r => !r.Error).ToList();
        if (done.Count == 0 && fresh.Count == 0) return;
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var g in done.Where(r => r.Deleted).GroupBy(r => (r.Block, r.Day)).OrderByDescending(g => g.Key.Day))
            {
                var removed = g.Sum(r => r.RemovedClashes);
                SiteHistory.Write(c, tx, projectId, SiteHistory.Remove,
                    $"{SiteHistory.Run(g.Key.Block, g.Key.Day)}: файлов нет в папке — удалено {Fmt.Plural(g.Count(), "отчёт", "отчёта", "отчётов")}"
                    + (removed > 0 ? $" (коллизий только из них — {removed})" : ""));
            }
            foreach (var r in done.Where(r => r.Replaced))
                SiteHistory.Write(c, tx, projectId, SiteHistory.Replace,
                    $"{SiteHistory.Run(r.Block, r.Day)} «{Path.GetFileName(r.File)}» — выгружен заново, теперь по нему: {SiteHistory.Changes(r.New, r.Fixed, r.Reappeared, r.NewTests, r.NewTestClashes)}");
            foreach (var g in done.Where(r => !r.Deleted && !r.Replaced).GroupBy(r => (r.Block, r.Day)).OrderBy(g => g.Key.Day))
            {
                var all = g.ToList();
                var head = $"{SiteHistory.Run(g.Key.Block, g.Key.Day)}: загружено {Fmt.Plural(all.Count, "отчёт", "отчёта", "отчётов")}";
                var text = all.All(r => r.Older) ? head + " — более ранний прогон, дополнил историю"
                    : all.All(r => r.AllTestsNew) ? head + " — первая проверка, " + Fmt.Plural(all.Sum(r => r.NewTestClashes), "коллизия", "коллизии", "коллизий")
                    : head + " — " + SiteHistory.Changes(all.Sum(r => r.New), all.Sum(r => r.Fixed), all.Sum(r => r.Reappeared), all.Sum(r => r.NewTests), all.Sum(r => r.NewTestClashes));
                SiteHistory.Write(c, tx, projectId, SiteHistory.Import, text);
            }
            foreach (var r in done.Where(r => !r.Deleted && r.Warning != null))
                SiteHistory.Write(c, tx, projectId, SiteHistory.Warning, $"{SiteHistory.Run(r.Block, r.Day)} «{Path.GetFileName(r.File)}»: {SiteHistory.Cut(r.Warning, 600)}");
            foreach (var r in fresh)
                SiteHistory.Write(c, tx, projectId, SiteHistory.Refused, $"«{Path.GetFileName(r.File)}»: {SiteHistory.Cut(r.Message, 600)}");
            tx.Commit();
        }
        catch (Exception ex) when (ex is SqliteException || ex is IOException)
        {
            _log.LogWarning(ex, "История сайта: не записан итог проверки папки проекта {Project}", projectId);
        }
    }

    /// <summary>Последний загруженный вариант файла: номер отчёта и его дата; не загружался — null.</summary>
    private (long Id, DateTime Date)? PreviousVariant(long projectId, string path)
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT Id, ReportDate FROM Reports WHERE ProjectId = $p AND FilePath = $f ORDER BY Id DESC LIMIT 1",
            ("$p", projectId), ("$f", path));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), Database.Time(r.GetString(1))) : null;
    }

    /// <summary>
    /// Почему отчёт нельзя заменить: по его проверке уже загружены более поздние прогоны (их история опирается на него);
    /// можно — null.
    /// </summary>
    private string LaterRuns(long reportId)
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, """
            SELECT t.Name, MAX(ro.ReportDate) FROM ReportTests rt
            JOIN Reports rp ON rp.Id = rt.ReportId
            JOIN Tests t ON t.Id = rt.TestId
            JOIN ReportTests o ON o.TestId = rt.TestId AND o.ReportId <> rt.ReportId
            JOIN Reports ro ON ro.Id = o.ReportId
            WHERE rt.ReportId = $r AND ro.ReportDate > rp.ReportDate
            GROUP BY t.Id LIMIT 1
            """, ("$r", reportId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? $"по проверке «{r.GetString(0)}» уже загружен более поздний прогон ({Database.Time(r.GetString(1)):dd.MM.yyyy})." : null;
    }

    private bool _backupDone;

    /// <summary>Копия базы рядом с ней перед заменой или удалением отчётов — как перед удалением проекта; одна на проверку папок.</summary>
    private void BackupOnce(string why)
    {
        if (_backupDone) return;
        _backupDone = true;
        var copy = _db.File + $".до-{why}-{DateTime.Now:yyyyMMdd-HHmmss}";
        using (var source = _db.Open())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        _log.LogInformation("Копия базы перед изменением отчётов: {Copy}", copy);
    }

    /// <summary>
    /// Отменить всё, что принёс отчёт (он — последний по своим проверкам, LaterRuns): его появления и записи истории
    /// убираются, у затронутых коллизий — состояние до него (последняя оставшаяся смена статуса, в том числе ручная),
    /// у проверок — прежний последний отчёт. Коллизии, найденные им, пока остаются без появлений («ждут» новый отчёт):
    /// есть в новом — сохраняются с историей и комментариями, нет — удаляются (FinishReplace).
    /// </summary>
    private static HashSet<long> UndoReport(SqliteConnection c, SqliteTransaction tx, long reportId)
    {
        var pending = new HashSet<long>();
        using (var cmd = ClashStore.Command(c, "SELECT DISTINCT ClashId FROM Events WHERE ReportId = $r AND Kind = 'found'", ("$r", reportId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) pending.Add(r.GetInt64(0));
        }
        var affected = new HashSet<long>();
        using (var cmd = ClashStore.Command(c, "SELECT ClashId FROM Occurrences WHERE ReportId = $r UNION SELECT ClashId FROM Events WHERE ReportId = $r", ("$r", reportId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) affected.Add(r.GetInt64(0));
        }
        var tests = new List<long>();
        using (var cmd = ClashStore.Command(c, "SELECT TestId FROM ReportTests WHERE ReportId = $r", ("$r", reportId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) tests.Add(r.GetInt64(0));
        }

        Exec(c, tx, """
            UPDATE Clashes SET ReturnCount = MAX(0, ReturnCount - (SELECT COUNT(*) FROM Events e WHERE e.ClashId = Clashes.Id AND e.ReportId = $r AND e.Kind = 'reappeared'))
            WHERE Id IN (SELECT ClashId FROM Events WHERE ReportId = $r AND Kind = 'reappeared')
            """, ("$r", reportId));
        Exec(c, tx, "DELETE FROM Events WHERE ReportId = $r AND Kind <> 'found'", ("$r", reportId));
        Exec(c, tx, "DELETE FROM Elements WHERE OccurrenceId IN (SELECT Id FROM Occurrences WHERE ReportId = $r)", ("$r", reportId));
        Exec(c, tx, "DELETE FROM Occurrences WHERE ReportId = $r", ("$r", reportId));
        Exec(c, tx, "DELETE FROM ReportTests WHERE ReportId = $r", ("$r", reportId));

        // проверки — прежний последний отчёт
        foreach (var t in tests)
            Exec(c, tx, """
                UPDATE Tests SET
                    LastReportId = (SELECT rp.Id FROM ReportTests rt JOIN Reports rp ON rp.Id = rt.ReportId WHERE rt.TestId = $t ORDER BY rp.ReportDate DESC, rp.Id DESC LIMIT 1),
                    LastReportDate = (SELECT rp.ReportDate FROM ReportTests rt JOIN Reports rp ON rp.Id = rt.ReportId WHERE rt.TestId = $t ORDER BY rp.ReportDate DESC, rp.Id DESC LIMIT 1)
                WHERE Id = $t
                """, ("$t", t));

        // затронутые коллизии, найденные раньше, — состояние до этого отчёта
        foreach (var id in affected.Where(id => !pending.Contains(id)))
            Exec(c, tx, """
                UPDATE Clashes SET
                    State = COALESCE((SELECT e.NewState FROM Events e WHERE e.ClashId = $id AND e.NewState IS NOT NULL ORDER BY e.Id DESC LIMIT 1), State),
                    FirstSeen = COALESCE((SELECT MIN(r.ReportDate) FROM Occurrences o JOIN Reports r ON r.Id = o.ReportId WHERE o.ClashId = $id), FirstSeen),
                    LastSeen = COALESCE((SELECT MAX(r.ReportDate) FROM Occurrences o JOIN Reports r ON r.Id = o.ReportId WHERE o.ClashId = $id), LastSeen),
                    LastOccurrenceId = (SELECT o.Id FROM Occurrences o JOIN Reports r ON r.Id = o.ReportId WHERE o.ClashId = $id ORDER BY r.ReportDate DESC, o.Id DESC LIMIT 1),
                    InLastReport = EXISTS(SELECT 1 FROM Occurrences o JOIN Tests t ON t.Id = Clashes.TestId
                                          WHERE o.ClashId = $id AND o.ReportId = t.LastReportId AND o.NavisStatus <> $resolved)
                WHERE Id = $id
                """, ("$id", id), ("$resolved", (int)NavisworksStatus.Resolved));
        foreach (var id in pending)
            Exec(c, tx, "UPDATE Clashes SET LastOccurrenceId = NULL, InLastReport = 0 WHERE Id = $id", ("$id", id));
        return pending;
    }

    /// <summary>
    /// После загрузки нового отчёта вместо прежнего: коллизии прежнего, которых в новом нет, удаляются (их не было —
    /// отчёт выгрузили неправильно); записи истории, ещё указывающие на прежний отчёт, отвязываются; проверки без отчётов и
    /// коллизий — из базы; прежний отчёт — из базы.
    /// Возвращает: сколько коллизий удалено и сколько у них было комментариев.
    /// </summary>
    private static (int Clashes, int Comments) FinishReplace(SqliteConnection c, SqliteTransaction tx, long oldReport, HashSet<long> pending)
    {
        var gone = new List<long>();
        foreach (var id in pending)
        {
            using var cmd = ClashStore.Command(c, "SELECT COUNT(*) FROM Occurrences WHERE ClashId = $id", ("$id", id));
            cmd.Transaction = tx;
            if ((long)cmd.ExecuteScalar()! == 0) gone.Add(id);
        }
        var comments = 0;
        foreach (var id in gone)
        {
            using (var cmd = ClashStore.Command(c, "SELECT COUNT(*) FROM Events WHERE ClashId = $id AND Kind = 'comment'", ("$id", id)))
            {
                cmd.Transaction = tx;
                comments += (int)(long)cmd.ExecuteScalar()!;
            }
            Exec(c, tx, "DELETE FROM Events WHERE ClashId = $id", ("$id", id));
            Exec(c, tx, "UPDATE SiteLog SET ClashId = NULL WHERE ClashId = $id", ("$id", id));
            Exec(c, tx, "DELETE FROM Clashes WHERE Id = $id", ("$id", id));
        }
        Exec(c, tx, "UPDATE Events SET ReportId = NULL WHERE ReportId = $r", ("$r", oldReport));
        // проверка, которой не осталось ни в одном отчёте и без коллизий (например, с опечаткой в названии, исправленной при новой выгрузке), — не нужна
        Exec(c, tx, """
            DELETE FROM Tests WHERE ProjectId = (SELECT ProjectId FROM Reports WHERE Id = $r)
              AND NOT EXISTS(SELECT 1 FROM ReportTests rt WHERE rt.TestId = Tests.Id) AND NOT EXISTS(SELECT 1 FROM Clashes cl WHERE cl.TestId = Tests.Id)
            """, ("$r", oldReport));
        Exec(c, tx, "DELETE FROM Reports WHERE Id = $r", ("$r", oldReport));
        return (gone.Count, comments);
    }

    private ImportResult Import(long projectId, string path, string hash, DateTime fileTime, ClashReport report, ReportPlace place,
        DateTime date, string dateSource, long? replaces)
    {
        var result = new ImportResult { File = path, Message = replaces == null ? "Загружен" : "Заменён (выгружен заново)",
            Replaced = replaces != null, Block = place.Block, Day = date.Date, AllTestsNew = true };
        var reportDate = Database.Time(date);
        var now = Database.Time(DateTime.Now);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        // выгрузили заново в тот же прогон: сначала отменить всё, что принёс прежний вариант файла
        var pending = replaces == null ? new HashSet<long>() : UndoReport(c, tx, replaces.Value);

        var reportId = Insert(c, tx, """
            INSERT INTO Reports(ProjectId, FilePath, FileHash, FileTime, ReportDate, DateFromReport, Units, Imported, Results, Criticality, Block, DateSource)
            VALUES($p, $f, $h, $ft, $d, $dr, $u, $i, $n, $cr, $bl, $ds)
            """, ("$p", projectId), ("$f", path), ("$h", hash), ("$ft", Database.Time(fileTime)), ("$d", reportDate),
            ("$dr", report.DateFromReport ? 1 : 0), ("$u", report.Units ?? "m"), ("$i", now), ("$n", report.Tests.Sum(t => t.Results.Count)),
            ("$cr", place.Criticality), ("$bl", place.Block), ("$ds", dateSource));

        var renamed = new SortedSet<(string Before, string After)>();
        foreach (var test in report.Tests)
        {
            // проверка — в своей критичности и своём блоке: «АР Стены/МЕР Трубы» в S1 и в S2 — разные проверки
            var (testId, lastDate, lastReportId) = EnsureTest(c, tx, projectId, test, place);
            Exec(c, tx, "INSERT OR IGNORE INTO ReportTests(ReportId, TestId) VALUES($r, $t)", ("$r", reportId), ("$t", testId));
            // отчёт старше уже загруженного по этой проверке: только дополняет историю, статусы не трогает
            var latest = lastDate == null || string.CompareOrdinal(reportDate, lastDate) >= 0;
            // проверка впервые на сайте: её коллизии — не «новые», их впервые проверили
            var firstForTest = lastDate == null;
            if (firstForTest) result.NewTests++;
            else result.AllTestsNew = false;
            if (!latest) result.Older = true;
            var known = KnownClashes(c, tx, testId);
            var dateText = date.ToString(dateSource == ReportPath.DateFromFolder ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm");
            // неполный отчёт (выгружены не все статусы): пропавшие не считаем исправленными — DECISIONS.md, «Неполный отчёт»
            var complete = ReportMerge.IsComplete(test);
            if (!complete)
            {
                var warning = $"«{test.Name}»: отчёт неполный — в проверке {test.Summary.Total} коллизий, в отчёте {test.ResultsInFile}. "
                              + "Пропавшие из отчёта не считаются исправленными. Выгрузите отчёт со всеми статусами.";
                result.Warning = result.Warning == null ? warning : result.Warning + " " + warning;
                _log.LogWarning("{File}: {Warning}", path, warning);
            }

            var steps = ReportMerge.Plan(test.Name, known, test.Results, complete);
            // предохранитель: пропало подозрительно много — предупреждение и «Вернуть активными» в «Отчётах» (DECISIONS.md, «Предохранитель»)
            var activeBefore = known.Count(k => k.State == ClashState.Active && !pending.Contains(k.Id));
            var vanished = steps.Count(s => s.Action == MergeAction.Gone && s.StateChanged && !pending.Contains(s.Known.Id));
            if (latest && ReportMerge.TooManyGone(activeBefore, vanished))
            {
                var warning = $"«{test.Name}»: исправлены сразу {vanished} из {activeBefore} активных. Если в Navisworks не подгрузилась модель — "
                              + "«Вернуть активными» у этого отчёта.";
                result.Warning = result.Warning == null ? warning : result.Warning + " " + warning;
                result.Suspicious = true;
                _log.LogWarning("{File}: {Warning}", path, warning);
            }
            foreach (var step in steps)
            {
                // коллизия, найденная прежним вариантом отчёта: есть в новом — та же коллизия, найдена этим отчётом
                // (история и комментарии сохраняются); нет — удалится после загрузки (FinishReplace)
                if (step.Known != null && pending.Contains(step.Known.Id))
                {
                    if (step.Result == null) continue;
                    var state = ManualState(c, tx, step.Known.Id)
                                ?? (step.Present ? ClashRules.StateOf(step.Result.Status) : ClashState.Fixed);
                    Exec(c, tx, "UPDATE Clashes SET State = $s, InLastReport = $in, FirstSeen = $d, LastSeen = $d WHERE Id = $id",
                        ("$s", (int)state), ("$in", step.Present ? 1 : 0), ("$d", reportDate), ("$id", step.Known.Id));
                    AddOccurrence(c, tx, step.Known.Id, reportId, report, step.Result, step.Points, makeLast: true, reportDate);
                    Exec(c, tx, "UPDATE Events SET ReportId = $r WHERE ClashId = $id AND Kind = 'found' AND ReportId = $old",
                        ("$r", reportId), ("$id", step.Known.Id), ("$old", replaces));
                    if (firstForTest) result.NewTestClashes++;
                    else result.New++;
                    continue;
                }
                if (!latest)
                {
                    if (step.Result == null) continue;
                    var cid = step.Known?.Id ?? NewClash(c, tx, projectId, testId, step.IdentityKey,
                        step.Present ? ClashState.Fixed : step.NewState, inLastReport: false, reportDate, reportId, "Найдена в более раннем отчёте от " + dateText);
                    // была в более раннем отчёте, а в более позднем её нет — исправлена к нему; в истории — почему (DECISIONS.md, «Более ранний отчёт»)
                    if (step.Known == null && step.Present)
                        ClashStore.AddEvent(c, tx, cid, now, EventKinds.Gone,
                            "Исправлена: нет в более позднем отчёте от " + Database.Time(lastDate).ToString("dd.MM.yyyy"),
                            (int)ClashState.Active, (int)ClashState.Fixed, null, lastReportId);
                    AddOccurrence(c, tx, cid, reportId, report, step.Result, step.Points, makeLast: step.Known == null, reportDate);
                    continue;
                }
                switch (step.Action)
                {
                    case MergeAction.New:
                    {
                        var cid = NewClash(c, tx, projectId, testId, step.IdentityKey, step.NewState, step.Present, reportDate, reportId,
                            "Найдена в отчёте от " + dateText);
                        AddOccurrence(c, tx, cid, reportId, report, step.Result, step.Points, makeLast: true, reportDate);
                        if (firstForTest) result.NewTestClashes++;
                        else result.New++;
                        break;
                    }
                    case MergeAction.Again:
                    case MergeAction.Reappeared:
                        AddOccurrence(c, tx, step.Known.Id, reportId, report, step.Result, step.Points, makeLast: true, reportDate);
                        Exec(c, tx, "UPDATE Clashes SET State = $s, InLastReport = 1 WHERE Id = $id", ("$s", (int)step.NewState), ("$id", step.Known.Id));
                        if (step.Returned)
                            Exec(c, tx, "UPDATE Clashes SET ReturnCount = ReturnCount + 1 WHERE Id = $id", ("$id", step.Known.Id));
                        if (step.StateChanged)
                            ClashStore.AddEvent(c, tx, step.Known.Id, now, EventKinds.Reappeared, "Вернулась после исправления: снова в отчёте от " + dateText,
                                (int)step.Known.State, (int)step.NewState, null, reportId);
                        if (step.Action == MergeAction.Again) result.Again++;
                        else result.Reappeared++;
                        break;
                    case MergeAction.Gone:
                        if (step.Result != null)
                            AddOccurrence(c, tx, step.Known.Id, reportId, report, step.Result, step.Points, makeLast: true, reportDate);
                        Exec(c, tx, "UPDATE Clashes SET State = $s, InLastReport = 0 WHERE Id = $id", ("$s", (int)step.NewState), ("$id", step.Known.Id));
                        if (step.StateChanged)
                        {
                            var why = step.Result != null ? "Navisworks пишет «Исправлено» (отчёт от " + dateText + ")" : "нет в отчёте от " + dateText;
                            ClashStore.AddEvent(c, tx, step.Known.Id, now, EventKinds.Gone, "Исправлена: " + why,
                                (int)step.Known.State, (int)step.NewState, null, reportId);
                            result.Fixed++;
                        }
                        break;
                }
            }
            // отчёт старше уже загруженных: коллизии, которых нет в следующих прогонах, исправлены к следующему после него
            if (!latest) ClashStore.RetimeFixes(c, tx, testId);
            renamed.UnionWith(RenamedModels(c, tx, reportId, testId));
            if (latest)
                Exec(c, tx, "UPDATE Tests SET LastReportId = $r, LastReportDate = $d WHERE Id = $id", ("$r", reportId), ("$d", reportDate), ("$id", testId));
        }
        if (renamed.Count > 0)
        {
            // коллизии узнаны по номерам элементов (ClashRules.IdentityKey), но имя файла модели должно быть постоянным —
            // DECISIONS.md, «Имя файла модели — не часть коллизии»
            var warning = "Модели названы иначе, чем в соседнем прогоне: " + string.Join(", ", renamed.Select(x => "здесь «" + x.After + "», в соседнем — «" + x.Before + "»"))
                          + ". Коллизии узнаны по номерам элементов. Держите имена файлов моделей постоянными (регламент выгрузки).";
            result.Warning = result.Warning == null ? warning : result.Warning + " " + warning;
            _log.LogWarning("{File}: {Warning}", path, warning);
        }
        if (result.Warning != null)
            Exec(c, tx, "UPDATE Reports SET Warning = $w, Suspicious = $s WHERE Id = $id",
                ("$w", result.Warning), ("$s", result.Suspicious ? 1 : 0), ("$id", reportId));
        var removed = replaces == null ? (0, 0) : FinishReplace(c, tx, replaces.Value, pending);
        tx.Commit();
        if (replaces != null)
        {
            // снимки прежнего варианта — после базы: не удалились — лишние файлы, а не потерянные данные
            var folder = Path.Combine(_options.ImagesFolder, replaces.Value.ToString());
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.LogWarning("Снимки заменённого отчёта не удалены: {Folder} — {Reason}", folder, ex.Message);
            }
            _log.LogInformation("Отчёт {File} заменён (прежний — {Old}): коллизий только из прежнего удалено {Removed}, комментариев у них {Comments}",
                path, replaces, removed.Item1, removed.Item2);
        }
        return result;
    }

    /// <summary>Статус, поставленный человеком (последняя смена статуса — не от загрузки); нет — null.</summary>
    private static ClashState? ManualState(SqliteConnection c, SqliteTransaction tx, long clashId)
    {
        using var cmd = ClashStore.Command(c, """
            SELECT NewState FROM Events WHERE ClashId = $id AND NewState IS NOT NULL AND Kind = 'status' ORDER BY Id DESC LIMIT 1
            """, ("$id", clashId));
        cmd.Transaction = tx;
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : (ClashState)(int)(long)v;
    }

    private static (long Id, string LastDate, long? LastReportId) EnsureTest(SqliteConnection c, SqliteTransaction tx, long projectId, ClashTest test, ReportPlace place)
    {
        using (var cmd = ClashStore.Command(c, "SELECT Id, LastReportDate, LastReportId FROM Tests WHERE ProjectId = $p AND Criticality = $cr AND Block = $bl AND Name = $n",
                   ("$p", projectId), ("$cr", place.Criticality), ("$bl", place.Block), ("$n", test.Name ?? "")))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var id = r.GetInt64(0);
                var last = r.IsDBNull(1) ? null : r.GetString(1);
                long? lastReport = r.IsDBNull(2) ? null : r.GetInt64(2);
                r.Close();
                Exec(c, tx, "UPDATE Tests SET LeftSet = COALESCE($l, LeftSet), RightSet = COALESCE($rs, RightSet) WHERE Id = $id",
                    ("$l", test.LeftSet), ("$rs", test.RightSet), ("$id", id));
                return (id, last, lastReport);
            }
        }
        var newId = Insert(c, tx, "INSERT INTO Tests(ProjectId, Criticality, Block, Name, LeftSet, RightSet) VALUES($p, $cr, $bl, $n, $l, $rs)",
            ("$p", projectId), ("$cr", place.Criticality), ("$bl", place.Block), ("$n", test.Name ?? ""), ("$l", test.LeftSet), ("$rs", test.RightSet));
        return (newId, null, null);
    }

    /// <summary>
    /// Модели, названные в этом отчёте иначе, чем в соседнем прогоне той же проверки (предыдущем; у самого раннего — в
    /// следующем): у той же коллизии тот же номер элемента, а файл модели другой.
    /// </summary>
    private static List<(string Before, string After)> RenamedModels(SqliteConnection c, SqliteTransaction tx, long reportId, long testId)
    {
        var list = new List<(string, string)>();
        using var cmd = ClashStore.Command(c, """
            SELECT DISTINCT pe.ModelFile, ne.ModelFile
            FROM Occurrences n
            JOIN Clashes cl ON cl.Id = n.ClashId AND cl.TestId = $t
            JOIN Elements ne ON ne.OccurrenceId = n.Id
            JOIN Reports rn ON rn.Id = n.ReportId
            -- соседнее появление той же коллизии: предыдущий прогон, а у самого раннего отчёта — следующий
            JOIN Occurrences p ON p.Id = COALESCE(
                (SELECT o2.Id FROM Occurrences o2 JOIN Reports r2 ON r2.Id = o2.ReportId
                 WHERE o2.ClashId = n.ClashId AND r2.ReportDate < rn.ReportDate ORDER BY r2.ReportDate DESC, o2.Id DESC LIMIT 1),
                (SELECT o2.Id FROM Occurrences o2 JOIN Reports r2 ON r2.Id = o2.ReportId
                 WHERE o2.ClashId = n.ClashId AND r2.ReportDate > rn.ReportDate ORDER BY r2.ReportDate, o2.Id LIMIT 1))
            JOIN Elements pe ON pe.OccurrenceId = p.Id AND pe.ElementId = ne.ElementId
            WHERE n.ReportId = $r AND pe.ModelFile IS NOT NULL AND ne.ModelFile IS NOT NULL AND pe.ModelFile <> ne.ModelFile COLLATE NOCASE
            """, ("$t", testId), ("$r", reportId));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Известные коллизии проверки.</summary>
    private static List<KnownClash> KnownClashes(SqliteConnection c, SqliteTransaction tx, long testId)
    {
        var list = new List<KnownClash>();
        using var cmd = ClashStore.Command(c, "SELECT Id, IdentityKey, State FROM Clashes WHERE TestId = $t", ("$t", testId));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new KnownClash { Id = r.GetInt64(0), IdentityKey = r.GetString(1), State = (ClashState)r.GetInt32(2) });
        return list;
    }

    private static long NewClash(SqliteConnection c, SqliteTransaction tx, long projectId, long testId, string key, ClashState state,
        bool inLastReport, string reportDate, long reportId, string text)
    {
        var id = Insert(c, tx, """
            INSERT INTO Clashes(ProjectId, TestId, IdentityKey, State, InLastReport, FirstSeen, LastSeen)
            VALUES($p, $t, $k, $s, $in, $d, $d)
            """, ("$p", projectId), ("$t", testId), ("$k", key), ("$s", (int)state), ("$in", inLastReport ? 1 : 0), ("$d", reportDate));
        ClashStore.AddEvent(c, tx, id, Database.Time(DateTime.Now), EventKinds.Found, text, null, (int)state, null, reportId);
        return id;
    }

    /// <summary>Появление в отчёте: первый результат пары; points — сколько всего результатов Navisworks на эту пару.</summary>
    private void AddOccurrence(SqliteConnection c, SqliteTransaction tx, long clashId, long reportId, ClashReport report, ClashResult r, int points,
        bool makeLast, string reportDate)
    {
        var image = CopyImage(reportId, report, r);
        var occId = Insert(c, tx, """
            INSERT INTO Occurrences(ClashId, ReportId, Name, NavisStatus, Image, X, Y, Z, Distance, Created, GridLocation, GroupName, Description, Points)
            VALUES($c, $r, $n, $s, $img, $x, $y, $z, $dist, $cr, $g, $gr, $desc, $pts)
            """, ("$c", clashId), ("$r", reportId), ("$n", r.Name), ("$s", (int)r.Status), ("$img", image), ("$pts", Math.Max(points, 1)),
            ("$x", r.Point?.X), ("$y", r.Point?.Y), ("$z", r.Point?.Z), ("$dist", r.Distance),
            ("$cr", r.Created.HasValue ? Database.Time(r.Created.Value) : null), ("$g", r.GridLocation), ("$gr", r.GroupName), ("$desc", r.Description));
        for (var i = 0; i < r.Elements.Count; i++)
        {
            var e = r.Elements[i];
            Exec(c, tx, """
                INSERT INTO Elements(OccurrenceId, Idx, ModelFile, Level, LevelKey, Category, Family, Type, Name, ElementId)
                VALUES($o, $i, $m, $l, $lk, $cat, $f, $t, $n, $id)
                """, ("$o", occId), ("$i", i), ("$m", e.ModelFile), ("$l", e.Level), ("$lk", ClashRules.LevelKey(e.Level)), ("$cat", e.Category),
                ("$f", e.Family), ("$t", e.Type), ("$n", e.Name), ("$id", e.ElementId));
        }
        Exec(c, tx, makeLast
                ? "UPDATE Clashes SET LastOccurrenceId = $o, LastSeen = MAX(LastSeen, $d), FirstSeen = MIN(FirstSeen, $d) WHERE Id = $id"
                : "UPDATE Clashes SET LastSeen = MAX(LastSeen, $d), FirstSeen = MIN(FirstSeen, $d) WHERE Id = $id",
            ("$o", occId), ("$d", reportDate), ("$id", clashId));
    }

    /// <summary>Снимок — в папку снимков сайта («Снимки\&lt;отчёт&gt;\cd040001.jpg»); нет снимка — null.</summary>
    private string CopyImage(long reportId, ClashReport report, ClashResult r)
    {
        var source = ClashRules.ImageFile(report, r);
        if (source == null || !File.Exists(source)) return null;
        var relative = Path.Combine(reportId.ToString(), Path.GetFileName(source));
        var target = Path.Combine(_options.ImagesFolder, relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            return relative;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log.LogWarning("Снимок не скопирован: {Source} — {Reason}", source, ex.Message);
            return null;
        }
    }

    private static long Insert(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = ClashStore.Command(c, sql + "; SELECT last_insert_rowid();", args);
        cmd.Transaction = tx;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = ClashStore.Command(c, sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}
