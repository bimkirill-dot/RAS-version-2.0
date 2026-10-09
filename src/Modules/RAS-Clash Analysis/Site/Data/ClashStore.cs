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

    public List<TestRow> Tests(long projectId)
    {
        using var c = _db.Open();
        var list = new List<TestRow>();
        using (var r = Query(c, "SELECT Id, Name, LeftSet, RightSet, LastReportDate, Criticality, Block FROM Tests WHERE ProjectId = $p", ("$p", projectId)))
            while (r.Read())
                list.Add(new TestRow
                {
                    Id = r.GetInt64(0), Name = r.GetString(1), LeftSet = Str(r, 2), RightSet = Str(r, 3),
                    LastReport = r.IsDBNull(4) ? null : Database.Time(r.GetString(4)),
                    Criticality = r.GetString(5), Block = r.GetString(6),
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
            SELECT Id, FilePath, ReportDate, DateFromReport, Imported, Results, Criticality, Block, DateSource
            FROM Reports WHERE ProjectId = $p ORDER BY ReportDate DESC, Id DESC
            """, ("$p", projectId));
        while (r.Read())
            list.Add(new ReportRow
            {
                Id = r.GetInt64(0), FilePath = r.GetString(1), ReportDate = Database.Time(r.GetString(2)), DateFromReport = r.GetInt32(3) != 0,
                Imported = Database.Time(r.GetString(4)), Results = r.GetInt32(5), Criticality = r.GetString(6), Block = r.GetString(7),
                DateSource = r.GetString(8),
            });
        return list;
    }

    // ───────────── список коллизий ─────────────

    private const string ClashSelect = """
        SELECT c.Id, o.Name, c.TestId, t.Name, c.State, c.InLastReport, c.FirstSeen, c.LastSeen,
               a.ModelFile, a.Level, a.LevelKey, a.Category, a.Family, a.Type, a.Name, a.ElementId,
               b.ModelFile, b.Level, b.LevelKey, b.Category, b.Family, b.Type, b.Name, b.ElementId,
               t.Criticality, t.Block
        FROM Clashes c
        JOIN Tests t ON t.Id = c.TestId
        LEFT JOIN Occurrences o ON o.Id = c.LastOccurrenceId
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
        if (f.TestName != null) { sql += " AND t.Name = $tn"; args.Add(("$tn", f.TestName)); }

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
                   t.Criticality, t.Block
            FROM Clashes c JOIN Projects p ON p.Id = c.ProjectId JOIN Tests t ON t.Id = c.TestId WHERE c.Id = $id
            """, ("$id", id)))
        {
            if (!r.Read()) return null;
            card = new ClashCard
            {
                Id = r.GetInt64(0), ProjectId = r.GetInt64(1), ProjectName = r.GetString(2), TestName = r.GetString(3),
                LeftSet = Str(r, 4), RightSet = Str(r, 5), State = (ClashState)r.GetInt32(6), InLastReport = r.GetInt32(7) != 0,
                FirstSeen = Database.Time(r.GetString(8)), LastSeen = Database.Time(r.GetString(9)),
                Criticality = r.GetString(10), Block = r.GetString(11),
            };
        }
        var byId = new Dictionary<long, OccurrenceView>();
        using (var r = Query(c, """
            SELECT o.Id, o.ReportId, rp.ReportDate, rp.FilePath, o.Name, o.NavisStatus, o.Image, o.X, o.Y, o.Z, o.Distance, o.Created,
                   o.GridLocation, o.GroupName, o.Description, rp.Units
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
                    GridLocation = Str(r, 12), GroupName = Str(r, 13), Description = Str(r, 14),
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
        Criticality = r.GetString(24), Block = r.GetString(25),
    };

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

    private static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
