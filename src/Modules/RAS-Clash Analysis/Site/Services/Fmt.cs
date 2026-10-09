using System.Globalization;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Как показывать на страницах (PRINCIPLES.md, раздел 12): даты «ДД.ММ.ГГГГ ЧЧ:ММ», статусы по-русски.</summary>
public static class Fmt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Date(DateTime t) => t.ToString("dd.MM.yyyy HH:mm", Ru);
    public static string Date(DateTime? t) => t.HasValue ? Date(t.Value) : "—";
    public static string Day(DateTime t) => t.ToString("dd.MM.yyyy", Ru);

    public static string State(ClashState s) => ClashRules.Title(s);
    public static string State(ClashState? s) => s.HasValue ? State(s.Value) : "—";

    /// <summary>Класс оформления статуса: state-active / state-fixed / state-accepted.</summary>
    public static string StateClass(ClashState s) => s switch
    {
        ClashState.Fixed => "state-fixed",
        ClashState.Accepted => "state-accepted",
        ClashState.Pending => "state-pending",
        _ => "state-active",
    };

    public static string Navis(NavisworksStatus s) => s switch
    {
        NavisworksStatus.New => "Создать",
        NavisworksStatus.Active => "Активные",
        NavisworksStatus.Reviewed => "Проверено",
        NavisworksStatus.Approved => "Подтверждено",
        NavisworksStatus.Resolved => "Исправлено",
        _ => "—",
    };

    /// <summary>Число в единицах отчёта → миллиметры, без лишних знаков.</summary>
    public static string Mm(double? value, string units) =>
        value.HasValue ? (value.Value * ClashRules.MetersPerUnit(units) * 1000).ToString("0", Ru) + " мм" : "—";

    /// <summary>Глубина пересечения без знака, мм: «160 мм».</summary>
    public static string MmAbs(double? value, string units) => value.HasValue ? Mm(Math.Abs(value.Value), units) : "—";

    public static string Meters(double? value, string units) =>
        value.HasValue ? (value.Value * ClashRules.MetersPerUnit(units)).ToString("0.000", Ru) : "—";

    /// <summary>«Стены · Базовая стена · 2598747».</summary>
    public static string Element(Data.ElementView e)
    {
        if (e == null) return "—";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(e.Category)) parts.Add(e.Category);
        if (!string.IsNullOrEmpty(e.Type)) parts.Add(e.Type);
        return parts.Count == 0 ? e.Name ?? "—" : string.Join(" · ", parts);
    }

    /// <summary>«у 1 коллизии», «у 21 коллизии», «у 5 коллизий» — после «у».</summary>
    public static string Clashes(int n) => n + (n % 10 == 1 && n % 100 != 11 ? " коллизии" : " коллизий");

    /// <summary>Число со словом: Plural(1, "отчёт", "отчёта", "отчётов") → «1 отчёт», 3 → «3 отчёта», 5 → «5 отчётов».</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var tens = n % 100;
        var last = n % 10;
        var word = tens >= 11 && tens <= 14 ? many : last == 1 ? one : last >= 2 && last <= 4 ? few : many;
        return n + " " + word;
    }

    /// <summary>Модель — имя файла без расширения.</summary>
    public static string Model(string modelFile) => string.IsNullOrEmpty(modelFile) ? "—" : ClashRules.ModelName(modelFile);
}
