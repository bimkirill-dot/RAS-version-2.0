using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Как человек смотрит на проект: отбор, сортировка, выбранная коллизия, вкладка.</summary>
public sealed class ProjectView
{
    public ClashFilter Filter { get; } = new ClashFilter { State = ClashState.Active };
    /// <summary>Критичность и блок уже выбраны (иначе при открытии проекта — первая критичность, все блоки).</summary>
    public bool ScopeChosen { get; set; }
    public string SortKey { get; set; }
    public bool SortDesc { get; set; }
    public long? SelectedId { get; set; }
    public int Tab { get; set; }
    /// <summary>После смены статуса в карточке — сразу к следующей коллизии.</summary>
    public bool AutoNext { get; set; } = true;

    /// <summary>Отчёты: сортировка (date / block / file / count / imported), группировка ("" — нет, block / date / file / imported), свёрнутые группы.</summary>
    public string ReportSortKey { get; set; } = "date";
    public bool ReportSortDesc { get; set; } = true;
    /// <summary>Отчёты — сразу по блоку (S1, S2… по порядку), внутри — по дате (новые сверху): просьба разработчика 13.10.2026.</summary>
    public string ReportGroup { get; set; } = "block-date";
    /// <summary>Свёрнутые группы «Отчётов» (блоки и прочие группы первого уровня): при открытии раскрыты.</summary>
    public HashSet<string> ReportCollapsed { get; } = new HashSet<string>();
    /// <summary>Раскрытые даты внутри блока: при открытии свёрнуты — «свёрнуто до дат» (просьба разработчика 13.10.2026).</summary>
    public HashSet<string> ReportExpanded { get; } = new HashSet<string>();

    /// <summary>Отбор последнего прогона: null — нет, new / fixed / returned.</summary>
    public string RunFilter { get; set; }
    /// <summary>Группировка списка: "" — нет, test — по типу проверки, element — по элементу; свёрнутые группы.</summary>
    public string GroupBy { get; set; } = "";
    public HashSet<string> GroupCollapsed { get; } = new HashSet<string>();
    /// <summary>Дата: last — последний прогон, all — все, «yyyy-MM-dd» — прогон этого дня.</summary>
    public string RunDate { get; set; } = "last";
    public int PageSize { get; set; } = 50;
    /// <summary>Отбор по умолчанию (статус — открытые) уже поставлен.</summary>
    public bool Initialized { get; set; }
    /// <summary>Показан отбор «Категория» (спрятан под «Ещё»).</summary>
    public bool MoreFilters { get; set; }
    /// <summary>«Блок» на боковой панели раскрыт — блоки видны подразделами.</summary>
    public bool BlocksOpen { get; set; }
    /// <summary>Раздел «Категория» боковой панели раскрыт.</summary>
    public bool CategoriesOpen { get; set; }
    /// <summary>«Тип проверки» на боковой панели раскрыт — типы видны подразделами.</summary>
    public bool TypesOpen { get; set; }
    /// <summary>Развёрнутые категории в «Типе проверки» (при открытии свёрнуты; выбранная — разворачивается).</summary>
    public HashSet<string> CategoriesExpanded { get; } = new HashSet<string>();
}

/// <summary>
/// Вид проекта на время работы с сайтом (одна вкладка браузера): ушёл в отдельную карточку коллизии, вернулся —
/// отбор и выбранная коллизия на месте. Обновление страницы (F5) начинает заново.
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
