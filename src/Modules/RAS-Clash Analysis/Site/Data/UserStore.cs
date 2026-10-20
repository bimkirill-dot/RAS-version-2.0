using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>Учётная запись сайта (без пароля).</summary>
public sealed class SiteUserRow
{
    public long Id { get; set; }
    public string Login { get; set; }
    public string Name { get; set; }
    /// <summary>UserStore.Admin или UserStore.Employee.</summary>
    public string Role { get; set; }
    public bool Active { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastLogin { get; set; }
    public bool IsAdmin => Role == UserStore.Admin;
}

/// <summary>
/// Учётные записи сайта (DECISIONS.md модуля, «Вход на сайт»): логин и пароль сайта, без входа сайт не открывается; две роли —
/// администратор (проекты, настройки, учётные записи) и сотрудник (статусы, комментарии). Заводит и выключает учётные записи
/// администратор; первую — на компьютере сайта. Удаления нет — только «выключена»: имя остаётся в истории.
/// </summary>
public sealed class UserStore
{
    public const string Admin = "admin", Employee = "user";

    private readonly Database _db;

    public UserStore(Database db) => _db = db;

    public static string RoleTitle(string role) => role == Admin ? "администратор" : "сотрудник";

    /// <summary>Есть ли хоть одна учётная запись (нет — сайт предлагает создать первую, администратора).</summary>
    public bool Any()
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT EXISTS(SELECT 1 FROM Users)");
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public List<SiteUserRow> List()
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT Id, Login, Name, Role, Active, Created, LastLogin FROM Users ORDER BY Active DESC, Name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        var list = new List<SiteUserRow>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public SiteUserRow Find(long id)
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT Id, Login, Name, Role, Active, Created, LastLogin FROM Users WHERE Id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Вход: логин без учёта регистра, пароль, учётная запись включена. Нет — null.</summary>
    public SiteUserRow SignIn(string login, string password)
    {
        login = (login ?? "").Trim();
        using var c = _db.Open();
        long id;
        string hash;
        bool active;
        using (var cmd = ClashStore.Command(c, "SELECT Id, PasswordHash, Active FROM Users WHERE Login = $l COLLATE NOCASE", ("$l", login)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            (id, hash, active) = (r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1);
        }
        if (!active || !Passwords.Verify(password, hash)) return null;
        using (var cmd = ClashStore.Command(c, "UPDATE Users SET LastLogin = $t WHERE Id = $id", ("$t", Database.Time(DateTime.Now)), ("$id", id)))
            cmd.ExecuteNonQuery();
        return Find(id);
    }

    /// <summary>Новая учётная запись; что не так — текст ошибки, иначе null.</summary>
    public string Create(string login, string name, string role, string password)
    {
        login = (login ?? "").Trim();
        name = (name ?? "").Trim();
        if (login.Length == 0) return "Введите логин.";
        if (login.Any(char.IsWhiteSpace)) return "В логине не должно быть пробелов.";
        if (name.Length == 0) return "Введите имя — оно будет видно в истории («кто сделал»).";
        if (role != Admin && role != Employee) return "Выберите роль.";
        var problem = Passwords.Problem(password);
        if (problem != null) return problem;
        using var c = _db.Open();
        using (var cmd = ClashStore.Command(c, "SELECT EXISTS(SELECT 1 FROM Users WHERE Login = $l COLLATE NOCASE)", ("$l", login)))
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 1) return "Такой логин уже есть.";
        using var tx = c.BeginTransaction();
        using (var cmd = ClashStore.Command(c, """
            INSERT INTO Users(Login, Name, Role, PasswordHash, Active, Created) VALUES($l, $n, $r, $h, 1, $t)
            """, ("$l", login), ("$n", name), ("$r", role), ("$h", Passwords.Hash(password)), ("$t", Database.Time(DateTime.Now))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Учётная запись добавлена: {name} ({login}), {RoleTitle(role)}");
        tx.Commit();
        return null;
    }

    /// <summary>Новый пароль (администратор — любому; сам себе — ChangeOwnPassword).</summary>
    public string SetPassword(long id, string password)
    {
        var problem = Passwords.Problem(password);
        if (problem != null) return problem;
        var user = Find(id);
        if (user == null) return "Учётная запись не найдена.";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = ClashStore.Command(c, "UPDATE Users SET PasswordHash = $h WHERE Id = $id", ("$h", Passwords.Hash(password)), ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, $"Новый пароль: {user.Name} ({user.Login})");
        tx.Commit();
        return null;
    }

    public string ChangeOwnPassword(long id, string current, string password)
    {
        using (var c = _db.Open())
        using (var cmd = ClashStore.Command(c, "SELECT PasswordHash FROM Users WHERE Id = $id", ("$id", id)))
            if (!(cmd.ExecuteScalar() is string hash) || !Passwords.Verify(current, hash)) return "Нынешний пароль введён неверно.";
        return SetPassword(id, password);
    }

    /// <summary>Включить / выключить. Себя не выключить; без последнего включённого администратора не остаться.</summary>
    public string SetActive(long id, bool active, long by)
    {
        var user = Find(id);
        if (user == null) return "Учётная запись не найдена.";
        if (!active && id == by) return "Свою учётную запись выключить нельзя.";
        if (!active && user.IsAdmin && ActiveAdmins() <= 1) return "Это последний администратор — сначала назначьте другого.";
        Update(id, "Active", active ? 1 : 0, (active ? "Учётная запись включена: " : "Учётная запись выключена: ") + $"{user.Name} ({user.Login})");
        return null;
    }

    public string SetRole(long id, string role, long by)
    {
        var user = Find(id);
        if (user == null) return "Учётная запись не найдена.";
        if (role != Admin && role != Employee) return "Неизвестная роль.";
        if (role == user.Role) return null;
        if (id == by) return "Свою роль поменять нельзя — попросите другого администратора.";
        if (user.IsAdmin && ActiveAdmins() <= 1) return "Это последний администратор — сначала назначьте другого.";
        Update(id, "Role", role, $"Роль: {user.Name} ({user.Login}) — {RoleTitle(role)}");
        return null;
    }

    private int ActiveAdmins()
    {
        using var c = _db.Open();
        using var cmd = ClashStore.Command(c, "SELECT COUNT(*) FROM Users WHERE Active = 1 AND Role = $r", ("$r", Admin));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Update(long id, string column, object value, string history)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = ClashStore.Command(c, $"UPDATE Users SET {column} = $v WHERE Id = $id", ("$v", value), ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        SiteHistory.Write(c, tx, null, SiteHistory.Settings, history);
        tx.Commit();
    }

    private static SiteUserRow Read(SqliteDataReader r) => new SiteUserRow
    {
        Id = r.GetInt64(0), Login = r.GetString(1), Name = r.GetString(2), Role = r.GetString(3), Active = r.GetInt64(4) == 1,
        Created = Database.Time(r.GetString(5)), LastLogin = r.IsDBNull(6) ? null : Database.Time(r.GetString(6)),
    };
}
