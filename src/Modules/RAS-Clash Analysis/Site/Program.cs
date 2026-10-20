using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
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
builder.Configuration.AddJsonFile(SiteOptions.SettingsFileFor(builder.Configuration["Site:DataFolder"]), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
var options = builder.Configuration.GetSection("Site").Get<SiteOptions>() ?? new SiteOptions();
builder.Services.AddSingleton(options);
builder.Logging.AddProvider(new FileLoggerProvider(options.LogsFolder));

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<ClashStore>();
builder.Services.AddSingleton<ReportImporter>();
builder.Services.AddSingleton<ProjectRemoval>();
builder.Services.AddSingleton<SiteSettings>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddHostedService<ImportScheduler>();
builder.Services.AddScoped<ProjectViewState>();

// вход на сайт (DECISIONS.md модуля, «Вход на сайт»): логин и пароль сайта, без входа не открывается ничего
builder.Services.AddSingleton<UserStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/login";
    o.LogoutPath = "/logout";
    o.AccessDeniedPath = "/";
    o.Cookie.Name = "ras-clash-analysis";
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // учётную запись выключили или сменили роль — вход больше не действует (проверка — при каждом запросе к сайту)
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var id = SiteUser.IdOf(ctx.Principal);
        var user = id == null ? null : ctx.HttpContext.RequestServices.GetRequiredService<UserStore>().Find(id.Value);
        if (user == null || !user.Active || !ctx.Principal.IsInRole(user.Role))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<CircuitHandler, SiteUserCircuitHandler>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation("Сайт запускается (процесс {Pid}). Данные: {Data}; копии базы: {Backups}", Environment.ProcessId, options.Data, options.Backups);
ExitWatch.Start(log);
app.Lifetime.ApplicationStopping.Register(() => log.LogInformation("Сайт останавливается."));
var database = app.Services.GetRequiredService<Database>();
var baseBefore = database.Migrate(log);
// история сайта: обновление программы и перенос базы (DECISIONS.md модуля, «История сайта»)
SiteHistory.NoteStart(database, baseBefore);

app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
LoginPage.Map(app);

// снимок коллизии: только из папки снимков сайта
app.MapGet("/snimok/{id:long}", (long id, ClashStore store) =>
{
    var relative = store.ImageOf(id);
    if (relative == null) return Results.NotFound();
    var root = Path.GetFullPath(options.ImagesFolder);
    var file = Path.GetFullPath(Path.Combine(root, relative));
    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Results.NotFound();
    return Results.File(file, "image/jpeg");
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();

app.Run();
