using System.Globalization;
using Microsoft.Data.Sqlite;

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
    };

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
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, Migrations[v], tx);
            Exec(c, $"INSERT OR REPLACE INTO Meta(Key, Value) VALUES('Version', '{v + 1}')", tx);
            tx.Commit();
            log.LogInformation("База: версия {Version}", v + 1);
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
