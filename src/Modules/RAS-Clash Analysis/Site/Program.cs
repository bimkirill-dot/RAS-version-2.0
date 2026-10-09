using RASv2.ClashAnalysis.Site;
using RASv2.ClashAnalysis.Site.Components;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Services;

// Сайт RAS-Clash Analysis. Адрес и настройки — appsettings.json рядом с программой; запуск и обновление — site.cmd.
// окно сайта показывает журнал по-русски
Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // папка программы, а не текущая папка: сайт запускают ярлыком, службой, из site.cmd
    ContentRootPath = AppContext.BaseDirectory,
});

// свои настройки — в папке данных (settings.json): appsettings.json рядом с программой заменяется при каждом обновлении
builder.Configuration.AddJsonFile(SiteOptions.LocalSettingsFile, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
var options = builder.Configuration.GetSection("Site").Get<SiteOptions>() ?? new SiteOptions();
builder.Services.AddSingleton(options);
builder.Logging.AddProvider(new FileLoggerProvider(options.LogsFolder));

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<ClashStore>();
builder.Services.AddSingleton<ReportImporter>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddHostedService<ImportScheduler>();
builder.Services.AddScoped<ProjectViewState>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation("Сайт запускается. Данные: {Data}; копии базы: {Backups}", options.Data, options.Backups);
app.Services.GetRequiredService<Database>().Migrate(log);

app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();

// снимок коллизии: только из папки снимков сайта
app.MapGet("/snimok/{id:long}", (long id, ClashStore store) =>
{
    var relative = store.ImageOf(id);
    if (relative == null) return Results.NotFound();
    var root = Path.GetFullPath(options.ImagesFolder);
    var file = Path.GetFullPath(Path.Combine(root, relative));
    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Results.NotFound();
    return Results.File(file, "image/jpeg");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
