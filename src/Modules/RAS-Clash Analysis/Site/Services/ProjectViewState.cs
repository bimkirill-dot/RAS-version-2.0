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
