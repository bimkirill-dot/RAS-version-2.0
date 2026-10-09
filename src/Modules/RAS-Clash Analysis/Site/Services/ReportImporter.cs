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
    public int Reappeared { get; set; }
    public int Fixed { get; set; }
    /// <summary>Узнаны по паре элементов: номер Navisworks сменился.</summary>
    public int Rekeyed { get; set; }
    /// <summary>Не отчёт, а новый проект: папка появилась в папке со всеми проектами.</summary>
    public bool NewProject { get; set; }
}

/// <summary>
/// Загрузка отчётов из папок проектов (DECISIONS.md модуля: проект — папка с отчётами; проверка — из отчёта; дата — из XML).
/// Отчёт — XML в папке проекта и её подпапках (кроме папок снимков «…_files»). Тот же файл с тем же содержимым второй раз
/// не загружается; Navisworks перезаписал файл — это новый отчёт. Снимки копируются к себе: в папке их перезапишет следующий прогон.
/// </summary>
public sealed class ReportImporter
{
    /// <summary>Настройка сайта: папка со всеми проектами — каждая папка внутри неё с отчётами становится проектом.</summary>
    public const string ProjectsRootKey = "ProjectsRoot";

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

    private List<ImportResult> Scan(long? projectId)
    {
        var results = projectId == null ? AutoProjects() : new List<ImportResult>();
        var projects = new List<(long Id, string Name, string Folder)>();
        using (var c = _db.Open())
        using (var cmd = ClashStore.Command(c, "SELECT Id, Name, Folder FROM Projects"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (projectId == null || r.GetInt64(0) == projectId)
                    projects.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));

        foreach (var p in projects)
        {
            if (!Directory.Exists(p.Folder))
            {
                results.Add(new ImportResult { ProjectName = p.Name, File = p.Folder, Error = true, Message = "Папка отчётов не найдена или недоступна." });
                continue;
            }
            var candidates = new List<(string Path, string Hash, DateTime FileTime, ClashReport Report, ReportPlace Place, DateTime Date, string DateSource)>();
            foreach (var file in ReportFiles(p.Folder))
            {
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
                    // критичность, блок и дата прогона — из названий папок (ReportPath)
                    var place = ReportPath.Parse(p.Folder, file);
                    var date = ReportPath.ReportDate(report, fileTime, place.RunDate, out var dateSource);
                    candidates.Add((file, hash, fileTime, report, place, date, dateSource));
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
            // по дате отчёта: история коллизий складывается в том порядке, в каком шли прогоны
            foreach (var x in candidates.OrderBy(x => x.Date).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var result = Import(p.Id, x.Path, x.Hash, x.FileTime, x.Report, x.Place, x.Date, x.DateSource);
                    result.ProjectName = p.Name;
                    results.Add(result);
                    _log.LogInformation("Загружен отчёт {File}: новых {New}, снова {Again}, появились снова {Reappeared}, исправлены {Fixed}, узнаны по паре элементов {Rekeyed}",
                        x.Path, result.New, result.Again, result.Reappeared, result.Fixed, result.Rekeyed);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Не удалось загрузить отчёт {File}", x.Path);
                    results.Add(new ImportResult { ProjectName = p.Name, File = x.Path, Error = true, Message = "Не удалось загрузить: " + ex.Message });
                }
            }
        }
        return results;
    }

