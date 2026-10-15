using System.Globalization;
using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>
/// База сайта — один файл SQLite на локальном диске (PRINCIPLES.md, раздел 8). Пишет только сайт.
/// Устройство базы меняется только переносом данных: новая версия — новый шаг в <see cref="Migrations"/>, старые не правятся.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(SiteOptions options)
    {
        Directory.CreateDirectory(options.Data);
        File = options.DatabaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = File,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string File { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return c;
    }

    /// <summary>
    /// Шаги устройства базы по порядку. Номер шага — версия базы (Meta.Version). Шаг, уже выпущенный, не меняется:
    /// изменение — новый шаг в конце списка.
    /// </summary>
    private static readonly string[] Migrations =
    {
        // 1 — 09.10.2026: проекты, отчёты, проверки, коллизии («личности»), их появления в отчётах, элементы, история
        """
        CREATE TABLE Projects(
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            Folder TEXT NOT NULL,
            Created TEXT NOT NULL);

        CREATE TABLE Reports(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            FilePath TEXT NOT NULL,
            FileHash TEXT NOT NULL,
            FileTime TEXT NOT NULL,
            ReportDate TEXT NOT NULL,
            DateFromReport INTEGER NOT NULL,
            Units TEXT NOT NULL,
            Imported TEXT NOT NULL,
            Results INTEGER NOT NULL);
        CREATE INDEX ReportsFile ON Reports(ProjectId, FilePath);

        CREATE TABLE Tests(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            Name TEXT NOT NULL,
            LeftSet TEXT,
            RightSet TEXT,
            LastReportId INTEGER REFERENCES Reports(Id),
            LastReportDate TEXT,
            UNIQUE(ProjectId, Name));

        CREATE TABLE Clashes(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            TestId INTEGER NOT NULL REFERENCES Tests(Id),
            IdentityKey TEXT NOT NULL,
            State INTEGER NOT NULL,
            InLastReport INTEGER NOT NULL,
            FirstSeen TEXT NOT NULL,
            LastSeen TEXT NOT NULL,
            LastOccurrenceId INTEGER,
            UNIQUE(ProjectId, IdentityKey));
        CREATE INDEX ClashesTest ON Clashes(TestId);

        CREATE TABLE Occurrences(
            Id INTEGER PRIMARY KEY,
            ClashId INTEGER NOT NULL REFERENCES Clashes(Id),
            ReportId INTEGER NOT NULL REFERENCES Reports(Id),
            Name TEXT,
            NavisStatus INTEGER NOT NULL,
            Image TEXT,
            X REAL, Y REAL, Z REAL,
            Distance REAL,
            Created TEXT,
            GridLocation TEXT,
            GroupName TEXT,
            Description TEXT);
        CREATE INDEX OccurrencesClash ON Occurrences(ClashId);

        CREATE TABLE Elements(
            OccurrenceId INTEGER NOT NULL REFERENCES Occurrences(Id),
            Idx INTEGER NOT NULL,
            ModelFile TEXT,
            Level TEXT,
            LevelKey TEXT,
            Category TEXT,
            Family TEXT,
            Type TEXT,
            Name TEXT,
            ElementId INTEGER,
            PRIMARY KEY(OccurrenceId, Idx));

        CREATE TABLE Events(
            Id INTEGER PRIMARY KEY,
            ClashId INTEGER NOT NULL REFERENCES Clashes(Id),
            Time TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Text TEXT,
            OldState INTEGER,
            NewState INTEGER,
            Author TEXT,
            ReportId INTEGER REFERENCES Reports(Id));
        CREATE INDEX EventsClash ON Events(ClashId);
        """,

        // 2 — 09.10.2026: критичность и блок у проверки (одна и та же проверка в блоках S1 и S2 — разные проверки);
        // «личность» коллизии уникальна в своей проверке, а не во всём проекте; настройки сайта (папка со всеми проектами).
        // Критичность и блок у уже загруженного — по пути отчёта (FillPlaces).
        """
        CREATE TABLE Tests_new(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            Criticality TEXT NOT NULL DEFAULT '',
            Block TEXT NOT NULL DEFAULT '',
            Name TEXT NOT NULL,
            LeftSet TEXT,
            RightSet TEXT,
            LastReportId INTEGER REFERENCES Reports(Id),
            LastReportDate TEXT,
            UNIQUE(ProjectId, Criticality, Block, Name));
        INSERT INTO Tests_new(Id, ProjectId, Name, LeftSet, RightSet, LastReportId, LastReportDate)
            SELECT Id, ProjectId, Name, LeftSet, RightSet, LastReportId, LastReportDate FROM Tests;
        DROP TABLE Tests;
        ALTER TABLE Tests_new RENAME TO Tests;

        CREATE TABLE Clashes_new(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            TestId INTEGER NOT NULL REFERENCES Tests(Id),
            IdentityKey TEXT NOT NULL,
            State INTEGER NOT NULL,
            InLastReport INTEGER NOT NULL,
            FirstSeen TEXT NOT NULL,
            LastSeen TEXT NOT NULL,
            LastOccurrenceId INTEGER,
            UNIQUE(TestId, IdentityKey));
        INSERT INTO Clashes_new SELECT Id, ProjectId, TestId, IdentityKey, State, InLastReport, FirstSeen, LastSeen, LastOccurrenceId FROM Clashes;
        DROP TABLE Clashes;
        ALTER TABLE Clashes_new RENAME TO Clashes;
        CREATE INDEX ClashesTest ON Clashes(TestId);
        CREATE INDEX ClashesProject ON Clashes(ProjectId);

        ALTER TABLE Reports ADD COLUMN Criticality TEXT NOT NULL DEFAULT '';
        ALTER TABLE Reports ADD COLUMN Block TEXT NOT NULL DEFAULT '';
        ALTER TABLE Reports ADD COLUMN DateSource TEXT NOT NULL DEFAULT 'file';
        UPDATE Reports SET DateSource = 'xml' WHERE DateFromReport = 1;

        -- настройки сайта; «папку со всеми проектами» отменили в тот же день (DECISIONS.md) — таблица пока пустая
        CREATE TABLE Settings(Key TEXT PRIMARY KEY, Value TEXT);
        """,

        // 3 — 09.10.2026: «Ждёт проверки» (ручные «Исправлена» — к ней), сколько раз коллизия возвращалась после исправления,
        // свои названия типов проверок в проекте (объединение одинаковых с опечатками), предупреждение у неполного отчёта
        """
        ALTER TABLE Clashes ADD COLUMN ReturnCount INTEGER NOT NULL DEFAULT 0;
        UPDATE Clashes SET ReturnCount = (SELECT COUNT(*) FROM Events e WHERE e.ClashId = Clashes.Id AND e.Kind = 'reappeared');
        UPDATE Clashes SET State = 3 WHERE State = 1 AND (
            SELECT e.Kind FROM Events e WHERE e.ClashId = Clashes.Id AND e.NewState IS NOT NULL ORDER BY e.Time DESC, e.Id DESC LIMIT 1) = 'status';

        CREATE TABLE TypeNames(
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            Name TEXT NOT NULL,
            TypeName TEXT NOT NULL,
            PRIMARY KEY(ProjectId, Name));

        ALTER TABLE Reports ADD COLUMN Warning TEXT;
        """,

        // 4 — 09.10.2026: у коллизий из более раннего отчёта, которых нет в более позднем, в истории не было, почему они
        // «Исправлена», — добавить запись «Исправлена: нет в более позднем отчёте от …» (только запись, статусы не меняются)
        """
        INSERT INTO Events(ClashId, Time, Kind, Text, OldState, NewState, Author, ReportId)
        SELECT c.Id, f.Time, 'gone',
               'Исправлена: нет в более позднем отчёте от ' || substr(t.LastReportDate, 9, 2) || '.' || substr(t.LastReportDate, 6, 2) || '.' || substr(t.LastReportDate, 1, 4),
               0, 1, NULL, t.LastReportId
        FROM Clashes c
        JOIN Tests t ON t.Id = c.TestId
        JOIN Events f ON f.ClashId = c.Id AND f.Kind = 'found' AND f.Text LIKE 'Найдена в более раннем отчёте%'
        WHERE c.State = 1 AND t.LastReportDate IS NOT NULL
          AND NOT EXISTS(SELECT 1 FROM Events e WHERE e.ClashId = c.Id AND e.Kind IN ('gone', 'confirmed'));
        """,

        // 5 — 13.10.2026 (DECISIONS.md, «Коллизии: пара элементов, три статуса»): статусов три — «Ждёт проверки» стала
        // «Исправлена»; у появления — сколько результатов Navisworks на пару (точек); у отчёта — «пропало подозрительно много».
        // Объединение коллизий одной пары — MergePairs (C#)
        """
        ALTER TABLE Occurrences ADD COLUMN Points INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE Reports ADD COLUMN Suspicious INTEGER NOT NULL DEFAULT 0;
        INSERT INTO Events(ClashId, Time, Kind, Text, OldState, NewState, Author, ReportId)
        SELECT Id, strftime('%Y-%m-%d %H:%M:%S', 'now', 'localtime'), 'status',
               'Статуса «Ждёт проверки» больше нет — стала «Исправлена»', NULL, 1, NULL, NULL
        FROM Clashes WHERE State = 3;
        UPDATE Clashes SET State = 1 WHERE State = 3;
        UPDATE Events SET OldState = 1 WHERE OldState = 3;
        UPDATE Events SET NewState = 1 WHERE NewState = 3;
        """,

        // 6 — 14.10.2026 (DECISIONS.md, «Более ранний отчёт, добавленный позже»): какие проверки были в отчёте — чтобы знать
        // следующий прогон проверки. У уже загруженных — по появлениям коллизий и последнему отчёту проверки (отчёт, где
        // в проверке не было ни одной коллизии, так не узнать). «Исправлена: нет в отчёте» — на следующий прогон (RetimeFixes)
        """
        CREATE TABLE ReportTests(
            ReportId INTEGER NOT NULL REFERENCES Reports(Id),
            TestId INTEGER NOT NULL REFERENCES Tests(Id),
            PRIMARY KEY(ReportId, TestId));
        CREATE INDEX ReportTestsTest ON ReportTests(TestId);
        INSERT OR IGNORE INTO ReportTests(ReportId, TestId)
        SELECT DISTINCT o.ReportId, cl.TestId FROM Occurrences o JOIN Clashes cl ON cl.Id = o.ClashId;
        INSERT OR IGNORE INTO ReportTests(ReportId, TestId)
        SELECT LastReportId, Id FROM Tests WHERE LastReportId IS NOT NULL;
        """,

        // 7 — 14.10.2026 (DECISIONS.md, «Имя файла модели — не часть коллизии»): «личность» — номера элементов без имени
        // модели. Коллизии, разошедшиеся из-за переименованной модели, — склеить (MergeRenamed, C#)
        "SELECT 1;",

        // 8 — 15.10.2026 (DECISIONS.md, «Удаление отчёта с сайта»): файлы отчётов, удалённые с сайта, — чтобы проверка папок
        // не загрузила их снова (путь + содержимое; выгрузили заново — другое содержимое, загрузится)
        """
        CREATE TABLE DeletedReports(
            Id INTEGER PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id),
            FilePath TEXT NOT NULL,
            FileHash TEXT NOT NULL,
            ReportDate TEXT NOT NULL,
            Block TEXT NOT NULL,
            Deleted TEXT NOT NULL);
        CREATE INDEX DeletedReportsFile ON DeletedReports(ProjectId, FilePath);
        """,
    };

    /// <summary>
    /// Шаг 5: коллизия — пара элементов в проверке (ClashRules.IdentityKey). Коллизии одной пары в одной проверке (раньше —
    /// отдельные результаты Navisworks: две-три точки касания одних и тех же элементов) объединяются в одну: появления и
    /// история — к самой ранней, появления в одном отчёте — одно, с числом точек; статус — «Активная», если активна хоть
    /// одна, иначе «Допущена», если допущена хоть одна, иначе «Исправлена». Ключ — пара (номер Navisworks не важен).
    /// </summary>
    private static void MergePairs(SqliteConnection c, SqliteTransaction tx)
    {
        var now = Time(DateTime.Now);
        var tests = new List<(long Id, string Name)>();
        using (var cmd = Cmd(c, tx, "SELECT Id, Name FROM Tests"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tests.Add((r.GetInt64(0), r.GetString(1)));

        foreach (var (testId, testName) in tests)
        {
            var clashes = new List<(long Id, string Key, int State, string First, string Last, int InLast, int Returns)>();
            using (var cmd = Cmd(c, tx, "SELECT Id, IdentityKey, State, FirstSeen, LastSeen, InLastReport, ReturnCount FROM Clashes WHERE TestId = $t ORDER BY FirstSeen, Id", ("$t", testId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) clashes.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
            var elements = new Dictionary<long, List<ClashElement>>();
            using (var cmd = Cmd(c, tx, """
                SELECT c.Id, e.ModelFile, e.ElementId FROM Clashes c JOIN Elements e ON e.OccurrenceId = c.LastOccurrenceId
                WHERE c.TestId = $t ORDER BY c.Id, e.Idx
                """, ("$t", testId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!elements.TryGetValue(r.GetInt64(0), out var list)) elements[r.GetInt64(0)] = list = new List<ClashElement>();
                    list.Add(new ClashElement { ModelFile = r.IsDBNull(1) ? null : r.GetString(1), ElementId = r.IsDBNull(2) ? null : r.GetInt64(2) });
                }
            string NewKey((long Id, string Key, int State, string First, string Last, int InLast, int Returns) x) =>
                elements.TryGetValue(x.Id, out var els) && ClashRules.HasFullPair(els) ? ClashRules.PairKey(testName, els) : x.Key;

            var groups = clashes.GroupBy(NewKey, StringComparer.Ordinal).ToList();
            foreach (var g in groups)
            {
                var all = g.ToList();
                var keep = all[0];
                if (all.Count == 1) continue;
                foreach (var other in all.Skip(1))
                {
                    var occs = new List<(long Id, long Report)>();
                    using (var cmd = Cmd(c, tx, "SELECT Id, ReportId FROM Occurrences WHERE ClashId = $c", ("$c", other.Id)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) occs.Add((r.GetInt64(0), r.GetInt64(1)));
                    foreach (var (occ, report) in occs)
                    {
                        object mine;
                        using (var cmd = Cmd(c, tx, "SELECT Id FROM Occurrences WHERE ClashId = $c AND ReportId = $r LIMIT 1", ("$c", keep.Id), ("$r", report)))
                            mine = cmd.ExecuteScalar();
                        if (mine == null)
                            Run(c, tx, "UPDATE Occurrences SET ClashId = $c WHERE Id = $o", ("$c", keep.Id), ("$o", occ));
                        else
                        {
                            // в этом отчёте у пары уже есть появление — одна точка больше
                            Run(c, tx, "UPDATE Occurrences SET Points = Points + (SELECT Points FROM Occurrences WHERE Id = $o) WHERE Id = $m", ("$o", occ), ("$m", mine));
                            Run(c, tx, "DELETE FROM Elements WHERE OccurrenceId = $o", ("$o", occ));
                            Run(c, tx, "DELETE FROM Occurrences WHERE Id = $o", ("$o", occ));
                        }
                    }
                    Run(c, tx, "UPDATE Events SET ClashId = $k WHERE ClashId = $c", ("$k", keep.Id), ("$c", other.Id));
                    Run(c, tx, "DELETE FROM Clashes WHERE Id = $c", ("$c", other.Id));
                }
                var state = all.Any(x => x.State == (int)ClashState.Active) ? ClashState.Active
                    : all.Any(x => x.State == (int)ClashState.Accepted) ? ClashState.Accepted : ClashState.Fixed;
                Run(c, tx, """
                    UPDATE Clashes SET State = $s, FirstSeen = $f, LastSeen = $l, InLastReport = $in, ReturnCount = $rc,
                        LastOccurrenceId = (SELECT o.Id FROM Occurrences o JOIN Reports r ON r.Id = o.ReportId WHERE o.ClashId = $id ORDER BY r.ReportDate DESC, o.Id DESC LIMIT 1)
                    WHERE Id = $id
                    """, ("$s", (int)state), ("$f", all.Min(x => x.First)), ("$l", all.Max(x => x.Last)), ("$in", all.Max(x => x.InLast)),
                    ("$rc", all.Max(x => x.Returns)), ("$id", keep.Id));
                Run(c, tx, """
                    INSERT INTO Events(ClashId, Time, Kind, Text, OldState, NewState, Author, ReportId)
                    VALUES($c, $t, 'status', $text, $old, $new, NULL, NULL)
                    """, ("$c", keep.Id), ("$t", now),
                    ("$text", $"Одна пара элементов — одна коллизия: объединены {all.Count} результата Navisworks (точки касания тех же элементов)"),
                    ("$old", keep.State == (int)state ? null : (object)keep.State), ("$new", keep.State == (int)state ? null : (object)(int)state));
            }
            // ключи — после удаления объединённых: UNIQUE(TestId, IdentityKey)
            foreach (var g in groups)
            {
                var keep = g.First();
                if (keep.Key != g.Key) Run(c, tx, "UPDATE Clashes SET IdentityKey = $k WHERE Id = $id", ("$k", g.Key), ("$id", keep.Id));
            }
        }
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Run(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Cmd(c, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Шаг 2: критичность и блок у уже загруженных отчётов и проверок — по пути отчёта внутри папки проекта.</summary>
    private static void FillPlaces(SqliteConnection c, SqliteTransaction tx)
    {
        var reports = new List<(long Id, string Folder, string Path)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT r.Id, p.Folder, r.FilePath FROM Reports r JOIN Projects p ON p.Id = r.ProjectId";
            using var r = cmd.ExecuteReader();
            while (r.Read()) reports.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }
        foreach (var (id, folder, path) in reports)
        {
            var place = Logic.ReportPath.Parse(folder, path);
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Reports SET Criticality = $c, Block = $b WHERE Id = $id";
            cmd.Parameters.AddWithValue("$c", place.Criticality);
            cmd.Parameters.AddWithValue("$b", place.Block);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        // проверка — там же, где её последний отчёт (до этого шага одна проверка не могла жить в двух блоках сразу)
        Exec(c, """
            UPDATE Tests SET
                Criticality = COALESCE((SELECT r.Criticality FROM Reports r WHERE r.Id = Tests.LastReportId), ''),
                Block = COALESCE((SELECT r.Block FROM Reports r WHERE r.Id = Tests.LastReportId), '')
            """, tx);
    }

    /// <summary>Дополнительная работа шага (по номеру шага, с 1), после его SQL — в той же транзакции.</summary>
    private static readonly Dictionary<int, Action<SqliteConnection, SqliteTransaction>> MigrationCode = new()
    {
        [2] = FillPlaces,
        [5] = MergePairs,
        [6] = RetimeAllFixes,
        [7] = MergeRenamed,
    };

    /// <summary>
    /// Шаг 7: имя файла модели больше не входит в «личность» коллизии (ClashRules.IdentityKey). Коллизии одной проверки с
    /// одинаковыми номерами элементов, которые ни разу не встретились в одном отчёте, — одна коллизия под разными именами
    /// модели («…_Р3_BIM_Alma.nwc» → «…_Р3.nwc»): склеиваются в самую раннюю. Ложные записи убираются: «Найдена» у более
    /// поздней (её не нашли — её переименовали) и «Исправлена: нет в отчёте» там, где она была под другим именем. Статус —
    /// последний по времени, но «Допущена» вручную не теряется. Встретились в одном отчёте — разные модели с одинаковыми
    /// номерами: остаются разными, с ключом по именам моделей.
    /// </summary>
    private static void MergeRenamed(SqliteConnection c, SqliteTransaction tx)
    {
        var now = Time(DateTime.Now);
        var tests = new List<(long Id, string Name)>();
        using (var cmd = Cmd(c, tx, "SELECT Id, Name FROM Tests"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tests.Add((r.GetInt64(0), r.GetString(1)));

        foreach (var (testId, testName) in tests)
        {
            var clashes = new List<(long Id, string Key, int State, string First, string Last, int InLast, int Returns)>();
            using (var cmd = Cmd(c, tx, "SELECT Id, IdentityKey, State, FirstSeen, LastSeen, InLastReport, ReturnCount FROM Clashes WHERE TestId = $t ORDER BY FirstSeen, Id", ("$t", testId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) clashes.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
            var elements = new Dictionary<long, List<ClashElement>>();
            using (var cmd = Cmd(c, tx, """
                SELECT c.Id, e.ModelFile, e.ElementId FROM Clashes c JOIN Elements e ON e.OccurrenceId = c.LastOccurrenceId
                WHERE c.TestId = $t ORDER BY c.Id, e.Idx
                """, ("$t", testId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!elements.TryGetValue(r.GetInt64(0), out var list)) elements[r.GetInt64(0)] = list = new List<ClashElement>();
                    list.Add(new ClashElement { ModelFile = r.IsDBNull(1) ? null : r.GetString(1), ElementId = r.IsDBNull(2) ? null : r.GetInt64(2) });
                }
            var reports = new Dictionary<long, HashSet<long>>();
            using (var cmd = Cmd(c, tx, "SELECT o.ClashId, o.ReportId FROM Occurrences o JOIN Clashes c ON c.Id = o.ClashId WHERE c.TestId = $t", ("$t", testId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (!reports.TryGetValue(r.GetInt64(0), out var set)) reports[r.GetInt64(0)] = set = new HashSet<long>();
                    set.Add(r.GetInt64(1));
                }

            var groups = clashes.GroupBy(x => elements.TryGetValue(x.Id, out var els) && ClashRules.HasFullPair(els) ? ClashRules.IdsKey(testName, els) : x.Key,
                StringComparer.Ordinal).ToList();
            var rekey = new List<(long Id, string Key)>();
            foreach (var g in groups)
            {
                if (!g.Key.StartsWith("ids:", StringComparison.Ordinal)) continue;
                var all = g.ToList();
                // встретились в одном отчёте — разные модели с одинаковыми номерами: остаются разными, ключ — с именами моделей
                var sets = all.Select(x => reports.TryGetValue(x.Id, out var set) ? set : new HashSet<long>()).ToList();
                var together = false;
                for (var i = 0; i < sets.Count && !together; i++)
                    for (var j = i + 1; j < sets.Count && !together; j++)
                        together = sets[i].Overlaps(sets[j]);
                if (together) continue;
                var keep = all[0];
                rekey.Add((keep.Id, g.Key));
                if (all.Count == 1) continue;

                var newest = all.OrderByDescending(x => x.Last, StringComparer.Ordinal).ThenByDescending(x => x.Id).First();
                var state = (ClashState)newest.State;
                if (state == ClashState.Active && all.Any(x => x.State == (int)ClashState.Accepted)) state = ClashState.Accepted;
                var before = elements[keep.Id].Select(e => e.ModelFile).ToList();
                var after = elements[newest.Id].Select(e => e.ModelFile).ToList();
                var renamed = string.Join(", ", before.Except(after, StringComparer.OrdinalIgnoreCase)
                    .Zip(after.Except(before, StringComparer.OrdinalIgnoreCase), (a, b) => "«" + a + "» → «" + b + "»"));
                foreach (var other in all.Skip(1))
                {
                    Run(c, tx, "UPDATE Occurrences SET ClashId = $k WHERE ClashId = $c", ("$k", keep.Id), ("$c", other.Id));
                    // её не нашли — её переименовали
                    Run(c, tx, "DELETE FROM Events WHERE ClashId = $c AND Kind = 'found'", ("$c", other.Id));
                    Run(c, tx, "UPDATE Events SET ClashId = $k WHERE ClashId = $c", ("$k", keep.Id), ("$c", other.Id));
                    Run(c, tx, "DELETE FROM Clashes WHERE Id = $c", ("$c", other.Id));
                }
                // «нет в отчёте», а она в нём есть — под другим именем модели: не исправлялась
                Run(c, tx, """
                    DELETE FROM Events WHERE ClashId = $k AND Kind = 'gone' AND Text LIKE 'Исправлена: нет в%'
                      AND ReportId IN (SELECT ReportId FROM Occurrences WHERE ClashId = $k)
                    """, ("$k", keep.Id));
                Run(c, tx, """
                    UPDATE Clashes SET State = $s, FirstSeen = $f, LastSeen = $l, InLastReport = $in, ReturnCount = $rc,
                        LastOccurrenceId = (SELECT o.Id FROM Occurrences o JOIN Reports r ON r.Id = o.ReportId WHERE o.ClashId = $id ORDER BY r.ReportDate DESC, o.Id DESC LIMIT 1)
                    WHERE Id = $id
                    """, ("$s", (int)state), ("$f", all.Min(x => x.First)), ("$l", all.Max(x => x.Last)), ("$in", all.Max(x => x.InLast)),
                    ("$rc", all.Max(x => x.Returns)), ("$id", keep.Id));
                Run(c, tx, """
                    INSERT INTO Events(ClashId, Time, Kind, Text, OldState, NewState, Author, ReportId)
                    VALUES($c, $t, 'status', $text, $old, $new, NULL, NULL)
                    """, ("$c", keep.Id), ("$t", now),
                    ("$text", "Та же коллизия под другим именем файла модели" + (renamed.Length > 0 ? " (" + renamed + ")" : "")
                              + ": объединены, ложные «Найдена» и «Исправлена» убраны"),
                    ("$old", keep.State == (int)state ? null : (object)keep.State), ("$new", keep.State == (int)state ? null : (object)(int)state));
            }
            // ключи — после удаления объединённых: UNIQUE(TestId, IdentityKey)
            foreach (var (id, key) in rekey) Run(c, tx, "UPDATE Clashes SET IdentityKey = $k WHERE Id = $id", ("$k", key), ("$id", id));
            ClashStore.RetimeFixes(c, tx, testId);
        }
    }

    /// <summary>Шаг 6: «Исправлена: нет в отчёте» у всех проверок — на следующий прогон после последнего появления.</summary>
    private static void RetimeAllFixes(SqliteConnection c, SqliteTransaction tx)
    {
        var tests = new List<long>();
        using (var cmd = Cmd(c, tx, "SELECT Id FROM Tests"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tests.Add(r.GetInt64(0));
        foreach (var t in tests) ClashStore.RetimeFixes(c, tx, t);
    }

    /// <summary>Создать базу или довести её до текущей версии. Перед переносом — копия рядом с базой.</summary>
    public void Migrate(ILogger log)
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL; CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT);");
        var version = Convert.ToInt32(Scalar(c, "SELECT Value FROM Meta WHERE Key = 'Version'") ?? "0", CultureInfo.InvariantCulture);
        if (version > Migrations.Length)
            throw new InvalidOperationException(
                $"База новее программы сайта (версия базы {version}, программа знает до {Migrations.Length}). Обновите сайт.");
        if (version == Migrations.Length) return;

        if (version > 0)
        {
            var copy = File + $".до-версии-{version + 1}-{DateTime.Now:yyyyMMdd-HHmmss}";
            using (var target = new SqliteConnection($"Data Source={copy}"))
            {
                target.Open();
                c.BackupDatabase(target);
            }
            log.LogInformation("Копия базы перед переносом: {Copy}", copy);
        }
        // таблицы пересоздаются (SQLite не умеет менять ограничения) — проверка ссылок на время переноса выключается,
        // а перед записью шага ссылки проверяются целиком
        Exec(c, "PRAGMA foreign_keys = OFF;");
        try
        {
            for (var v = version; v < Migrations.Length; v++)
            {
                using var tx = c.BeginTransaction();
                Exec(c, Migrations[v], tx);
                if (MigrationCode.TryGetValue(v + 1, out var code)) code(c, tx);
                using (var check = c.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "PRAGMA foreign_key_check;";
                    using var r = check.ExecuteReader();
                    if (r.Read()) throw new InvalidOperationException($"Перенос базы на версию {v + 1}: нарушены связи таблиц ({r.GetString(0)}). База не изменена.");
                }
                Exec(c, $"INSERT OR REPLACE INTO Meta(Key, Value) VALUES('Version', '{v + 1}')", tx);
                tx.Commit();
                log.LogInformation("База: версия {Version}", v + 1);
            }
        }
        finally
        {
            Exec(c, "PRAGMA foreign_keys = ON;");
        }
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Время в базе — местное, «yyyy-MM-dd HH:mm:ss»: сортируется как строка.</summary>
    public static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateTime Time(string s) =>
        DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
