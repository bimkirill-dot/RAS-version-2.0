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
        using (var r = Query(c, "SELECT Id, Name, Folder FROM Projects ORDER BY Name"))
            while (r.Read()) list.Add(new ProjectRow { Id = r.GetInt64(0), Name = r.GetString(1), Folder = r.GetString(2) });
        var byId = list.ToDictionary(p => p.Id);
        using (var r = Query(c, "SELECT ProjectId, State, COUNT(*) FROM Clashes GROUP BY ProjectId, State"))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p)) p.Counts.Add((ClashState)r.GetInt32(1), r.GetInt32(2));
        using (var r = Query(c, """
            SELECT c.ProjectId, t.Criticality, COUNT(*) FROM Clashes c JOIN Tests t ON t.Id = c.TestId
            WHERE c.State = 0 GROUP BY c.ProjectId, t.Criticality
            """))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p)) p.ActiveByCriticality[r.GetString(1)] = r.GetInt32(2);
        using (var r = Query(c, "SELECT ProjectId, MAX(ReportDate) FROM Reports GROUP BY ProjectId"))
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(0), out var p) && !r.IsDBNull(1)) p.LastReport = Database.Time(r.GetString(1));
        return list;
    }

    public ProjectRow Project(long id) => Projects().FirstOrDefault(p => p.Id == id);

    public long AddProject(string name, string folder)
    {
        using var c = _db.Open();
        using var cmd = Command(c, "INSERT INTO Projects(Name, Folder, Created) VALUES($n, $f, $t); SELECT last_insert_rowid();",
            ("$n", name.Trim()), ("$f", folder.Trim()), ("$t", Database.Time(DateTime.Now)));
        return (long)cmd.ExecuteScalar();
    }

    public void UpdateProject(long id, string name, string folder)
    {
        using var c = _db.Open();
        using var cmd = Command(c, "UPDATE Projects SET Name = $n, Folder = $f WHERE Id = $id", ("$n", name.Trim()), ("$f", folder.Trim()), ("$id", id));
        cmd.ExecuteNonQuery();
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
        // по ссылкам — снизу вверх: элементы → появления → история → коллизии → проверки → отчёты → проект
        const string clashes = "SELECT Id FROM Clashes WHERE ProjectId = $p";
        const string reportIds = "SELECT Id FROM Reports WHERE ProjectId = $p";
        foreach (var sql in new[]
        {
            $"DELETE FROM Elements WHERE OccurrenceId IN (SELECT Id FROM Occurrences WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds}))",
            $"DELETE FROM Occurrences WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds})",
            $"DELETE FROM Events WHERE ClashId IN ({clashes}) OR ReportId IN ({reportIds})",
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
        tx.Commit();
        return ids.Count;
    }

    // ───────────── список коллизий ─────────────

    private const string ClashSelect = """
        SELECT c.Id, o.Name, c.TestId, t.Name, c.State, c.InLastReport, c.FirstSeen, c.LastSeen,
               a.ModelFile, a.Level, a.LevelKey, a.Category, a.Family, a.Type, a.Name, a.ElementId,
               b.ModelFile, b.Level, b.LevelKey, b.Category, b.Family, b.Type, b.Name, b.ElementId,
               t.Criticality, t.Block, COALESCE(tn.TypeName, t.Name), c.ReturnCount,
               CASE WHEN c.FirstSeen = t.LastReportDate THEN 1 ELSE 0 END,
               EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind IN ('gone', 'confirmed')),
               EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.ReportId = t.LastReportId AND e.Kind = 'reappeared'),
               o.Distance, o.GridLocation, rl.Units,
               (SELECT group_concat(substr(rp.ReportDate, 1, 10)) FROM Occurrences o2 JOIN Reports rp ON rp.Id = o2.ReportId WHERE o2.ClashId = c.Id),
               (SELECT group_concat(substr(rp.ReportDate, 1, 10)) FROM Events e JOIN Reports rp ON rp.Id = e.ReportId
                WHERE e.ClashId = c.Id AND e.Kind IN ('gone', 'confirmed'))
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
                   t.Criticality, t.Block, COALESCE(tn.TypeName, t.Name), c.ReturnCount
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
        }
        tx.Commit();
        return changed;
    }

    public void AddComment(long id, string text, string author)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        AddEvent(c, tx, id, Database.Time(DateTime.Now), EventKinds.Comment, text.Trim(), null, null, author, null);
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
    }.WithRuns(Str(r, 34), Str(r, 35));

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
    internal static ClashRow FillRuns(ClashRow row, string present, string fixedIn)
    {
        var seen = (present ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        row.Repeats = seen.Count;
        row.Runs.UnionWith(seen);
        row.Runs.UnionWith((fixedIn ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        return row;
    }

    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

internal static class ClashRowRuns
{
    public static ClashRow WithRuns(this ClashRow row, string present, string fixedIn) => ClashStore.FillRuns(row, present, fixedIn);
}
