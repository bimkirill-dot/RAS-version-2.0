// явные using — файл подключают и проверки (tests\RASv2.Tests, .NET Framework 4.8 без неявных using)
using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>Сколько коллизий в каком статусе.</summary>
public sealed class StateCounts
{
    public int Active { get; set; }
    public int Fixed { get; set; }
    public int Accepted { get; set; }
    public int Total => Active + Fixed + Accepted;

    public void Add(ClashState state, int count)
    {
        switch (state)
        {
            case ClashState.Fixed: Fixed += count; break;
            case ClashState.Accepted: Accepted += count; break;
            default: Active += count; break;
        }
    }
}

public sealed class ProjectRow
{
    public long Id { get; set; }
    public string Name { get; set; }
    public string Folder { get; set; }
    public StateCounts Counts { get; } = new StateCounts();
    /// <summary>Активные по критичности («Высокая» → 12); "" — без критичности.</summary>
    public Dictionary<string, int> ActiveByCriticality { get; } = new Dictionary<string, int>();
    public DateTime? LastReport { get; set; }
}

public sealed class TestRow
{
    public long Id { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public string Name { get; set; }
    public string LeftSet { get; set; }
    public string RightSet { get; set; }
    public StateCounts Counts { get; } = new StateCounts();
    public DateTime? LastReport { get; set; }
}

public sealed class ReportRow
{
    public long Id { get; set; }
    public string FilePath { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public DateTime ReportDate { get; set; }
    public bool DateFromReport { get; set; }
    /// <summary>Откуда дата отчёта: Logic.ReportPath.DateFromXml / DateFromFolder / DateFromFile.</summary>
    public string DateSource { get; set; }
    public DateTime Imported { get; set; }
    public int Results { get; set; }
}

public sealed class ElementView
{
    public string ModelFile { get; set; }
    public string Level { get; set; }
    public string LevelKey { get; set; }
    public string Category { get; set; }
    public string Family { get; set; }
    public string Type { get; set; }
    public string Name { get; set; }
    public long? ElementId { get; set; }
}

/// <summary>Строка списка коллизий: «личность» + её последнее появление в отчёте.</summary>
public sealed class ClashRow
{
    public long Id { get; set; }
    public string Name { get; set; }
    public long TestId { get; set; }
    public string TestName { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public ClashState State { get; set; }
    public bool InLastReport { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public ElementView A { get; set; }
    public ElementView B { get; set; }
}

/// <summary>
/// Отбор в списке коллизий; пустое поле — без отбора. Критичность, блок, проверка — в базе (ClashStore.Clashes);
/// статус, поля и поиск — на странице (Logic.ClashFacets), чтобы считать числа у значений.
/// </summary>
public sealed class ClashFilter
{
    /// <summary>Критичность («Высокая», "" — без критичности); null — любая.</summary>
    public string Criticality { get; set; }
    /// <summary>Блок («S1», "" — без блока); null — любой.</summary>
    public string Block { get; set; }
    /// <summary>
    /// Тип проверки — название проверки Navisworks («АР Стены/МЕР Трубы»); одинаковое в разных блоках и критичностях —
    /// один тип (решение разработчика). null — все.
    /// </summary>
    public string TestName { get; set; }
    public ClashState? State { get; set; }
    /// <summary>Выбранные значения полей (Logic.ClashFacets.Level / Category / Discipline / Report → ключи значений).</summary>
    public Dictionary<string, HashSet<string>> Facets { get; } = new Dictionary<string, HashSet<string>>();
    /// <summary>Номер элемента, имя, тип, семейство, файл модели, имя коллизии — без учёта регистра.</summary>
    public string Search { get; set; }
}

public sealed class OccurrenceView
{
    public long Id { get; set; }
    public long ReportId { get; set; }
    public DateTime ReportDate { get; set; }
    public string ReportFile { get; set; }
    public string Name { get; set; }
    public NavisworksStatus NavisStatus { get; set; }
    public bool HasImage { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double? Distance { get; set; }
    public DateTime? Created { get; set; }
    public string GridLocation { get; set; }
    public string GroupName { get; set; }
    public string Description { get; set; }
    public List<ElementView> Elements { get; } = new List<ElementView>();
}

public sealed class EventView
{
    public DateTime Time { get; set; }
    public string Kind { get; set; }
    public string Text { get; set; }
    public ClashState? OldState { get; set; }
    public ClashState? NewState { get; set; }
    public string Author { get; set; }
}

/// <summary>Карточка коллизии.</summary>
public sealed class ClashCard
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; }
    public string TestName { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public string LeftSet { get; set; }
    public string RightSet { get; set; }
    public ClashState State { get; set; }
    public bool InLastReport { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public string Units { get; set; } = "m";
    /// <summary>Последнее появление — первое в списке.</summary>
    public List<OccurrenceView> Occurrences { get; } = new List<OccurrenceView>();
    public List<EventView> Events { get; } = new List<EventView>();
    public OccurrenceView Last => Occurrences.FirstOrDefault();
}

/// <summary>Вид события в истории коллизии (Events.Kind).</summary>
public static class EventKinds
{
    public const string Found = "found";
    public const string Reappeared = "reappeared";
    public const string Gone = "gone";
    public const string Status = "status";
    public const string Comment = "comment";
    /// <summary>Номер Navisworks сменился — коллизия узнана по паре элементов.</summary>
    public const string Rekey = "rekey";
}
