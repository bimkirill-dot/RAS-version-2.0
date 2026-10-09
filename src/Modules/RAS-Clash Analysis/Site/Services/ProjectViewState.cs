using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Как человек смотрит на список коллизий проекта: статус, поиск, поля, свёрнутые группы, выбранная коллизия.</summary>
public sealed class ProjectView
{
    public ClashFilter Filter { get; } = new ClashFilter { State = ClashState.Active };
    /// <summary>Свёрнутые группы «что с чем» (номера проверок).</summary>
    public HashSet<long> Collapsed { get; } = new HashSet<long>();
    public long? SelectedId { get; set; }
    /// <summary>После смены статуса в карточке — сразу к следующей коллизии.</summary>
    public bool AutoNext { get; set; } = true;
}

/// <summary>
/// Вид списка на время работы с сайтом (одна вкладка браузера): ушёл в отдельную карточку коллизии или в обзор, вернулся —
/// статус, поиск, поля, свёрнутые группы на месте. Обновление страницы (F5) начинает заново.
/// </summary>
public sealed class ProjectViewState
{
    private readonly Dictionary<long, ProjectView> _views = new Dictionary<long, ProjectView>();

    public ProjectView For(long projectId)
    {
        if (!_views.TryGetValue(projectId, out var v)) _views[projectId] = v = new ProjectView();
        return v;
    }
}

/// <summary>
/// Адрес списка коллизий: критичность и блок — в адресе (?c=…&amp;b=…), чтобы ссылку можно было открыть снова и переслать.
/// «*» (или нет параметра) — все, «-» — без критичности / без блока.
/// </summary>
public static class ScopeQuery
{
    public static string Encode(string value) => value == null ? "*" : value == "" ? "-" : value;

    public static string Decode(string query) => query == null || query == "*" ? null : query == "-" ? "" : query;

    public static string ListLink(long projectId, string criticality, string block) =>
        $"project/{projectId}/clashes?c={Uri.EscapeDataString(Encode(criticality))}&b={Uri.EscapeDataString(Encode(block))}";
}
