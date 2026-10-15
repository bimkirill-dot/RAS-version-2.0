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
    /// <summary>Блоки — вручную (проекты, добавленные с 15.10.2026); иначе — распознаются по названиям папок.</summary>
    public bool ManualBlocks { get; set; }
    /// <summary>Сколько блоков задано вручную.</summary>
    public int Blocks { get; set; }
    public long Id { get; set; }
    public string Name { get; set; }
    public string Folder { get; set; }
    public StateCounts Counts { get; } = new StateCounts();
    public DateTime? LastReport { get; set; }
    /// <summary>Сколько было прогонов (дней отчётов): один — «первая проверка».</summary>
    public int Runs { get; set; }
    /// <summary>Что принёс последний прогон каждой проверки проекта (DECISIONS.md, «Что принёс прогон — везде»).</summary>
    public RunDelta LastRun { get; } = new RunDelta();
}

/// <summary>Что принёс прогон: новые, исправленные им, вернувшиеся после исправления.</summary>
public sealed class RunDelta
{
    public int New { get; set; }
    public int Fixed { get; set; }
    public int Returned { get; set; }
    /// <summary>
    /// Проверки, впервые попавшие в прогон (не в первый прогон блока): их коллизии — активные, но не «новые» — их просто
    /// впервые проверили (14.10.2026). Без них счёт «активных было − исправлено + новые» не сходился.
    /// </summary>
    public List<string> NewTests { get; } = new List<string>();
    /// <summary>Сколько коллизий в этих проверках.</summary>
    public int NewTestClashes { get; set; }
    /// <summary>Проверки прошлого прогона блока, которых в этом прогоне нет.</summary>
    public List<string> DroppedTests { get; } = new List<string>();
    public bool Any => New + Fixed + Returned > 0 || NewTests.Count > 0 || DroppedTests.Count > 0;

    public void Add(RunDelta d)
    {
        New += d.New;
        Fixed += d.Fixed;
        Returned += d.Returned;
        NewTests.AddRange(d.NewTests);
        NewTestClashes += d.NewTestClashes;
        DroppedTests.AddRange(d.DroppedTests);
    }
}

/// <summary>Блок проекта, заданный вручную: название и папка внутри папки проекта; сколько отчётов из него загружено.</summary>
public sealed class BlockRow
{
    public long Id { get; set; }
    public string Name { get; set; }
    public string Folder { get; set; }
    public int Reports { get; set; }
}

/// <summary>Категория проверок (на весь сайт): название и проверки (ключ сравнения и название, как добавили).</summary>
public sealed class CategoryRow
{
    public long Id { get; set; }
    public string Name { get; set; }
    /// <summary>Цвет — ключ палитры (TestCategories.Colors); null — без цвета.</summary>
    public string Color { get; set; }
    /// <summary>Проверки категории без подкатегории.</summary>
    public List<(string Key, string Name)> Tests { get; } = new();
    public List<SubcategoryRow> Subcategories { get; } = new();
    /// <summary>Все проверки категории — и в подкатегориях.</summary>
    public int TotalTests => Tests.Count + Subcategories.Sum(s => s.Tests.Count);
}

/// <summary>Подкатегория — внутри своей категории (один уровень).</summary>
public sealed class SubcategoryRow
{
    public long Id { get; set; }
    public string Name { get; set; }
    /// <summary>Цвет — ключ палитры; null — как у категории.</summary>
    public string Color { get; set; }
    public List<(string Key, string Name)> Tests { get; } = new();
}

/// <summary>Блок в форме ввода (ещё не сохранён).</summary>
public sealed class BlockDraft
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>Название вписали руками — выбор папки его больше не меняет.</summary>
    public bool NameTouched { get; set; }
    /// <summary>Подставлен сайтом: папка, похожая на блок, найдена в папке проекта.</summary>
    public bool Suggested { get; set; }
}

/// <summary>Запись истории сайта (SiteHistory).</summary>
public sealed class HistoryRow
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    /// <summary>Проект ещё есть — на него можно перейти; удалён — только название.</summary>
    public long? ProjectId { get; set; }
    public string ProjectName { get; set; }
    public string Kind { get; set; }
    public string Text { get; set; }
    /// <summary>Коллизия ещё есть — на неё можно перейти.</summary>
    public long? ClashId { get; set; }
    public string Author { get; set; }
}

/// <summary>Что удалится вместе с проектом — для вопроса перед удалением.</summary>
public sealed class ProjectContents
{
    public int Reports { get; set; }
    public int Clashes { get; set; }
    public int Comments { get; set; }
    /// <summary>Статусы, поставленные людьми (а не прогоном).</summary>
    public int ManualStates { get; set; }
}

