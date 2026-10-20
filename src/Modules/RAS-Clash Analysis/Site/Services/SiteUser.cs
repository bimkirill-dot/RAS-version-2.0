using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Кто сейчас делает действие на сайте — для истории («кто сделал») и журнала статусов. Ставится на время каждого действия в
/// браузере (SiteUserCircuitHandler); загрузка отчётов по расписанию идёт без человека — там пусто (это делает сам сайт).
/// </summary>
public static class SiteUser
{
    private static readonly AsyncLocal<string> Current = new();

    /// <summary>Имя вошедшего (как в учётной записи); вне действия человека — null.</summary>
    public static string Name => Current.Value;

    internal static void Set(string name) => Current.Value = name;

    public const string IdClaim = ClaimTypes.NameIdentifier;
    public const string LoginClaim = "ras:login";

    public static long? IdOf(ClaimsPrincipal user) => long.TryParse(user?.FindFirst(IdClaim)?.Value, out var id) ? id : null;
}

/// <summary>Перед каждым действием в браузере (нажатие, ввод) — кто его делает (SiteUser.Name).</summary>
public sealed class SiteUserCircuitHandler(AuthenticationStateProvider auth) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var state = await auth.GetAuthenticationStateAsync();
            SiteUser.Set(state.User.Identity?.IsAuthenticated == true ? state.User.Identity.Name : null);
            await next(context);
        };
}
