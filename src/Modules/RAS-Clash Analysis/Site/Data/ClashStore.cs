using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>Чтение и действия людей: проекты, списки, карточка, статусы, комментарии. Загрузку отчётов делает ReportImporter.</summary>
public sealed class ClashStore
{
    private readonly Database _db;

    public ClashStore(Database db) => _db = db;

    // ───────────── проекты ─────────────

    public List<ProjectRow> Projects()
    {
        using var c = _db.Open();
        var list = new List<ProjectRow>();
        using (var r = Query(c, "SELECT Id, Name, Folder, ManualBlocks, (SELECT COUNT(*) FROM Blocks b WHERE b.ProjectId = Projects.Id) FROM Projects ORDER BY Name"))
            while (r.Read())
                list.Add(new ProjectRow
                {
                    Id = r.GetInt64(0), Name = r.GetString(1), Folder = r.GetString(2), ManualBlocks = r.GetInt32(3) != 0, Blocks = r.GetInt32(4),
                });
        var byId = list.ToDictionary(p => p.Id);
        using (var r = Query(c, "SELECT ProjectId, State, COUNT(*) FROM Clashes GROUP BY ProjectId, State"))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p)) p.Counts.Add((ClashState)r.GetInt32(1), r.GetInt32(2));
        using (var r = Query(c, "SELECT ProjectId, MAX(ReportDate), COUNT(DISTINCT substr(ReportDate, 1, 10)) FROM Reports GROUP BY ProjectId"))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p))
                {
                    if (!r.IsDBNull(1)) p.LastReport = Database.Time(r.GetString(1));
                    p.Runs = r.GetInt32(2);
                }
        using (var r = Query(c, """
            SELECT c.ProjectId,
                   COALESCE(SUM(c.FirstSeen = t.LastReportDate AND substr(t.LastReportDate, 1, 10) > (SELECT substr(MIN(c0.FirstSeen), 1, 10) FROM Clashes c0 WHERE c0.TestId = t.Id)), 0),
                   COALESCE(SUM(EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind IN ('gone', 'confirmed') AND NOT EXISTS(SELECT 1 FROM Events u WHERE u.ClashId = e.ClashId AND u.ReportId = e.ReportId AND u.Kind = 'status' AND u.NewState = 0))), 0),
                   COALESCE(SUM(EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind = 'reappeared')), 0)
            FROM Clashes c JOIN Tests t ON t.Id = c.TestId GROUP BY c.ProjectId
            """))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p))
                {
                    p.LastRun.New = r.GetInt32(1);
                    p.LastRun.Fixed = r.GetInt32(2);
                    p.LastRun.Returned = r.GetInt32(3);
                }
        // проверки, впервые попавшие в последний прогон своего блока (у блока были прогоны и раньше): их коллизии — не «новые»
        using (var r = Query(c, """
            WITH firsts AS (
                SELECT t.Id, t.ProjectId, t.Block, t.Name, MIN(substr(rp.ReportDate, 1, 10)) AS First
                FROM Tests t JOIN ReportTests rt ON rt.TestId = t.Id JOIN Reports rp ON rp.Id = rt.ReportId GROUP BY t.Id),
            blocks AS (
                SELECT ProjectId, Block, MIN(substr(ReportDate, 1, 10)) AS First, MAX(substr(ReportDate, 1, 10)) AS Last
                FROM Reports GROUP BY ProjectId, Block)
            SELECT f.ProjectId, f.Block, f.Name, (SELECT COUNT(*) FROM Clashes cl WHERE cl.TestId = f.Id AND substr(cl.FirstSeen, 1, 10) = f.First)
            FROM firsts f JOIN blocks b ON b.ProjectId = f.ProjectId AND b.Block = f.Block
            WHERE f.First = b.Last AND b.First < b.Last
            """))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p))
                {
                    p.LastRun.NewTests.Add((r.GetString(1) == "" ? "" : r.GetString(1) + " · ") + r.GetString(2));
                    p.LastRun.NewTestClashes += r.GetInt32(3);
                }
        return list;
    }

    public ProjectRow Project(long id) => Projects().FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Новый проект — с блоками, заданными вручную (DECISIONS.md, «Блоки — вручную»); блоков может не быть: тогда отчёты
    /// не загружаются, пока их не добавят. blocks — проверенные (ProjectBlocks.Validate), папки — относительно папки проекта.
    /// </summary>
    public long AddProject(string name, string folder, IReadOnlyList<(string Name, string Folder)> blocks)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = Command(c, "INSERT INTO Projects(Name, Folder, Created, ManualBlocks) VALUES($n, $f, $t, 1); SELECT last_insert_rowid();",
            ("$n", name.Trim()), ("$f", folder.Trim()), ("$t", Database.Time(DateTime.Now)));
        cmd.Transaction = tx;
        var id = (long)cmd.ExecuteScalar();
        foreach (var b in blocks) InsertBlock(c, tx, id, b.Name, b.Folder);
        SiteHistory.Write(c, tx, id, SiteHistory.ProjectAdded, "Папка " + folder.Trim() + "; "
            + (blocks.Count == 0 ? "блоков пока нет" : "блоки: " + string.Join(", ", blocks.Select(b => $"{b.Name.Trim()} ({b.Folder})"))));
        tx.Commit();
        return id;
    }

    private static void InsertBlock(SqliteConnection c, SqliteTransaction tx, long projectId, string name, string folder)
    {
        using var cmd = Command(c, "INSERT INTO Blocks(ProjectId, Name, Folder, Created) VALUES($p, $n, $f, $t)",
            ("$p", projectId), ("$n", name.Trim()), ("$f", Logic.ProjectFolders.Clean(folder)), ("$t", Database.Time(DateTime.Now)));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Блоки проекта, заданные вручную, — по порядку блоков; у каждого — сколько отчётов из него загружено.</summary>
    public List<BlockRow> Blocks(long projectId)
    {
        using var c = _db.Open();
        var list = new List<BlockRow>();
        using var r = Query(c, """
            SELECT b.Id, b.Name, b.Folder, (SELECT COUNT(*) FROM Reports r WHERE r.ProjectId = b.ProjectId AND r.Block = b.Name)
            FROM Blocks b WHERE b.ProjectId = $p
            """, ("$p", projectId));
        while (r.Read()) list.Add(new BlockRow { Id = r.GetInt64(0), Name = r.GetString(1), Folder = r.GetString(2), Reports = r.GetInt32(3) });
        return list.OrderBy(b => Logic.ReportPath.BlockOrder(b.Name)).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Добавить блоки к проекту (вместе с уже заданными — без повторов и вложенных папок). Что не так — текст; всё хорошо — null.</summary>
    public string AddBlocks(long projectId, IReadOnlyList<(string Name, string Folder)> blocks)
    {
        if (blocks.Count == 0) return null;
        var all = Blocks(projectId).Select(b => (b.Name, b.Folder)).Concat(blocks).ToList();
        var problem = Logic.ProjectBlocks.Validate(all);
        if (problem != null) return problem;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var b in blocks) InsertBlock(c, tx, projectId, b.Name, b.Folder);
        SiteHistory.Write(c, tx, projectId, SiteHistory.ProjectChanged,
            (blocks.Count == 1 ? "Блок добавлен: " : "Блоки добавлены: ") + string.Join(", ", blocks.Select(b => $"{b.Name.Trim()} ({b.Folder})")));
        tx.Commit();
        return null;
    }

    /// <summary>Удалить блок — только если из него ещё ничего не загружено (загруженное не переносится). Что не так — текст; удалён — null.</summary>
    public string DeleteBlock(long projectId, long blockId)
    {
        var block = Blocks(projectId).FirstOrDefault(b => b.Id == blockId);
        if (block == null) return "Блок не найден — возможно, его уже удалили.";
        if (block.Reports > 0) return $"Из блока «{block.Name}» уже загружены отчёты — его нельзя удалить.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "DELETE FROM Blocks WHERE Id = $id AND ProjectId = $p", ("$id", blockId), ("$p", projectId)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, projectId, SiteHistory.ProjectChanged, $"Блок удалён: {block.Name} ({block.Folder})");
        tx.Commit();
        return null;
    }

    public void UpdateProject(long id, string name, string folder)
    {
        var was = Project(id);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = Command(c, "UPDATE Projects SET Name = $n, Folder = $f WHERE Id = $id", ("$n", name.Trim()), ("$f", folder.Trim()), ("$id", id));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
        var changes = new List<string>();
        if (was != null && was.Name != name.Trim()) changes.Add($"название «{was.Name}» → «{name.Trim()}»");
        if (was != null && !string.Equals(was.Folder, folder.Trim(), StringComparison.OrdinalIgnoreCase)) changes.Add($"папка {was.Folder} → {folder.Trim()}");
        if (changes.Count > 0) SiteHistory.Write(c, tx, id, SiteHistory.ProjectChanged, char.ToUpper(changes[0][0]) + string.Join("; ", changes)[1..]);
        tx.Commit();
    }

    public ProjectContents ProjectContents(long id)
    {
        using var c = _db.Open();
        int Count(string sql)
        {
            using var cmd = Command(c, sql, ("$p", id));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        return new ProjectContents
        {
            Reports = Count("SELECT COUNT(*) FROM Reports WHERE ProjectId = $p"),
            Clashes = Count("SELECT COUNT(*) FROM Clashes WHERE ProjectId = $p"),
            Comments = Count("SELECT COUNT(*) FROM Events e JOIN Clashes c ON c.Id = e.ClashId WHERE c.ProjectId = $p AND e.Kind = 'comment'"),
            ManualStates = Count("SELECT COUNT(*) FROM Events e JOIN Clashes c ON c.Id = e.ClashId WHERE c.ProjectId = $p AND e.Kind = 'status'"),
        };
    }

    /// <summary>
    /// Удалить проект из базы со всем, что к нему относится, — одной транзакцией (сорвалось — не удалено ничего).
    /// Возвращает номера его отчётов: по ним — папки снимков («Снимки\&lt;отчёт&gt;»). Копию базы делает ProjectRemoval.
    /// </summary>
    public List<long> DeleteProject(long id)
    {
        using var c = _db.Open();
        var reports = new List<long>();
        using (var r = Query(c, "SELECT Id FROM Reports WHERE ProjectId = $p", ("$p", id)))
            while (r.Read()) reports.Add(r.GetInt64(0));
        using var tx = c.BeginTransaction();
        // история сайта остаётся — с названием проекта, без ссылок (номера проекта и коллизий могут достаться новым)
        using (var keep = Command(c, "UPDATE SiteLog SET ProjectId = NULL, ClashId = NULL WHERE ProjectId = $p", ("$p", id)))
        {
            keep.Transaction = tx;
            keep.ExecuteNonQuery();
        }
        // по ссылкам — снизу вверх: элементы → появления → история → коллизии → проверки → отчёты → проект
        const string clashes = "SELECT Id FROM Clashes WHERE ProjectId = $p";
        const string reportIds = "SELECT Id FROM Reports WHERE ProjectId = $p";
        foreach (var sql in new[]
        {
            $"DELETE FROM Elements WHERE OccurrenceId IN (SELECT Id FROM Occurrences WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds}))",
            $"DELETE FROM Occurrences WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds})",
            $"DELETE FROM Events WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds})",
            $"DELETE FROM ReportTests WHERE ReportId IN ({reportIds})",
            "DELETE FROM DeletedReports WHERE ProjectId = $p",
            "DELETE FROM Blocks WHERE ProjectId = $p",
            "DELETE FROM Clashes WHERE ProjectId = $p",
            "DELETE FROM Tests WHERE ProjectId = $p",
            "DELETE FROM TypeNames WHERE ProjectId = $p",
            "DELETE FROM Reports WHERE ProjectId = $p",
            "DELETE FROM Projects WHERE Id = $p",
        })
        {
            using var cmd = Command(c, sql, ("$p", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return reports;
    }

    public List<TestRow> Tests(long projectId)
    {
        using var c = _db.Open();
        var list = new List<TestRow>();
        using (var r = Query(c, """
            SELECT t.Id, t.Name, t.LeftSet, t.RightSet, t.LastReportDate, t.Criticality, t.Block, COALESCE(tn.TypeName, t.Name)
            FROM Tests t LEFT JOIN TypeNames tn ON tn.ProjectId = t.ProjectId AND tn.Name = t.Name
            WHERE t.ProjectId = $p
            """, ("$p", projectId)))
            while (r.Read())
                list.Add(new TestRow
                {
                    Id = r.GetInt64(0), Name = r.GetString(1), LeftSet = Str(r, 2), RightSet = Str(r, 3),
                    LastReport = r.IsDBNull(4) ? null : Database.Time(r.GetString(4)),
                    Criticality = r.GetString(5), Block = r.GetString(6), Type = r.GetString(7),
                });
        var byId = list.ToDictionary(t => t.Id);
        using (var r = Query(c, "SELECT TestId, State, COUNT(*) FROM Clashes WHERE ProjectId = $p GROUP BY TestId, State", ("$p", projectId)))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var t)) t.Counts.Add((ClashState)r.GetInt32(1), r.GetInt32(2));
        return list.OrderBy(t => Logic.ReportPath.CriticalityOrder(t.Criticality)).ThenBy(t => Logic.ReportPath.BlockOrder(t.Block))
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public List<ReportRow> Reports(long projectId)
    {
        using var c = _db.Open();
        var list = new List<ReportRow>();
        using var r = Query(c, """
            SELECT Id, FilePath, ReportDate, DateFromReport, Imported, Results, Criticality, Block, DateSource, Warning, Suspicious
            FROM Reports WHERE ProjectId = $p ORDER BY ReportDate DESC, Id DESC
            """, ("$p", projectId));
        while (r.Read())
            list.Add(new ReportRow
            {
                Id = r.GetInt64(0), FilePath = r.GetString(1), ReportDate = Database.Time(r.GetString(2)), DateFromReport = r.GetInt32(3) != 0,
                Imported = Database.Time(r.GetString(4)), Results = r.GetInt32(5), Criticality = r.GetString(6), Block = r.GetString(7),
                DateSource = r.GetString(8), Warning = Str(r, 9), Suspicious = r.GetInt32(10) != 0,
            });
        r.Close();
        var byId = list.ToDictionary(x => x.Id);
        using (var d = Query(c, """
            SELECT e.ReportId,
                   COUNT(DISTINCT CASE WHEN e.Kind = 'found' AND substr(cl.FirstSeen, 1, 10) = substr(rp.ReportDate, 1, 10) AND substr(rp.ReportDate, 1, 10) > (SELECT substr(MIN(c0.FirstSeen), 1, 10) FROM Clashes c0 WHERE c0.TestId = cl.TestId) THEN e.ClashId END),
                   COUNT(DISTINCT CASE WHEN e.Kind IN ('gone', 'confirmed') AND NOT EXISTS(SELECT 1 FROM Events u WHERE u.ClashId = e.ClashId AND u.ReportId = e.ReportId AND u.Kind = 'status' AND u.NewState = 0) THEN e.ClashId END),
                   COUNT(DISTINCT CASE WHEN e.Kind = 'reappeared' THEN e.ClashId END)
            FROM Events e JOIN Reports rp ON rp.Id = e.ReportId JOIN Clashes cl ON cl.Id = e.ClashId WHERE rp.ProjectId = $p GROUP BY e.ReportId
            """, ("$p", projectId)))
            while (d.Read())
                if (byId.TryGetValue(d.GetInt64(0), out var x))
                {
                    x.Delta.New = d.GetInt32(1);
                    x.Delta.Fixed = d.GetInt32(2);
                    x.Delta.Returned = d.GetInt32(3);
                }
        // первый прогон проверки: в отчёте есть коллизии, и ни у одной её проверки нет коллизий раньше дня отчёта
        using (var f = Query(c, """
            SELECT rp.Id FROM Reports rp
            WHERE rp.ProjectId = $p AND EXISTS(SELECT 1 FROM Occurrences o WHERE o.ReportId = rp.Id)
              AND NOT EXISTS(SELECT 1 FROM Occurrences o JOIN Clashes cl ON cl.Id = o.ClashId
                             WHERE o.ReportId = rp.Id
                               AND (SELECT substr(MIN(c0.FirstSeen), 1, 10) FROM Clashes c0 WHERE c0.TestId = cl.TestId) < substr(rp.ReportDate, 1, 10))
            """, ("$p", projectId)))
            while (f.Read())
                if (byId.TryGetValue(f.GetInt64(0), out var x)) x.FirstRun = true;
        // проверки отчёта; первый ли это отчёт проверки (по дню) и сколько в нём её коллизий
        using (var t = Query(c, """
            SELECT rt.ReportId, rt.TestId,
                   NOT EXISTS(SELECT 1 FROM ReportTests r2 JOIN Reports p2 ON p2.Id = r2.ReportId
                              WHERE r2.TestId = rt.TestId AND substr(p2.ReportDate, 1, 10) < substr(rp.ReportDate, 1, 10)),
                   (SELECT COUNT(DISTINCT o.ClashId) FROM Occurrences o JOIN Clashes cl ON cl.Id = o.ClashId
                    WHERE o.ReportId = rt.ReportId AND cl.TestId = rt.TestId)
            FROM ReportTests rt JOIN Reports rp ON rp.Id = rt.ReportId WHERE rp.ProjectId = $p
            """, ("$p", projectId)))
            while (t.Read())
                if (byId.TryGetValue(t.GetInt64(0), out var x))
                {
                    x.TestIds.Add(t.GetInt64(1));
                    if (t.GetInt64(2) != 0)
                    {
                        x.NewTestIds.Add(t.GetInt64(1));
                        x.NewTestClashes += t.GetInt32(3);
                    }
                }
        return list;
    }

    /// <summary>
    /// «Вернуть активными» у отчёта, из которого пропало подозрительно много (DECISIONS.md, «Предохранитель»): коллизии,
    /// которые этот отчёт сделал «Исправлена» и которые так и остались, — снова «Активная», с записью в истории.
    /// </summary>
    public int UndoReportFixes(long reportId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var ids = new List<long>();
        using (var cmd = Command(c, """
            SELECT DISTINCT e.ClashId FROM Events e JOIN Clashes c ON c.Id = e.ClashId
            WHERE e.ReportId = $r AND e.Kind = 'gone' AND e.NewState = 1 AND c.State = 1
              AND NOT EXISTS(SELECT 1 FROM Events l WHERE l.ClashId = e.ClashId AND l.NewState IS NOT NULL AND l.Id > e.Id)
            """, ("$r", reportId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        var now = Database.Time(DateTime.Now);
        foreach (var id in ids)
        {
            using (var upd = Command(c, "UPDATE Clashes SET State = 0, InLastReport = 1 WHERE Id = $id", ("$id", id)))
            {
                upd.Transaction = tx;
                upd.ExecuteNonQuery();
            }
            AddEvent(c, tx, id, now, EventKinds.Status, "Возвращена: отчёт, из которого она пропала, выгружен неправильно («Вернуть активными»)",
                (int)ClashState.Fixed, (int)ClashState.Active, null, reportId);
        }
        using (var mark = Command(c, "UPDATE Reports SET Suspicious = 0, Warning = COALESCE(Warning, '') || $w WHERE Id = $r",
                   ("$w", $" Возвращены активными: {ids.Count}."), ("$r", reportId)))
        {
            mark.Transaction = tx;
            mark.ExecuteNonQuery();
        }
        using (var info = Command(c, "SELECT ProjectId, FilePath, Block, ReportDate FROM Reports WHERE Id = $r", ("$r", reportId)))
        {
            info.Transaction = tx;
            using var r = info.ExecuteReader();
            if (r.Read())
            {
                var (project, file, block, date) = (r.GetInt64(0), r.GetString(1), r.GetString(2), Database.Time(r.GetString(3)));
                r.Close();
                SiteHistory.Write(c, tx, project, SiteHistory.UndoFixes,
                    $"{Services.Fmt.Plural(ids.Count, "коллизия", "коллизии", "коллизий")} снова «Активная» — отчёт «{Path.GetFileName(file)}» ({SiteHistory.Run(block, date)})");
            }
        }
        tx.Commit();
        return ids.Count;
    }

    // ───────────── список коллизий ─────────────

    private const string ClashSelect = """
        SELECT c.Id, o.Name, c.TestId, t.Name, c.State, c.InLastReport, c.FirstSeen, c.LastSeen,
               a.ModelFile, a.Level, a.LevelKey, a.Category, a.Family, a.Type, a.Name, a.ElementId,
               b.ModelFile, b.Level, b.LevelKey, b.Category, b.Family, b.Type, b.Name, b.ElementId,
               t.Criticality, t.Block, COALESCE(tn.TypeName, t.Name), c.ReturnCount,
               CASE WHEN c.FirstSeen = t.LastReportDate AND substr(t.LastReportDate, 1, 10) > (SELECT substr(MIN(c0.FirstSeen), 1, 10) FROM Clashes c0 WHERE c0.TestId = t.Id) THEN 1 ELSE 0 END,
               EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind IN ('gone', 'confirmed') AND NOT EXISTS(SELECT 1 FROM Events u WHERE u.ClashId = e.ClashId AND u.ReportId = e.ReportId AND u.Kind = 'status' AND u.NewState = 0)),
               EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind = 'reappeared'),
               o.Distance, o.GridLocation, rl.Units,
               (SELECT group_concat(substr(rp.ReportDate, 1, 10)) FROM Occurrences o2 JOIN Reports rp ON rp.Id = o2.ReportId WHERE o2.ClashId = c.Id),
               (SELECT group_concat(substr(rp.ReportDate, 1, 10)) FROM Events e JOIN Reports rp ON rp.Id = e.ReportId
                WHERE e.ClashId = c.Id AND e.Kind IN ('gone', 'confirmed') AND NOT EXISTS(SELECT 1 FROM Events u WHERE u.ClashId = e.ClashId AND u.ReportId = e.ReportId AND u.Kind = 'status' AND u.NewState = 0)),
               (SELECT group_concat(substr(rp.ReportDate, 1, 10)) FROM Events e JOIN Reports rp ON rp.Id = e.ReportId
                WHERE e.ClashId = c.Id AND e.Kind = 'reappeared')
        FROM Clashes c
        JOIN Tests t ON t.Id = c.TestId
        LEFT JOIN TypeNames tn ON tn.ProjectId = c.ProjectId AND tn.Name = t.Name
        LEFT JOIN Occurrences o ON o.Id = c.LastOccurrenceId
        LEFT JOIN Reports rl ON rl.Id = o.ReportId
        LEFT JOIN Elements a ON a.OccurrenceId = o.Id AND a.Idx = 0
        LEFT JOIN Elements b ON b.OccurrenceId = o.Id AND b.Idx = 1
        """;

    /// <summary>
    /// Коллизии проекта в критичности, блоке, проверке (статус, поля и поиск — на странице, Logic.ClashFacets):
    /// по блоку, проверке, затем по номеру в имени («Конфликт2» раньше «Конфликт10»).
    /// </summary>
    public List<ClashRow> Clashes(long projectId, ClashFilter f)
    {
        var sql = ClashSelect + " WHERE c.ProjectId = $p";
        var args = new List<(string, object)> { ("$p", projectId) };
        if (f.Criticality != null) { sql += " AND t.Criticality = $cr"; args.Add(("$cr", f.Criticality)); }
        if (f.Block != null) { sql += " AND t.Block = $bl"; args.Add(("$bl", f.Block)); }
        if (f.TestType != null) { sql += " AND COALESCE(tn.TypeName, t.Name) = $tn"; args.Add(("$tn", f.TestType)); }

        using var c = _db.Open();
        var list = new List<ClashRow>();
        using (var r = Query(c, sql, args.ToArray()))
            while (r.Read()) list.Add(ReadRow(r));

        // первый прогон проверки: его коллизии — исходная точка, не «новые»
        foreach (var g in list.GroupBy(x => x.TestId))
        {
            var first = g.Min(x => x.FirstSeen).ToString("yyyy-MM-dd");
            foreach (var x in g) x.TestFirstRun = first;
        }
        return list.OrderBy(x => Logic.ReportPath.BlockOrder(x.Block)).ThenBy(x => x.TestName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => NameNumber(x.Name)).ThenBy(x => x.Name).ToList();
    }

    // ───────────── карточка ─────────────

    public ClashCard Card(long id)
    {
        using var c = _db.Open();
        ClashCard card;
        using (var r = Query(c, """
            SELECT c.Id, c.ProjectId, p.Name, t.Name, t.LeftSet, t.RightSet, c.State, c.InLastReport, c.FirstSeen, c.LastSeen,
                   t.Criticality, t.Block, COALESCE(tn.TypeName, t.Name), c.ReturnCount, t.LastReportDate,
                   CASE WHEN EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind = 'reappeared') THEN 'returned'
                        WHEN EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind IN ('gone', 'confirmed') AND NOT EXISTS(SELECT 1 FROM Events u WHERE u.ClashId = e.ClashId AND u.ReportId = e.ReportId AND u.Kind = 'status' AND u.NewState = 0)) THEN 'fixed'
                        WHEN c.FirstSeen = t.LastReportDate AND substr(t.LastReportDate, 1, 10) > (SELECT substr(MIN(c0.FirstSeen), 1, 10) FROM Clashes c0 WHERE c0.TestId = t.Id) THEN 'new' END
            FROM Clashes c JOIN Projects p ON p.Id = c.ProjectId JOIN Tests t ON t.Id = c.TestId
            LEFT JOIN TypeNames tn ON tn.ProjectId = c.ProjectId AND tn.Name = t.Name
            WHERE c.Id = $id
            """, ("$id", id)))
        {
            if (!r.Read()) return null;
            card = new ClashCard
            {
                Id = r.GetInt64(0), ProjectId = r.GetInt64(1), ProjectName = r.GetString(2), TestName = r.GetString(3),
                LeftSet = Str(r, 4), RightSet = Str(r, 5), State = (ClashState)r.GetInt32(6), InLastReport = r.GetInt32(7) != 0,
                FirstSeen = Database.Time(r.GetString(8)), LastSeen = Database.Time(r.GetString(9)),
                Criticality = r.GetString(10), Block = r.GetString(11), Type = r.GetString(12), ReturnCount = r.GetInt32(13),
                RunDay = r.IsDBNull(14) ? null : Database.Time(r.GetString(14)).Date, RunKind = Str(r, 15),
            };
        }
        var byId = new Dictionary<long, OccurrenceView>();
        using (var r = Query(c, """
            SELECT o.Id, o.ReportId, rp.ReportDate, rp.FilePath, o.Name, o.NavisStatus, o.Image, o.X, o.Y, o.Z, o.Distance, o.Created,
                   o.GridLocation, o.GroupName, o.Description, rp.Units, o.Points
            FROM Occurrences o JOIN Reports rp ON rp.Id = o.ReportId WHERE o.ClashId = $id ORDER BY rp.ReportDate DESC, o.Id DESC
            """, ("$id", id)))
            while (r.Read())
            {
                var o = new OccurrenceView
                {
                    Id = r.GetInt64(0), ReportId = r.GetInt64(1), ReportDate = Database.Time(r.GetString(2)), ReportFile = r.GetString(3),
                    Name = Str(r, 4), NavisStatus = (NavisworksStatus)r.GetInt32(5), HasImage = !r.IsDBNull(6),
                    X = Num(r, 7), Y = Num(r, 8), Z = Num(r, 9), Distance = Num(r, 10),
                    Created = r.IsDBNull(11) ? null : Database.Time(r.GetString(11)),
                    GridLocation = Str(r, 12), GroupName = Str(r, 13), Description = Str(r, 14), Points = r.GetInt32(16),
                };
                if (card.Occurrences.Count == 0) card.Units = r.GetString(15);
                card.Occurrences.Add(o);
                byId[o.Id] = o;
            }
        if (card.Last != null)
            using (var r = Query(c, "SELECT ModelFile, Level, LevelKey, Category, Family, Type, Name, ElementId FROM Elements WHERE OccurrenceId = $o ORDER BY Idx",
                       ("$o", card.Last.Id)))
                while (r.Read()) card.Last.Elements.Add(ReadElement(r, 0));
        using (var r = Query(c, "SELECT Time, Kind, Text, OldState, NewState, Author FROM Events WHERE ClashId = $id ORDER BY Time DESC, Id DESC", ("$id", id)))
            while (r.Read())
                card.Events.Add(new EventView
                {
                    Time = Database.Time(r.GetString(0)), Kind = r.GetString(1), Text = Str(r, 2),
                    OldState = r.IsDBNull(3) ? null : (ClashState)r.GetInt32(3), NewState = r.IsDBNull(4) ? null : (ClashState)r.GetInt32(4),
                    Author = Str(r, 5),
                });
        return card;
    }

    /// <summary>Снимок появления коллизии — путь относительно папки снимков; null — снимка нет.</summary>
    public string ImageOf(long occurrenceId)
    {
        using var c = _db.Open();
        using var cmd = Command(c, "SELECT Image FROM Occurrences WHERE Id = $id", ("$id", occurrenceId));
        return cmd.ExecuteScalar() as string;
    }

    // ───────────── типы проверок ─────────────

    /// <summary>
    /// Своё название типа для всех проверок с этим названием в проекте (все блоки и критичности, и будущие отчёты).
    /// Название другого типа — объединение; пусто или как у проверки — своё название снимается.
    /// </summary>
    public void SetTestType(long projectId, string testName, string typeName)
    {
        using var c = _db.Open();
        var value = typeName?.Trim();
        using var cmd = string.IsNullOrEmpty(value) || value == testName
            ? Command(c, "DELETE FROM TypeNames WHERE ProjectId = $p AND Name = $n", ("$p", projectId), ("$n", testName))
            : Command(c, "INSERT OR REPLACE INTO TypeNames(ProjectId, Name, TypeName) VALUES($p, $n, $t)", ("$p", projectId), ("$n", testName), ("$t", value));
        cmd.ExecuteNonQuery();
    }

    // ───────────── действия ─────────────

    /// <summary>Сменить статус у одной или многих коллизий; у каждой — событие в истории. Сколько изменено.</summary>
    public int SetState(IEnumerable<long> ids, ClashState state, string comment, string author)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var now = Database.Time(DateTime.Now);
        var changed = 0;
        var touched = new List<(long Id, long Project, string Name, string Block, string Test)>();
        foreach (var id in ids.Distinct())
        {
            using var get = Command(c, "SELECT State FROM Clashes WHERE Id = $id", ("$id", id));
            get.Transaction = tx;
            var old = get.ExecuteScalar();
            if (old == null || Convert.ToInt32(old) == (int)state) continue;
            using (var upd = Command(c, "UPDATE Clashes SET State = $s WHERE Id = $id", ("$s", (int)state), ("$id", id)))
            {
                upd.Transaction = tx;
                upd.ExecuteNonQuery();
            }
            AddEvent(c, tx, id, now, EventKinds.Status, NullIfEmpty(comment), Convert.ToInt32(old), (int)state, author, null);
            changed++;
            touched.Add(ClashInfo(c, tx, id));
        }
        // в историю сайта — одной строкой на проект: «Допущена» — 12 коллизий (S2 · АР Стены/МЕР Трубы)
        foreach (var g in touched.Where(x => x.Project != 0).GroupBy(x => x.Project))
        {
            var list = g.ToList();
            var what = list.Count == 1
                ? SiteHistory.Clash(list[0].Name, list[0].Block, list[0].Test)
                : Services.Fmt.Plural(list.Count, "коллизия", "коллизии", "коллизий") + " ("
                  + (list.Select(x => (x.Block, x.Test)).Distinct().Count() == 1
                      ? SiteHistory.Run(list[0].Block, null) + list[0].Test
                      : string.Join(", ", list.Select(x => x.Block).Where(b => b != "").Distinct()) + " · " + Services.Fmt.Plural(list.Select(x => x.Test).Distinct().Count(), "проверка", "проверки", "проверок"))
                  + ")";
            SiteHistory.Write(c, tx, g.Key, SiteHistory.Status,
                $"«{ClashRules.Title(state)}» — {what}" + (string.IsNullOrWhiteSpace(comment) ? "" : $": «{SiteHistory.Cut(comment)}»"),
                list.Count == 1 ? list[0].Id : null, author);
        }
        tx.Commit();
        return changed;
    }

    /// <summary>Коллизия для истории сайта: проект, имя, блок, проверка.</summary>
    private static (long Id, long Project, string Name, string Block, string Test) ClashInfo(SqliteConnection c, SqliteTransaction tx, long id)
    {
        using var cmd = Command(c, """
            SELECT cl.ProjectId, o.Name, t.Block, t.Name FROM Clashes cl JOIN Tests t ON t.Id = cl.TestId
            LEFT JOIN Occurrences o ON o.Id = cl.LastOccurrenceId WHERE cl.Id = $id
            """, ("$id", id));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (id, r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3)) : (id, 0, null, "", "");
    }

    // ───────────── категории проверок (DECISIONS.md, «Категории проверок») ─────────────

    /// <summary>Категории по порядку — с подкатегориями по порядку и проверками (по алфавиту).</summary>
    public List<CategoryRow> Categories()
    {
        using var c = _db.Open();
        var list = new List<CategoryRow>();
        using (var r = Query(c, "SELECT Id, Name FROM Categories ORDER BY Sort, Id"))
            while (r.Read()) list.Add(new CategoryRow { Id = r.GetInt64(0), Name = r.GetString(1) });
        var byId = list.ToDictionary(x => x.Id);
        var subs = new Dictionary<long, SubcategoryRow>();
        using (var r = Query(c, "SELECT Id, CategoryId, Name FROM Subcategories ORDER BY Sort, Id"))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(1), out var cat))
                {
                    var sub = new SubcategoryRow { Id = r.GetInt64(0), Name = r.GetString(2) };
                    cat.Subcategories.Add(sub);
                    subs[sub.Id] = sub;
                }
        using (var r = Query(c, "SELECT CategoryId, Key, Name, SubcategoryId FROM CategoryTests"))
            while (r.Read())
                if (!r.IsDBNull(3) && subs.TryGetValue(r.GetInt64(3), out var sub)) sub.Tests.Add((r.GetString(1), r.GetString(2)));
                else if (byId.TryGetValue(r.GetInt64(0), out var cat)) cat.Tests.Add((r.GetString(1), r.GetString(2)));
        static void Sort(List<(string Key, string Name)> t) => t.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (var cat in list)
        {
            Sort(cat.Tests);
            foreach (var sub in cat.Subcategories) Sort(sub.Tests);
        }
        return list;
    }

    /// <summary>Место проверки по её названию: ключ (TestCategories.Key) → категория и подкатегория ("" — без подкатегории).</summary>
    public Dictionary<string, (string Category, string Subcategory)> CategoryMap()
    {
        using var c = _db.Open();
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        using var r = Query(c, """
            SELECT t.Key, k.Name, COALESCE(s.Name, '') FROM CategoryTests t JOIN Categories k ON k.Id = t.CategoryId
            LEFT JOIN Subcategories s ON s.Id = t.SubcategoryId
            """);
        while (r.Read()) map[r.GetString(0)] = (r.GetString(1), r.GetString(2));
        return map;
    }

    /// <summary>Новая категория — в конец. Что не так — текст; добавлена — null.</summary>
    public string AddCategory(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Впишите название категории.";
        if (Categories().Any(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) return $"Категория «{name}» уже есть.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "INSERT INTO Categories(Name, Sort) VALUES($n, COALESCE((SELECT MAX(Sort) FROM Categories), 0) + 1)", ("$n", name)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Категория проверок добавлена: «{name}»");
        tx.Commit();
        return null;
    }

    public string RenameCategory(long id, string name)
    {
        name = (name ?? "").Trim();
        var all = Categories();
        var cat = all.FirstOrDefault(x => x.Id == id);
        if (cat == null) return "Категория не найдена — возможно, её уже удалили.";
        if (name.Length == 0) return "Впишите название категории.";
        if (name == cat.Name) return null;
        if (all.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) return $"Категория «{name}» уже есть.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "UPDATE Categories SET Name = $n WHERE Id = $id", ("$n", name), ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Категория проверок переименована: «{cat.Name}» → «{name}»");
        tx.Commit();
        return null;
    }

    /// <summary>Удалить категорию: её проверки станут «без категории».</summary>
    public void DeleteCategory(long id)
    {
        var cat = Categories().FirstOrDefault(x => x.Id == id);
        if (cat == null) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM CategoryTests WHERE CategoryId = $id", "DELETE FROM Subcategories WHERE CategoryId = $id", "DELETE FROM Categories WHERE Id = $id" })
        {
            using var cmd = Command(c, sql, ("$id", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Категория проверок удалена: «{cat.Name}» ({Services.Fmt.Plural(cat.TotalTests, "проверка", "проверки", "проверок")} — без категории)");
        tx.Commit();
    }

    /// <summary>Поднять (delta −1) или опустить (+1) категорию в порядке.</summary>
    public void MoveCategory(long id, int delta)
    {
        var list = Categories();
        var i = list.FindIndex(x => x.Id == id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        for (var k = 0; k < list.Count; k++)
        {
            using var cmd = Command(c, "UPDATE Categories SET Sort = $s WHERE Id = $id", ("$s", k + 1), ("$id", list[k].Id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Добавить проверки в категорию. Уже в этой — пропускаются; в другой категории — переносятся, только если move
    /// (иначе остаются там). Возвращает: добавлено, и какие из названий — в других категориях (название, категория).
    /// </summary>
    public (int Added, List<(string Name, string Category)> Elsewhere) AddCategoryTests(long categoryId, IEnumerable<string> names, bool move, long? subcategoryId = null)
    {
        var all = Categories();
        var cat = all.FirstOrDefault(x => x.Id == categoryId);
        if (cat == null) return (0, new List<(string, string)>());
        var target = subcategoryId == null ? null : cat.Subcategories.FirstOrDefault(x => x.Id == subcategoryId);
        if (subcategoryId != null && target == null) return (0, new List<(string, string)>());
        // где проверка сейчас: категория и подкатегория (null — без неё)
        var owner = new Dictionary<string, (CategoryRow Category, long? Sub)>(StringComparer.Ordinal);
        foreach (var x in all)
        {
            foreach (var t in x.Tests) owner[t.Key] = (x, null);
            foreach (var sub in x.Subcategories)
                foreach (var t in sub.Tests) owner[t.Key] = (x, sub.Id);
        }
        var elsewhere = new List<(string Name, string Category)>();
        var added = 0;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var raw in names)
        {
            var name = (raw ?? "").Trim();
            var key = Logic.TestCategories.Key(name);
            if (key.Length == 0) continue;
            if (owner.TryGetValue(key, out var other))
            {
                // в той же категории — просто в другую подкатегорию (или из неё): не переспрашиваем
                if (other.Category.Id == categoryId && other.Sub == subcategoryId) continue;
                if (other.Category.Id != categoryId)
                {
                    elsewhere.Add((name, other.Category.Name));
                    if (!move) continue;
                }
            }
            using var cmd = Command(c, "INSERT OR REPLACE INTO CategoryTests(Key, Name, CategoryId, SubcategoryId) VALUES($k, $n, $c, $s)",
                ("$k", key), ("$n", name), ("$c", categoryId), ("$s", subcategoryId));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
            owner[key] = (cat, subcategoryId);
            added++;
        }
        if (added > 0)
            SiteHistory.Write(c, tx, null, SiteHistory.Settings,
                $"В {(target == null ? $"категорию «{cat.Name}»" : $"подкатегорию «{cat.Name} → {target.Name}»")} — {Services.Fmt.Plural(added, "проверка", "проверки", "проверок")}"
                + (move && elsewhere.Count > 0 ? $" (перенесено из других категорий — {elsewhere.Count})" : ""));
        tx.Commit();
        return (added, elsewhere);
    }

    /// <summary>Подкатегория проверки внутри её категории (null — без подкатегории).</summary>
    public void SetTestSubcategory(string key, long? subcategoryId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, """
            UPDATE CategoryTests SET SubcategoryId = $s
            WHERE Key = $k AND ($s IS NULL OR $s IN (SELECT Id FROM Subcategories WHERE CategoryId = CategoryTests.CategoryId))
            """, ("$k", key), ("$s", subcategoryId)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        using (var info = Command(c, """
            SELECT t.Name, k.Name, s.Name FROM CategoryTests t JOIN Categories k ON k.Id = t.CategoryId LEFT JOIN Subcategories s ON s.Id = t.SubcategoryId WHERE t.Key = $k
            """, ("$k", key)))
        {
            info.Transaction = tx;
            using var r = info.ExecuteReader();
            if (r.Read())
            {
                var (name, cat, sub) = (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
                r.Close();
                SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Проверка «{name}» — " + (sub == null ? $"в «{cat}» без подкатегории" : $"в подкатегорию «{cat} → {sub}»"));
            }
        }
        tx.Commit();
    }

    /// <summary>Новая подкатегория в категории — в конец. Что не так — текст; добавлена — null.</summary>
    public string AddSubcategory(long categoryId, string name)
    {
        name = (name ?? "").Trim();
        var cat = Categories().FirstOrDefault(x => x.Id == categoryId);
        if (cat == null) return "Категория не найдена — возможно, её уже удалили.";
        if (name.Length == 0) return "Впишите название подкатегории.";
        if (cat.Subcategories.Any(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) return $"Подкатегория «{name}» в «{cat.Name}» уже есть.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "INSERT INTO Subcategories(CategoryId, Name, Sort) VALUES($c, $n, COALESCE((SELECT MAX(Sort) FROM Subcategories WHERE CategoryId = $c), 0) + 1)",
                   ("$c", categoryId), ("$n", name)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Подкатегория добавлена: «{cat.Name} → {name}»");
        tx.Commit();
        return null;
    }

    public string RenameSubcategory(long id, string name)
    {
        name = (name ?? "").Trim();
        var cat = Categories().FirstOrDefault(x => x.Subcategories.Any(s => s.Id == id));
        var sub = cat?.Subcategories.First(s => s.Id == id);
        if (sub == null) return "Подкатегория не найдена — возможно, её уже удалили.";
        if (name.Length == 0) return "Впишите название подкатегории.";
        if (name == sub.Name) return null;
        if (cat.Subcategories.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) return $"Подкатегория «{name}» в «{cat.Name}» уже есть.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "UPDATE Subcategories SET Name = $n WHERE Id = $id", ("$n", name), ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Подкатегория переименована: «{cat.Name} → {sub.Name}» → «{name}»");
        tx.Commit();
        return null;
    }

    /// <summary>Удалить подкатегорию: её проверки остаются в категории, без подкатегории.</summary>
    public void DeleteSubcategory(long id)
    {
        var cat = Categories().FirstOrDefault(x => x.Subcategories.Any(s => s.Id == id));
        var sub = cat?.Subcategories.First(s => s.Id == id);
        if (sub == null) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var sql in new[] { "UPDATE CategoryTests SET SubcategoryId = NULL WHERE SubcategoryId = $id", "DELETE FROM Subcategories WHERE Id = $id" })
        {
            using var cmd = Command(c, sql, ("$id", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Подкатегория удалена: «{cat.Name} → {sub.Name}» ({Services.Fmt.Plural(sub.Tests.Count, "проверка", "проверки", "проверок")} — в категории без подкатегории)");
        tx.Commit();
    }

    /// <summary>Поднять (−1) или опустить (+1) подкатегорию внутри её категории.</summary>
    public void MoveSubcategory(long id, int delta)
    {
        var cat = Categories().FirstOrDefault(x => x.Subcategories.Any(s => s.Id == id));
        if (cat == null) return;
        var list = cat.Subcategories;
        var i = list.FindIndex(x => x.Id == id);
        var j = i + delta;
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        for (var k = 0; k < list.Count; k++)
        {
            using var cmd = Command(c, "UPDATE Subcategories SET Sort = $s WHERE Id = $id", ("$s", k + 1), ("$id", list[k].Id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Убрать проверку из её категории — станет «без категории».</summary>
    public void RemoveCategoryTest(string key)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        string name = null, category = null;
        using (var get = Command(c, "SELECT t.Name, k.Name FROM CategoryTests t JOIN Categories k ON k.Id = t.CategoryId WHERE t.Key = $k", ("$k", key)))
        {
            get.Transaction = tx;
            using var r = get.ExecuteReader();
            if (r.Read()) (name, category) = (r.GetString(0), r.GetString(1));
        }
        if (name == null) return;
        using (var cmd = Command(c, "DELETE FROM CategoryTests WHERE Key = $k", ("$k", key)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Из категории «{category}» убрана проверка «{name}»");
        tx.Commit();
    }

    /// <summary>Проверки, которые есть на сайте (в любом проекте), но не входят ни в одну категорию, — по алфавиту.</summary>
    public List<string> UncategorizedTests()
    {
        var map = CategoryMap();
        using var c = _db.Open();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var r = Query(c, "SELECT DISTINCT Name FROM Tests"))
            while (r.Read())
            {
                var name = r.GetString(0);
                var key = Logic.TestCategories.Key(name);
                if (key.Length > 0 && !map.ContainsKey(key) && !names.ContainsKey(key)) names[key] = name;
            }
        return names.Values.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>История сайта: всё или по проекту, новые сверху; limit — сколько последних.</summary>
    public List<HistoryRow> History(long? projectId, int limit)
    {
        using var c = _db.Open();
        var list = new List<HistoryRow>();
        using var r = Query(c, """
            SELECT s.Id, s.Time, p.Id, COALESCE(p.Name, s.ProjectName), s.Kind, s.Text, cl.Id, s.Author
            FROM SiteLog s
            LEFT JOIN Projects p ON p.Id = s.ProjectId
            LEFT JOIN Clashes cl ON cl.Id = s.ClashId
            WHERE $p IS NULL OR s.ProjectId = $p
            ORDER BY s.Id DESC LIMIT $n
            """, ("$p", projectId), ("$n", limit));
        while (r.Read())
            list.Add(new HistoryRow
            {
                Id = r.GetInt64(0), Time = Database.Time(r.GetString(1)), ProjectId = r.IsDBNull(2) ? null : r.GetInt64(2),
                ProjectName = Str(r, 3), Kind = r.GetString(4), Text = r.GetString(5), ClashId = r.IsDBNull(6) ? null : r.GetInt64(6),
                Author = Str(r, 7),
            });
        return list;
    }

    public void AddComment(long id, string text, string author)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        AddEvent(c, tx, id, Database.Time(DateTime.Now), EventKinds.Comment, text.Trim(), null, null, author, null);
        var info = ClashInfo(c, tx, id);
        if (info.Project != 0)
            SiteHistory.Write(c, tx, info.Project, SiteHistory.Comment, SiteHistory.Clash(info.Name, info.Block, info.Test) + ": «" + SiteHistory.Cut(text) + "»", id, author);
        tx.Commit();
    }

    internal static void AddEvent(SqliteConnection c, SqliteTransaction tx, long clashId, string time, string kind, string text,
        int? oldState, int? newState, string author, long? reportId)
    {
        using var cmd = Command(c, """
            INSERT INTO Events(ClashId, Time, Kind, Text, OldState, NewState, Author, ReportId)
            VALUES($c, $t, $k, $x, $o, $n, $a, $r)
            """, ("$c", clashId), ("$t", time), ("$k", kind), ("$x", text), ("$o", oldState), ("$n", newState), ("$a", author), ("$r", reportId));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// «Исправлена: нет в отчёте» — на первый отчёт проверки после последнего появления коллизии (DECISIONS.md, «Более
    /// ранний отчёт, добавленный позже»): отчёт, загруженный не по порядку, меняет прогон, к которому её не стало. Только
    /// у коллизий, которые сейчас «Исправлена» по отсутствию в отчёте (последняя смена статуса — она). Возвращает: сколько перенесено.
    /// </summary>
    internal static int RetimeFixes(SqliteConnection c, SqliteTransaction tx, long testId)
    {
        var moves = new List<(long Event, long Report, string Date)>();
        using (var cmd = Command(c, """
            SELECT g.Id, n.Id, n.ReportDate
            FROM Clashes cl
            JOIN Events g ON g.Id = (SELECT e.Id FROM Events e WHERE e.ClashId = cl.Id AND e.NewState IS NOT NULL ORDER BY e.Id DESC LIMIT 1)
            JOIN Reports n ON n.Id = (SELECT rp.Id FROM ReportTests rt JOIN Reports rp ON rp.Id = rt.ReportId
                                      WHERE rt.TestId = cl.TestId AND rp.ReportDate > cl.LastSeen ORDER BY rp.ReportDate, rp.Id LIMIT 1)
            WHERE cl.TestId = $t AND cl.State = 1 AND g.Kind = 'gone' AND g.ReportId IS NOT n.Id
              AND NOT EXISTS(SELECT 1 FROM Occurrences o WHERE o.ClashId = cl.Id AND o.ReportId = g.ReportId)
            """, ("$t", testId)))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) moves.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
        }
        foreach (var (ev, report, date) in moves)
        {
            using var cmd = Command(c, "UPDATE Events SET ReportId = $r, Text = $x WHERE Id = $id",
                ("$r", report), ("$x", "Исправлена: нет в отчёте от " + Database.Time(date).ToString("dd.MM.yyyy")), ("$id", ev));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        return moves.Count;
    }

    // ───────────── служебное ─────────────

    internal static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static SqliteDataReader Query(SqliteConnection c, string sql, params (string Name, object Value)[] args) =>
        Command(c, sql, args).ExecuteReader();

    private static ClashRow ReadRow(SqliteDataReader r) => new ClashRow
    {
        Id = r.GetInt64(0), Name = Str(r, 1), TestId = r.GetInt64(2), TestName = r.GetString(3), State = (ClashState)r.GetInt32(4),
        InLastReport = r.GetInt32(5) != 0, FirstSeen = Database.Time(r.GetString(6)), LastSeen = Database.Time(r.GetString(7)),
        A = r.IsDBNull(8) && r.IsDBNull(15) ? null : ReadElement(r, 8),
        B = r.IsDBNull(16) && r.IsDBNull(23) ? null : ReadElement(r, 16),
        Criticality = r.GetString(24), Block = r.GetString(25), Type = r.GetString(26), ReturnCount = r.GetInt32(27),
        NewInLastRun = r.GetInt32(28) != 0, FixedInLastRun = r.GetInt32(29) != 0, ReturnedInLastRun = r.GetInt32(30) != 0,
        Distance = Num(r, 31), GridLocation = Str(r, 32), Units = Str(r, 33) ?? "m",
    }.WithRuns(Str(r, 34), Str(r, 35), Str(r, 36));

    private static ElementView ReadElement(SqliteDataReader r, int i) => new ElementView
    {
        ModelFile = Str(r, i), Level = Str(r, i + 1), LevelKey = Str(r, i + 2), Category = Str(r, i + 3), Family = Str(r, i + 4),
        Type = Str(r, i + 5), Name = Str(r, i + 6), ElementId = r.IsDBNull(i + 7) ? null : r.GetInt64(i + 7),
    };

    private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.CultureInvariant);

    private static long NameNumber(string name)
    {
        var m = name == null ? null : Digits.Match(name);
        return m != null && m.Success && long.TryParse(m.Value, out var n) ? n : long.MaxValue;
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Прогоны коллизии: дни отчётов, где она была, и где её исправили (пропала).</summary>
    internal static ClashRow FillRuns(ClashRow row, string present, string fixedIn, string returnedIn)
    {
        var seen = (present ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        row.Repeats = seen.Count;
        row.Runs.UnionWith(seen);
        row.FixedOn.UnionWith((fixedIn ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        row.ReturnedOn.UnionWith((returnedIn ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        row.Runs.UnionWith(row.FixedOn);
        return row;
    }

    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

internal static class ClashRowRuns
{
    public static ClashRow WithRuns(this ClashRow row, string present, string fixedIn, string returnedIn) =>
        ClashStore.FillRuns(row, present, fixedIn, returnedIn);
}
