using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Site.Services;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>
/// История сайта (DECISIONS.md модуля, «История сайта»): что и когда сделано — загрузка, замена и удаление отчётов,
/// статусы и комментарии, проекты, настройки, обновления сайта. «Кто» — столбец Author: пока пустой (учётных записей нет),
/// с ними — Windows-логин. Проверки папки, которые ничего не изменили, не записываются.
/// </summary>
public static class SiteHistory
{
    public const string Import = "import";
    public const string Replace = "replace";
    public const string Remove = "remove";
    public const string Refused = "refused";
    public const string Warning = "warning";
    public const string Status = "status";
    public const string Comment = "comment";
    public const string UndoFixes = "undo-fixes";
    public const string ProjectAdded = "project-add";
    public const string ProjectChanged = "project-edit";
    public const string ProjectDeleted = "project-delete";
    public const string Settings = "settings";
    public const string SiteUpdated = "site-update";

    /// <summary>Разделы для отбора: что к чему относится.</summary>
    public static readonly string[] Groups = { "Отчёты", "Коллизии", "Проекты", "Сайт" };

    public static string Group(string kind) => kind switch
    {
        Import or Replace or Remove or Refused or Warning => "Отчёты",
        Status or Comment or UndoFixes => "Коллизии",
        ProjectAdded or ProjectChanged or ProjectDeleted => "Проекты",
        _ => "Сайт",
    };

    public static string Title(string kind) => kind switch
    {
        Import => "Загрузка отчётов",
        Replace => "Замена отчёта",
        Remove => "Удаление отчётов",
        Refused => "Не выполнено",
        Warning => "Предупреждение",
        Status => "Статус",
        Comment => "Комментарий",
        UndoFixes => "Вернуть активными",
        ProjectAdded => "Проект добавлен",
        ProjectChanged => "Проект изменён",
        ProjectDeleted => "Проект удалён",
        Settings => "Настройки сайта",
        SiteUpdated => "Сайт обновлён",
        _ => kind,
    };

    /// <summary>
    /// Записать действие. Название проекта запоминается (projectName — или текущее): запись остаётся и после удаления проекта.
    /// </summary>
    public static void Write(SqliteConnection c, SqliteTransaction tx, long? projectId, string kind, string text,
        long? clashId = null, string author = null, string projectName = null)
    {
        using var cmd = ClashStore.Command(c, """
            INSERT INTO SiteLog(Time, ProjectId, ProjectName, Kind, Text, ClashId, Author)
            VALUES($t, $p, COALESCE($pn, (SELECT Name FROM Projects WHERE Id = $p)), $k, $x, $c, $a)
            """, ("$t", Database.Time(DateTime.Now)), ("$p", projectId), ("$pn", projectName), ("$k", kind), ("$x", text),
            ("$c", clashId), ("$a", author));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>То же — своей транзакцией.</summary>
    public static void Write(Database db, long? projectId, string kind, string text, long? clashId = null, string author = null, string projectName = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Write(c, tx, projectId, kind, text, clashId, author, projectName);
        tx.Commit();
    }

    /// <summary>
    /// При запуске сайта: программа новая (другая сборка, чем в последней записи «Сайт обновлён») — запись с её датой
    /// и, если базу перенесли, с версиями базы.
    /// </summary>
    public static void NoteStart(Database db, int baseBefore)
    {
        var built = File.GetLastWriteTime(typeof(SiteHistory).Assembly.Location);
        var text = "Программа сайта от " + built.ToString("dd.MM.yyyy HH:mm");
        using var c = db.Open();
        using (var cmd = ClashStore.Command(c, "SELECT Text FROM SiteLog WHERE Kind = $k ORDER BY Id DESC LIMIT 1", ("$k", SiteUpdated)))
            if (cmd.ExecuteScalar() is string last && last.StartsWith(text, StringComparison.Ordinal)) return;
        using var tx = c.BeginTransaction();
        Write(c, tx, null, SiteUpdated, text + (baseBefore > 0 && baseBefore < Database.Version ? $"; база перенесена: версия {baseBefore} → {Database.Version}" : ""));
        tx.Commit();
    }

    /// <summary>«S1 · 14.10.2026»; без блока — только дата.</summary>
    public static string Run(string block, DateTime? day) =>
        (string.IsNullOrEmpty(block) ? "" : block + " · ") + (day.HasValue ? Fmt.Day(day.Value) : "");

    /// <summary>«+3 новых · 2 исправлены · ↺ 1 вернулась · +1 проверка (4 коллизии)»; ничего — «без изменений».</summary>
    public static string Changes(int @new, int @fixed, int returned, int newTests, int newTestClashes)
    {
        var parts = new List<string>();
        if (@new > 0) parts.Add("+" + Fmt.Plural(@new, "новая", "новые", "новых"));
        if (@fixed > 0) parts.Add(Fmt.Plural(@fixed, "исправлена", "исправлены", "исправлено"));
        if (returned > 0) parts.Add("↺ " + Fmt.Plural(returned, "вернулась", "вернулись", "вернулось"));
        if (newTests > 0) parts.Add("+" + Fmt.Plural(newTests, "проверка", "проверки", "проверок") + " (" + Fmt.Plural(newTestClashes, "коллизия", "коллизии", "коллизий") + ")");
        return parts.Count == 0 ? "без изменений" : string.Join(" · ", parts);
    }

    /// <summary>Коллизия для записи: «Конфликт14 (S1 · АР Стены/МЕР Трубы)».</summary>
    public static string Clash(string name, string block, string test) =>
        (string.IsNullOrEmpty(name) ? "коллизия" : name) + " (" + (string.IsNullOrEmpty(block) ? "" : block + " · ") + test + ")";

    public static string Cut(string text, int max = 300)
    {
        text = (text ?? "").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