public sealed class TestRow
{
    public long Id { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public string Name { get; set; }
    /// <summary>Тип проверки: своё название в проекте (TypeNames) или название проверки.</summary>
    public string Type { get; set; }
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
    /// <summary>Предупреждение загрузки (неполный отчёт, пропало подозрительно много); null — нет.</summary>
    public string Warning { get; set; }
    /// <summary>Пропало подозрительно много (ReportMerge.TooManyGone): у отчёта — «Вернуть активными».</summary>
    public bool Suspicious { get; set; }
    public DateTime Imported { get; set; }
    public int Results { get; set; }
    /// <summary>Что принёс этот отчёт.</summary>
    public RunDelta Delta { get; } = new RunDelta();
    /// <summary>Первый прогон его проверки: раньше отчётов по ней не было — «первая проверка», а не «новые».</summary>
    public bool FirstRun { get; set; }
    /// <summary>Проверки в отчёте (ReportTests).</summary>
    public List<long> TestIds { get; } = new List<long>();
    /// <summary>Проверки, для которых это первый отчёт, и сколько в нём их коллизий.</summary>
    public List<long> NewTestIds { get; } = new List<long>();
    public int NewTestClashes { get; set; }
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
    /// <summary>Тип проверки (своё название в проекте или название проверки).</summary>
    public string Type { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public ClashState State { get; set; }
    /// <summary>Сколько раз возвращалась после исправления.</summary>
    public int ReturnCount { get; set; }
    /// <summary>В последнем прогоне своей проверки: появилась / исправлена / вернулась.</summary>
    public bool NewInLastRun { get; set; }
    public bool FixedInLastRun { get; set; }
    public bool ReturnedInLastRun { get; set; }
    /// <summary>Дни прогонов («yyyy-MM-dd»), которыми исправлена и в которые вернулась (для «Даты» — любого прогона).</summary>
    public HashSet<string> FixedOn { get; } = new HashSet<string>();
    public HashSet<string> ReturnedOn { get; } = new HashSet<string>();
    /// <summary>Первый прогон её проверки («yyyy-MM-dd»): коллизии в нём — исходная точка, а не «новые».</summary>
    public string TestFirstRun { get; set; }
    /// <summary>Расстояние (глубина пересечения) и оси — из последнего появления; null — не включены в отчёт Navisworks.</summary>
    public double? Distance { get; set; }
    public string Units { get; set; } = "m";
    public string GridLocation { get; set; }
    /// <summary>Дни прогонов («yyyy-MM-dd»), к которым коллизия относится: была в отчёте или исправлена им.</summary>
    public HashSet<string> Runs { get; } = new HashSet<string>();
    /// <summary>Повторы — в скольких прогонах (днях отчётов) встречалась.</summary>
    public int Repeats { get; set; }
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
    /// <summary>Категория проверок («Высокая критичность», "" — без категории); null — любая (DECISIONS.md, «Категории проверок»).</summary>
    public string Category { get; set; }
    /// <summary>Подкатегория выбранной категории; null — вся категория.</summary>
    public string Subcategory { get; set; }
    /// <summary>
    /// Тип проверки — название проверки Navisworks («АР Стены/МЕР Трубы») или своё название в проекте (TypeNames);
    /// одинаковое в разных блоках и критичностях — один тип (решение разработчика). null — все.
    /// </summary>
    public string TestType { get; set; }
    public ClashState? State { get; set; }
    /// <summary>Выбранные значения полей (Logic.ClashFacets.Level / Category / Report / State → ключи значений).</summary>
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
    /// <summary>Сколько результатов Navisworks на эту пару элементов в отчёте (точек касания).</summary>
    public int Points { get; set; } = 1;
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
    public string Type { get; set; }
    public string Criticality { get; set; } = "";
    public string Block { get; set; } = "";
    public int ReturnCount { get; set; }
    /// <summary>Последний прогон её проверки (день) и что он с ней сделал: «new» / «fixed» / «returned»; null — ничего.</summary>
    public DateTime? RunDay { get; set; }
    public string RunKind { get; set; }
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
    /// <summary>Исправление подтверждено прогоном: была «Ждёт проверки», в отчёте её нет.</summary>
    public const string Confirmed = "confirmed";
    /// <summary>Номер Navisworks сменился — коллизия узнана по паре элементов.</summary>
    public const string Rekey = "rekey";
}
