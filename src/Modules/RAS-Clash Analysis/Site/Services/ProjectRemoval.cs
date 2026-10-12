using Microsoft.Data.Sqlite;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Удаление проекта с сайта (DECISIONS.md модуля, «Удаление проекта»): копия базы рядом с ней → проект со всем, что к нему
/// относится, — из базы (одной транзакцией) → его снимки. Папка проекта и отчёты в ней не трогаются.
/// Идёт, пока не идёт загрузка отчётов: иначе проверка папок могла бы дописывать в удаляемый проект.
/// </summary>
public sealed class ProjectRemoval
{
    private readonly Database _db;
    private readonly ClashStore _store;
    private readonly ReportImporter _importer;
    private readonly SiteOptions _options;
    private readonly ILogger<ProjectRemoval> _log;

    public ProjectRemoval(Database db, ClashStore store, ReportImporter importer, SiteOptions options, ILogger<ProjectRemoval> log)
    {
        _db = db;
        _store = store;
        _importer = importer;
        _options = options;
        _log = log;
    }

    /// <summary>Удалить проект; возвращает путь копии базы, сделанной перед удалением.</summary>
    public Task<string> DeleteAsync(long id) => _importer.ExclusiveAsync(() => Delete(id));

    private string Delete(long id)
    {
        var project = _store.Project(id) ?? throw new InvalidOperationException("Проект не найден — возможно, его уже удалили.");
        var contents = _store.ProjectContents(id);

        var copy = _db.File + $".до-удаления-{SafeName(project.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}";
        using (var source = _db.Open())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }

        var reports = _store.DeleteProject(id);

        // снимки — после базы: не удалились (заняты) — лишние файлы, а не потерянные данные
        var left = 0;
        foreach (var report in reports)
        {
            var folder = Path.Combine(_options.ImagesFolder, report.ToString());
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                left++;
                _log.LogWarning("Снимки удалённого проекта не удалены: {Folder} — {Reason}", folder, ex.Message);
            }
        }

        _log.LogInformation(
            "Проект удалён с сайта: «{Name}» ({Folder}) — отчётов {Reports}, коллизий {Clashes}, комментариев {Comments}, статусов от людей {Manual}; " +
            "папок снимков не удалено {Left}. Копия базы перед удалением: {Copy}",
            project.Name, project.Folder, contents.Reports, contents.Clashes, contents.Comments, contents.ManualStates, left, copy);
        return copy;
    }

    /// <summary>Название проекта для имени файла: без запрещённых знаков, не длиннее 60.</summary>
    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return safe.Length > 60 ? safe[..60] : safe;
    }
}
