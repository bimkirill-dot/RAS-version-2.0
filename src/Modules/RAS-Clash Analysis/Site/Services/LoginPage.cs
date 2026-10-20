using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Вход на сайт (DECISIONS.md модуля, «Вход на сайт»): своя простая страница (без Blazor — вход ставит куки, а это делает
/// обычный запрос браузера), выход, первая учётная запись. Первую — администратора — можно создать только на компьютере
/// сайта: иначе её мог бы завести кто угодно из сети, пока сайт ещё пустой.
/// </summary>
public static class LoginPage
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/login", (HttpContext ctx, UserStore users, IAntiforgery af) =>
        {
            var back = SafeReturn(ctx.Request.Query["ReturnUrl"]);
            if (!users.Any()) return Html(IsLocal(ctx) ? Setup(af.GetAndStoreTokens(ctx), null, "", "") : NotReady());
            return Html(Form(af.GetAndStoreTokens(ctx), null, "", back));
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext ctx, UserStore users, IAntiforgery af) =>
        {
            if (!await Valid(ctx, af)) return Html(Form(af.GetAndStoreTokens(ctx), "Страница устарела — введите логин и пароль ещё раз.", "", "/"));
            var form = await ctx.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var back = SafeReturn(form["back"]);
            var user = users.SignIn(login, form["password"]);
            if (user == null)
                return Html(Form(af.GetAndStoreTokens(ctx), "Неверный логин или пароль — или учётная запись выключена (спросите администратора сайта).", login, back));
            await SignIn(ctx, user, form["remember"] == "on");
            return Results.LocalRedirect(back);
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/setup", async (HttpContext ctx, UserStore users, IAntiforgery af) =>
        {
            if (users.Any() || !IsLocal(ctx)) return Results.Redirect("/login");
            var form = await ctx.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var name = form["name"].ToString();
            if (!await Valid(ctx, af)) return Html(Setup(af.GetAndStoreTokens(ctx), "Страница устарела — попробуйте ещё раз.", login, name));
            string error = form["password"] != form["repeat"] ? "Пароли не совпадают." : users.Create(login, name, UserStore.Admin, form["password"]);
            if (error != null) return Html(Setup(af.GetAndStoreTokens(ctx), error, login, name));
            await SignIn(ctx, users.SignIn(login, form["password"]), remember: true);
            return Results.LocalRedirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous();
    }

    /// <summary>Вход: в куки — номер, имя, логин и роль; запомнить — на 30 дней, иначе до закрытия браузера.</summary>
    private static Task SignIn(HttpContext ctx, SiteUserRow user, bool remember)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(SiteUser.IdClaim, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(SiteUser.LoginClaim, user.Login),
            new Claim(ClaimTypes.Role, user.Role),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = remember });
    }

    private static async Task<bool> Valid(HttpContext ctx, IAntiforgery af)
    {
        try
        {
            await af.ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>Куда вернуться после входа — только страница этого сайта («/project/3»), не чужой адрес.</summary>
    private static string SafeReturn(string back) =>
        !string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//") && !back.StartsWith("/\\") ? back : "/";

    private static IResult Html(string body) => Results.Content(Page(body), "text/html; charset=utf-8");

    private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string Token(AntiforgeryTokenSet t) => $"<input type=\"hidden\" name=\"{E(t.FormFieldName)}\" value=\"{E(t.RequestToken)}\" />";

    private static string Error(string error) => error == null ? "" : $"<p class=\"login-error\">{E(error)}</p>";

    private static string Form(AntiforgeryTokenSet t, string error, string login, string back) => $"""
        <form class="login-box" method="post" action="/login">
          <h1>Вход</h1>
          <p class="muted">Clash Analysis · Менеджер коллизий</p>
          {Error(error)}
          {Token(t)}
          <input type="hidden" name="back" value="{E(back)}" />
          <label>Логин<input name="login" value="{E(login)}" autocomplete="username" autofocus required /></label>
          <label>Пароль<input name="password" type="password" autocomplete="current-password" required /></label>
          <label class="login-check"><input type="checkbox" name="remember" checked /> Запомнить меня на этом компьютере</label>
          <button class="btn primary" type="submit">Войти</button>
          <p class="muted small">Нет учётной записи или забыли пароль — обратитесь к администратору сайта.</p>
        </form>
        """;

    private static string Setup(AntiforgeryTokenSet t, string error, string login, string name) => $"""
        <form class="login-box" method="post" action="/setup">
          <h1>Первый вход</h1>
          <p class="muted">Учётных записей ещё нет. Создайте свою — администратора: вы будете заводить учётные записи коллегам («Настройки» → «Сотрудники»).</p>
          {Error(error)}
          {Token(t)}
          <label>Логин<input name="login" value="{E(login)}" autocomplete="username" autofocus required /></label>
          <label>Имя — как его видеть в истории<input name="name" value="{E(name)}" placeholder="Полетаев К." required /></label>
          <label>Пароль<input name="password" type="password" autocomplete="new-password" required /></label>
          <label>Пароль ещё раз<input name="repeat" type="password" autocomplete="new-password" required /></label>
          <button class="btn primary" type="submit">Создать и войти</button>
        </form>
        """;

    private static string NotReady() => """
        <div class="login-box">
          <h1>Сайт ещё не настроен</h1>
          <p>Учётных записей пока нет. Первую создаёт администратор сайта на том компьютере, где работает сайт.</p>
        </div>
        """;

    private static string Page(string body) => $$"""
        <!DOCTYPE html>
        <html lang="ru">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Вход · Clash Analysis</title>
          <link rel="stylesheet" href="/site.css?v={{AppVersion.Tag}}" />
          <link rel="icon" href="/favicon.svg?v={{AppVersion.Tag}}" type="image/svg+xml" />
          <script>try { document.documentElement.dataset.theme = localStorage.getItem('ras-theme') || 'light'; } catch (e) { document.documentElement.dataset.theme = 'light'; }</script>
        </head>
        <body class="login-page">
          {{body}}
        </body>
        </html>
        """;
}
