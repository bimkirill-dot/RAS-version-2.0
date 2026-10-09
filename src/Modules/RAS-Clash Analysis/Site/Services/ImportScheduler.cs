namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Сам проверяет папки проектов на новые отчёты: при запуске и затем каждые SiteOptions.ScanMinutes минут.</summary>
public sealed class ImportScheduler : BackgroundService
{
    private readonly ReportImporter _importer;
    private readonly SiteOptions _options;
    private readonly ILogger<ImportScheduler> _log;

    public ImportScheduler(ReportImporter importer, SiteOptions options, ILogger<ImportScheduler> log)
    {
        _importer = importer;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (_options.ScanMinutes <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await _importer.ScanAsync();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Проверка папок проектов не удалась");
                }
                await Task.Delay(TimeSpan.FromMinutes(_options.ScanMinutes), stop);
            }
        }
        catch (OperationCanceledException)
        {
            // сайт останавливается
        }
    }
}