    private static IEnumerable<string> ReportFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.xml", Deep)
            .Where(f => !Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar)
                .Any(d => d.EndsWith("_files", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Проекты из папки со всеми проектами: каждая папка внутри неё, где есть отчёты XML, — проект с тем же названием
    /// (название можно поменять на сайте, связь — по папке). Проекты не удаляются, если папка пропала.
    /// </summary>
    private List<ImportResult> AutoProjects()
    {
        var results = new List<ImportResult>();
        string root;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = _db.Open())
        {
            using (var cmd = ClashStore.Command(c, "SELECT Value FROM Settings WHERE Key = $k", ("$k", ProjectsRootKey)))
                root = cmd.ExecuteScalar() as string;
            using (var cmd = ClashStore.Command(c, "SELECT Folder FROM Projects"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) known.Add(Normalize(r.GetString(0)));
        }
        if (string.IsNullOrWhiteSpace(root)) return results;
        if (!Directory.Exists(root))
        {
            results.Add(new ImportResult { ProjectName = "Папка со всеми проектами", File = root, Error = true, Message = "Не найдена или недоступна." });
            return results;
        }
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            results.Add(new ImportResult { ProjectName = "Папка со всеми проектами", File = root, Error = true, Message = ex.Message });
            return results;
        }
        foreach (var folder in folders)
        {
            if (known.Contains(Normalize(folder))) continue;
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal)) continue;
            if (!ReportFiles(folder).Any()) continue;
            using var c = _db.Open();
            using var cmd = ClashStore.Command(c, "INSERT INTO Projects(Name, Folder, Created) VALUES($n, $f, $t)",
                ("$n", name), ("$f", folder), ("$t", Database.Time(DateTime.Now)));
            cmd.ExecuteNonQuery();
            _log.LogInformation("Новый проект из папки со всеми проектами: {Name} ({Folder})", name, folder);
            results.Add(new ImportResult { ProjectName = name, File = folder, NewProject = true, Message = "Новый проект" });
        }
        return results;
    }

    private static string Normalize(string folder) => Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/');

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

    private ImportResult Import(long projectId, string path, string hash, DateTime fileTime, ClashReport report, ReportPlace place,
        DateTime date, string dateSource)
    {
        var result = new ImportResult { File = path, Message = "Загружен" };
        var reportDate = Database.Time(date);
        var now = Database.Time(DateTime.Now);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();

        var reportId = Insert(c, tx, """
            INSERT INTO Reports(ProjectId, FilePath, FileHash, FileTime, ReportDate, DateFromReport, Units, Imported, Results, Criticality, Block, DateSource)
            VALUES($p, $f, $h, $ft, $d, $dr, $u, $i, $n, $cr, $bl, $ds)
            """, ("$p", projectId), ("$f", path), ("$h", hash), ("$ft", Database.Time(fileTime)), ("$d", reportDate),
            ("$dr", report.DateFromReport ? 1 : 0), ("$u", report.Units ?? "m"), ("$i", now), ("$n", report.Tests.Sum(t => t.Results.Count)),
            ("$cr", place.Criticality), ("$bl", place.Block), ("$ds", dateSource));

        foreach (var test in report.Tests)
        {
            // проверка — в своей критичности и своём блоке: «АР Стены/МЕР Трубы» в S1 и в S2 — разные проверки
            var (testId, lastDate) = EnsureTest(c, tx, projectId, test, place);
            // отчёт старше уже загруженного по этой проверке: только дополняет историю, статусы не трогает
            var latest = lastDate == null || string.CompareOrdinal(reportDate, lastDate) >= 0;
            var known = KnownClashes(c, tx, testId, test.Name);
            var dateText = date.ToString(dateSource == ReportPath.DateFromFolder ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm");

            foreach (var step in ReportMerge.Plan(test.Name, known, test.Results))
            {
                if (!latest)
                {
                    if (step.Result == null) continue;
                    var cid = step.Known?.Id ?? NewClash(c, tx, projectId, testId, step.IdentityKey,
                        step.Present ? ClashState.Fixed : step.NewState, inLastReport: false, reportDate, reportId, "Найдена в более раннем отчёте от " + dateText);
                    AddOccurrence(c, tx, cid, reportId, report, step.Result, makeLast: step.Known == null, reportDate);
                    continue;
                }
                if (step.Rekeyed)
                {
                    // номер Navisworks сменился (проверку пересоздали / новый файл Navisworks) — та же коллизия, новый ключ
                    Exec(c, tx, "UPDATE Clashes SET IdentityKey = $k WHERE Id = $id", ("$k", step.IdentityKey), ("$id", step.Known.Id));
                    ClashStore.AddEvent(c, tx, step.Known.Id, now, EventKinds.Rekey,
                        "Номер Navisworks сменился — узнана по паре элементов (отчёт от " + dateText + ")", null, null, null, reportId);
                    result.Rekeyed++;
                }
                switch (step.Action)
                {
                    case MergeAction.New:
                    {
                        var cid = NewClash(c, tx, projectId, testId, step.IdentityKey, step.NewState, step.Present, reportDate, reportId,
                            "Найдена в отчёте от " + dateText);
                        AddOccurrence(c, tx, cid, reportId, report, step.Result, makeLast: true, reportDate);
                        result.New++;
                        break;
                    }
                    case MergeAction.Again:
                    case MergeAction.Reappeared:
                        AddOccurrence(c, tx, step.Known.Id, reportId, report, step.Result, makeLast: true, reportDate);
                        Exec(c, tx, "UPDATE Clashes SET State = $s, InLastReport = 1 WHERE Id = $id", ("$s", (int)step.NewState), ("$id", step.Known.Id));
                        if (step.StateChanged)
                            ClashStore.AddEvent(c, tx, step.Known.Id, now, EventKinds.Reappeared, "Снова в отчёте от " + dateText,
                                (int)step.Known.State, (int)step.NewState, null, reportId);
                        if (step.Action == MergeAction.Again) result.Again++;
                        else result.Reappeared++;
                        break;
                    case MergeAction.Gone:
                        if (step.Result != null)
                            AddOccurrence(c, tx, step.Known.Id, reportId, report, step.Result, makeLast: true, reportDate);
                        Exec(c, tx, "UPDATE Clashes SET State = $s, InLastReport = 0 WHERE Id = $id", ("$s", (int)step.NewState), ("$id", step.Known.Id));
                        if (step.StateChanged)
                        {
                            ClashStore.AddEvent(c, tx, step.Known.Id, now, EventKinds.Gone,
                                step.Result != null ? "Исправлена в Navisworks (отчёт от " + dateText + ")" : "Нет в отчёте от " + dateText,
                                (int)step.Known.State, (int)step.NewState, null, reportId);
                            result.Fixed++;
                        }
                        break;
                }
            }
            if (latest)
                Exec(c, tx, "UPDATE Tests SET LastReportId = $r, LastReportDate = $d WHERE Id = $id", ("$r", reportId), ("$d", reportDate), ("$id", testId));
        }
        tx.Commit();
        return result;
    }

    private static (long Id, string LastDate) EnsureTest(SqliteConnection c, SqliteTransaction tx, long projectId, ClashTest test, ReportPlace place)
    {
        using (var cmd = ClashStore.Command(c, "SELECT Id, LastReportDate FROM Tests WHERE ProjectId = $p AND Criticality = $cr AND Block = $bl AND Name = $n",
                   ("$p", projectId), ("$cr", place.Criticality), ("$bl", place.Block), ("$n", test.Name ?? "")))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var id = r.GetInt64(0);
                var last = r.IsDBNull(1) ? null : r.GetString(1);
                r.Close();
                Exec(c, tx, "UPDATE Tests SET LeftSet = COALESCE($l, LeftSet), RightSet = COALESCE($rs, RightSet) WHERE Id = $id",
                    ("$l", test.LeftSet), ("$rs", test.RightSet), ("$id", id));
                return (id, last);
            }
        }
        var newId = Insert(c, tx, "INSERT INTO Tests(ProjectId, Criticality, Block, Name, LeftSet, RightSet) VALUES($p, $cr, $bl, $n, $l, $rs)",
            ("$p", projectId), ("$cr", place.Criticality), ("$bl", place.Block), ("$n", test.Name ?? ""), ("$l", test.LeftSet), ("$rs", test.RightSet));
        return (newId, null);
    }

    /// <summary>Известные коллизии проверки — с парой элементов последнего появления (узнавание при смене номера Navisworks).</summary>
    private static List<KnownClash> KnownClashes(SqliteConnection c, SqliteTransaction tx, long testId, string testName)
    {
        var byId = new Dictionary<long, KnownClash>();
        using (var cmd = ClashStore.Command(c, "SELECT Id, IdentityKey, State FROM Clashes WHERE TestId = $t", ("$t", testId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) byId[r.GetInt64(0)] = new KnownClash { Id = r.GetInt64(0), IdentityKey = r.GetString(1), State = (ClashState)r.GetInt32(2) };
        }
        var elements = new Dictionary<long, List<ClashElement>>();
        using (var cmd = ClashStore.Command(c, """
            SELECT c.Id, e.ModelFile, e.ElementId FROM Clashes c JOIN Elements e ON e.OccurrenceId = c.LastOccurrenceId
            WHERE c.TestId = $t ORDER BY c.Id, e.Idx
            """, ("$t", testId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!elements.TryGetValue(r.GetInt64(0), out var list)) elements[r.GetInt64(0)] = list = new List<ClashElement>();
                list.Add(new ClashElement { ModelFile = r.IsDBNull(1) ? null : r.GetString(1), ElementId = r.IsDBNull(2) ? null : r.GetInt64(2) });
            }
        }
        foreach (var (id, list) in elements)
            if (byId.TryGetValue(id, out var k) && ClashRules.HasFullPair(list))
                k.PairKey = ClashRules.PairKey(testName, list);
        return byId.Values.ToList();
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

    private void AddOccurrence(SqliteConnection c, SqliteTransaction tx, long clashId, long reportId, ClashReport report, ClashResult r,
        bool makeLast, string reportDate)
    {
        var image = CopyImage(reportId, report, r);
        var occId = Insert(c, tx, """
            INSERT INTO Occurrences(ClashId, ReportId, Name, NavisStatus, Image, X, Y, Z, Distance, Created, GridLocation, GroupName, Description)
            VALUES($c, $r, $n, $s, $img, $x, $y, $z, $dist, $cr, $g, $gr, $desc)
            """, ("$c", clashId), ("$r", reportId), ("$n", r.Name), ("$s", (int)r.Status), ("$img", image),
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
