using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Модель задачи: файл (сетевая папка) или путь Revit Server (RSN://…).</summary>
    public sealed class ExportModel
    {
        public string Path { get; set; }
        /// <summary>Имя NWC, поправленное руками; null — из имени модели по общему правилу.</summary>
        public string NwcName { get; set; }
        /// <summary>Блок, вписанный руками (в имени модели его нет — «Генплан.rvt»); null — из имени модели.</summary>
        public string Block { get; set; }
        /// <summary>
        /// Версия Revit модели (разработчик, 03.11.2026: «добавлять модели всех версий из любого Revit»): Revit Server, откуда взята
        /// (у каждой версии — свой); 0 — не известна (файл из сетевой папки). Выгружать — в Revit этой версии или новее.
        /// </summary>
        public int Year { get; set; }

        public ExportModel Clone() => (ExportModel)MemberwiseClone();
    }

    /// <summary>
    /// Настройки строки «Задач выгрузки» (разработчик, 03.11.2026: «могут задаваться на любом уровне; заданные ниже переписывают
    /// уровень выше»): null — от уровня выше; у задачи верхнего уровня всё своё (Top). Всё, что зависит от регламента, задаёт
    /// человек — плагин сам ничего из этого не решает. Папки — строки внутри папки проекта (полный путь — тоже можно).
    /// </summary>
    [DataContract]
    public sealed class NodeSettings
    {
        /// <summary>Модели; свои у подзадачи — вместо моделей уровня выше (разработчик: «свои вместо родителя»).</summary>
        [DataMember(Order = 1, EmitDefaultValue = false)] public List<ExportModel> Models { get; set; }
        /// <summary>Файлы проверок Navisworks (.xml, выгрузка Clash Detective) — все в NWF блока.</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public List<string> TestsFiles { get; set; }
        /// <summary>Имя NWF строки — своё, не наследуется; пусто — название строки («S1_ВК» → «S1_ВК.nwf»).</summary>
        [DataMember(Order = 3, EmitDefaultValue = false)] public string NwfName { get; set; }
        /// <summary>Папка проекта на сетевом диске — от неё строки папок.</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Folder { get; set; }
        /// <summary>
        /// Папка на Revit Server, откуда добавляли модели, — с неё открывается «Добавить модели…»; ставится сама, в окне её нет
        /// (разработчик, 02.11.2026: «убрать „Модели на сервере“ совсем»).
        /// </summary>
        [DataMember(Order = 5, EmitDefaultValue = false)] public string ServerFolder { get; set; }
        /// <summary>NWC — метки {Блок}, {Отдел}; даты нет — NWF ищет NWC по постоянному пути.</summary>
        [DataMember(Order = 6, EmitDefaultValue = false)] public string NwcFolder { get; set; }
        /// <summary>Папка для NWF; NWF постоянная (регламент 22.10): обновляется, а не собирается заново. Меток нет (03.11.2026).</summary>
        [DataMember(Order = 7, EmitDefaultValue = false)] public string NwfFolder { get; set; }
        /// <summary>Папка для отчётов — строка с {Дата}, {Формат}.</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)] public string ReportFolder { get; set; }
        /// <summary>Только прежние файлы (до 03.11.2026): форматы XML / HTML — при чтении переходят в ReportFormats.</summary>
        [DataMember(Order = 9, EmitDefaultValue = false)] public bool? Xml { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public bool? Html { get; set; }
        /// <summary>Вид для выгрузки; нигде не задан — из общих настроек.</summary>
        [DataMember(Order = 11, EmitDefaultValue = false)] public string ViewName { get; set; }
        /// <summary>
        /// «Формировать NWF» — своё у строки, не наследуется; действует только у строки без подзадач (разработчик, 03.11.2026:
        /// «подзадача — как отдельный блок, обычно 4 модели: добавляем их, ставим галочку — они объединяются в NWF»).
        /// </summary>
        [DataMember(Order = 12, EmitDefaultValue = false)] public bool MakeNwf { get; set; }
        /// <summary>Только прежние файлы (03.11.2026, днём): «имя элемента содержит …» — при чтении переходят в HideSets.</summary>
        [DataMember(Order = 13, EmitDefaultValue = false)] public List<string> HideNames { get; set; }
        /// <summary>Форматы отчётов — названия, как в Navisworks (ExportNodes.ReportFormatNames).</summary>
        [DataMember(Order = 14, EmitDefaultValue = false)] public List<string> ReportFormats { get; set; }
        /// <summary>
        /// Свои параметры выгрузки NWC и «Удалить перед выгрузкой» (разработчик, 03.11.2026: «Настройки — по общим настройкам и
        /// „Изменить“, то же, что шестерёнка»); null — от уровня выше, нигде — из общих настроек.
        /// </summary>
        [DataMember(Order = 15, EmitDefaultValue = false)] public ExportOverride Export { get; set; }
        /// <summary>
        /// Скрыть в NWF — поисковые наборы Navisworks (разработчик, 03.11.2026: «временные» скрываем — скрытые в проверки не
        /// попадают; правила — из инструмента поиска Navisworks; передаётся подзадачам). Своих правил компании в коде нет.
        /// </summary>
        [DataMember(Order = 16, EmitDefaultValue = false)] public List<HideSet> HideSets { get; set; }
        /// <summary>«Формировать отчёты» — своё у строки, не наследуется; действует у строки, которая собирает NWF.</summary>
        [DataMember(Order = 17, EmitDefaultValue = false)] public bool MakeReports { get; set; }
        /// <summary>Выгружать только модели, отредактированные в выбранный срок (остальные — NWC актуален); null — от уровня выше.</summary>
        [DataMember(Order = 18, EmitDefaultValue = false)] public ModelDateFilter Edited { get; set; }
        /// <summary>
        /// «NWC» выключен — ▷ не выгружает NWC моделей строки (NWF — из NWC, что уже лежат); своё у строки, не наследуется
        /// (разработчик, 07.11.2026: «выключатель должен быть у всего»). По умолчанию выгружается — поэтому «не», а не «да».
        /// </summary>
        [DataMember(Order = 19, EmitDefaultValue = false)] public bool NoNwc { get; set; }
        /// <summary>
        /// Галочка у подзадачи снята — при ▷ у строки выше она (и всё под ней) не делается, в списке — серая (разработчик, 07.11.2026);
        /// ▷ у неё самой — делается. Своё у строки, не наследуется.
        /// </summary>
        [DataMember(Order = 20, EmitDefaultValue = false)] public bool Skipped { get; set; }
        /// <summary>
        /// «Выполнять проверки» снята — NWF собирается и обновляется (со скрытием), проверки плагин не загружает и не прогоняет: человек
        /// — вручную (разработчик, 08.11.2026). Своё у строки, не наследуется; по умолчанию выполнять — поэтому «не».
        /// </summary>
        [DataMember(Order = 21, EmitDefaultValue = false)] public bool NoTestsRun { get; set; }
        /// <summary>
        /// «Экспортировать RVT» — модели строки отдельными RVT для заказчика (разработчик, 08.11.2026): связи удалить, очистить от
        /// неиспользуемого три раза, сохранить в «Папку для RVT», рабочие наборы — по общим настройкам. Своё у строки, не наследуется.
        /// </summary>
        [DataMember(Order = 22, EmitDefaultValue = false)] public bool MakeRvt { get; set; }
        /// <summary>Папка для RVT — метки {Блок}, {Отдел}, {Дата}; по умолчанию пусто — папка задачи (разработчик, 08.11.2026).</summary>
        [DataMember(Order = 23, EmitDefaultValue = false)] public string RvtFolder { get; set; }
        /// <summary>
        /// «Пересохранить RVT» — модели строки на то же место (разработчик, 08.11.2026: «чтобы уменьшить вес файла»): открыть
        /// отсоединённо с рабочими наборами, «Сохранить как» центральную туда же, освободить всё. Своё у строки, не наследуется.
        /// </summary>
        [DataMember(Order = 24, EmitDefaultValue = false)] public bool Resave { get; set; }
        /// <summary>Свои «Настройки RVT» (выгрузка) вместо общих; null — от уровня выше, нигде — общие.</summary>
        [DataMember(Order = 25, EmitDefaultValue = false)] public RvtOptions Rvt { get; set; }
        /// <summary>Свои «Настройки пересохранения» вместо общих; null — от уровня выше, нигде — общие.</summary>
        [DataMember(Order = 26, EmitDefaultValue = false)] public ResaveOptions ResaveOptions { get; set; }

        /// <summary>Задача верхнего уровня: всё своё, по умолчанию — регламент 15.10 (вида нет — из общих настроек).</summary>
        /// <summary>
        /// Новая задача: пути пустые (разработчик, 04.11.2026: «все пути по умолчанию пустые»; было «{Блок}\NWC», «NWF»,
        /// «Отчеты\{Дата}») — папку задачи человек задаёт сам (обязательна), пустая папка NWC, NWF или отчётов — сама папка задачи.
        /// </summary>
        public static NodeSettings Top() => new NodeSettings
        {
            Models = new List<ExportModel>(), TestsFiles = new List<string>(), HideSets = new List<HideSet>(), Folder = "", NwcFolder = "",
            NwfFolder = "", ReportFolder = "", RvtFolder = "", ReportFormats = new List<string> { "XML", "HTML" },
        };

        public NodeSettings Clone() => new NodeSettings
        {
            Models = Models?.Select(m => m.Clone()).ToList(),
            TestsFiles = TestsFiles?.ToList(), NwfName = NwfName, Folder = Folder, ServerFolder = ServerFolder, NwcFolder = NwcFolder,
            NwfFolder = NwfFolder, ReportFolder = ReportFolder, Xml = Xml, Html = Html, ViewName = ViewName, MakeNwf = MakeNwf,
            HideNames = HideNames?.ToList(), ReportFormats = ReportFormats?.ToList(), Export = Export?.Clone(),
            HideSets = HideSets?.Select(h => h.Clone()).ToList(), MakeReports = MakeReports, Edited = Edited?.Clone(), NoNwc = NoNwc, Skipped = Skipped, NoTestsRun = NoTestsRun,
            MakeRvt = MakeRvt, RvtFolder = RvtFolder, Resave = Resave, Rvt = Rvt?.Clone(),
            ResaveOptions = ResaveOptions?.Clone(),
        };
    }

    /// <summary>
    /// «Экспортировать модели, отредактированные после / до / за период» (разработчик, 03.11.2026: «если модель после этого не
    /// обновлялась — NWC уже актуален, в списке — серой»). Даты — по дням, включительно.
    /// </summary>
    [DataContract]
    public sealed class ModelDateFilter
    {
        public const string After = "после";
        public const string Before = "до";
        public const string Between = "за период";

        [DataMember(Order = 1)] public bool On { get; set; }
        [DataMember(Order = 2)] public string Mode { get; set; } = After;
        /// <summary>«после» и «до» — эта дата; «за период» — начало.</summary>
        [DataMember(Order = 3, EmitDefaultValue = false)] public DateTime? From { get; set; }
        /// <summary>«за период» — конец.</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public DateTime? To { get; set; }

        /// <summary>Модель выгружать: отбор выключен, дата не известна или изменена в срок.</summary>
        public bool Passes(DateTime? modified)
        {
            if (!On || modified == null) return true;
            var m = modified.Value;
            switch (Mode)
            {
                case Before: return From == null || m < From.Value.Date.AddDays(1);
                case Between: return (From == null || m >= From.Value.Date) && (To == null || m < To.Value.Date.AddDays(1));
                default: return From == null || m >= From.Value.Date;
            }
        }

        public ModelDateFilter Clone() => (ModelDateFilter)MemberwiseClone();
    }

    /// <summary>Параметры выгрузки строки дерева вместо общих: экспортёр NWC и что удалить из модели перед выгрузкой.</summary>
    [DataContract]
    public sealed class ExportOverride
    {
        [DataMember(Order = 1)] public NwcOptions Nwc { get; set; } = new NwcOptions();
        [DataMember(Order = 2)] public bool RemoveRevitLinks { get; set; }
        [DataMember(Order = 3)] public bool RemoveCad { get; set; }
        [DataMember(Order = 4)] public bool RemovePointClouds { get; set; }
        [DataMember(Order = 5)] public bool RemoveIfc { get; set; }

        public static ExportOverride From(ExportSettings s) => new ExportOverride
        {
            Nwc = s.Nwc.Clone(), RemoveRevitLinks = s.RemoveRevitLinks, RemoveCad = s.RemoveCad, RemovePointClouds = s.RemovePointClouds,
            RemoveIfc = s.RemoveIfc,
        };

        /// <summary>Общие настройки с этими параметрами выгрузки (метки, отделы, обрезка имени — общие).</summary>
        public ExportSettings ApplyTo(ExportSettings common)
        {
            var s = common.Clone();
            s.Nwc = (Nwc ?? new NwcOptions()).Clone();
            s.RemoveRevitLinks = RemoveRevitLinks;
            s.RemoveCad = RemoveCad;
            s.RemovePointClouds = RemovePointClouds;
            s.RemoveIfc = RemoveIfc;
            return s;
        }

        public ExportOverride Clone() => new ExportOverride
        {
            Nwc = (Nwc ?? new NwcOptions()).Clone(), RemoveRevitLinks = RemoveRevitLinks, RemoveCad = RemoveCad,
            RemovePointClouds = RemovePointClouds, RemoveIfc = RemoveIfc,
        };
    }

    /// <summary>Блок: имя (пусто — «без блока») и его модели.</summary>
    public sealed class BlockGroup
    {
        public string Name { get; set; }
        public List<ExportModel> Models { get; set; } = new List<ExportModel>();
    }

    /// <summary>Строка главного окна: задача или подзадача.</summary>
    public sealed class TreeRow
    {
        public string Number { get; set; }
        public int Level { get; set; }
        public ExportTask Item { get; set; }
    }

    /// <summary>
    /// Столбцы строки дерева (разработчик, 05.11.2026: «список — таблицей»): подзадачи, модели, проверки, NWF; «—» — нет.
    /// *From — значение от строки выше (её название), null — своё.
    /// </summary>
    public sealed class RowColumns
    {
        public string Subtasks { get; set; }
        public string Models { get; set; }
        public string ModelsFrom { get; set; }
        public string Tests { get; set; }
        public string TestsFrom { get; set; }
        public string Nwf { get; set; }
        /// <summary>Строка сама собирает NWF (имя — рядом с галочкой).</summary>
        public bool NwfOn { get; set; }
        /// <summary>Выключатели (ExportNodes.FlagState): да / нет / у подзадач по-разному (null).</summary>
        public bool? NwcState { get; set; }
        public bool? NwfState { get; set; }
        public bool? ReportsState { get; set; }
        public bool? RvtState { get; set; }
        public bool? ResaveState { get; set; }
        /// <summary>Отчёты — у строк без подзадач (и без «NWF» — по готовой NWF); ни одной — галочка серая.</summary>
        public bool ReportsEnabled { get; set; }
    }

    /// <summary>Выключатели списка задач (разработчик, 07.11.2026: «выключатель должен быть у всего»).</summary>
    public enum RowFlag { Nwc, Nwf, Reports, Rvt, Resave }

    /// <summary>Настройка строки по дереву, блоки, где что лежит, строки окна, проверки окна настроек.</summary>
    public static class ExportNodes
    {
        /// <summary>Метки, которые можно ставить в строки (подсказка под полем и проверка при сохранении).</summary>
        public static readonly string[] NwcFolderLabels = { "Блок", "Отдел" };
        // {Блок} — только в папке для NWC (по каждой модели): в NWF и отчётах его нет (разработчик, 03.11.2026: «метку блок и
        // проверку „из разных блоков“ — убрать»)
        public static readonly string[] NwfFolderLabels = new string[0];
        public static readonly string[] NwfNameLabels = new string[0];
        public static readonly string[] ReportFolderLabels = { "Дата", "Формат" };
        public static readonly string[] RvtFolderLabels = { "Блок", "Отдел", "Дата" };

        // верхняя задача без значения (файл от прежней версии) — по умолчанию
        private static readonly NodeSettings Defaults = NodeSettings.Top();

        public static ExportTask Root(ExportTask task)
        {
            while (task.Parent != null) task = task.Parent;
            return task;
        }

        // ── настройка по дереву: своя или ближайшего уровня выше ──

        /// <summary>Где задана настройка: сама строка или ближайшая выше; нигде — null.</summary>
        public static ExportTask OwnerOf(ExportTask task, Func<NodeSettings, object> field)
        {
            for (var t = task; t != null; t = t.Parent)
                if (field(t.Settings) != null) return t;
            return null;
        }

        private static T Get<T>(ExportTask task, Func<NodeSettings, T> field) where T : class
        {
            var owner = OwnerOf(task, field);
            return owner != null ? field(owner.Settings) : field(Defaults);
        }

        public static List<ExportModel> ModelsOf(ExportTask task) => Get(task, s => s.Models);
        public static List<string> TestsOf(ExportTask task) => Get(task, s => s.TestsFiles);
        public static List<HideSet> HideSetsOf(ExportTask task) => Get(task, s => s.HideSets);
        /// <summary>Отбор моделей по дате изменения: свой или уровня выше; нигде — нет (выгружать все).</summary>
        public static ModelDateFilter EditedOf(ExportTask task) => OwnerOf(task, s => s.Edited)?.Settings.Edited;
        /// <summary>
        /// Строка выгружает отчёты: «Формировать отчёты» у строки без подзадач. Без «NWF» — по готовой NWF строки (разработчик,
        /// 08.11.2026: «каждая галочка работает и по отдельности: стоят только отчёты — плагин находит NWF, обновляет проверки и
        /// выгружает отчёты»).
        /// </summary>
        public static bool MakesReports(ExportTask task) => task.Settings.MakeReports && task.Children.Count == 0;
        /// <summary>Имя NWF строки — своё (не наследуется), пусто — название строки.</summary>
        public static string NwfNameOf(ExportTask task) =>
            (string.IsNullOrWhiteSpace(task.Settings.NwfName) ? task.Name ?? "" : task.Settings.NwfName).Trim();
        /// <summary>Строка собирает NWF: галочка «Формировать NWF» и нет подзадач.</summary>
        public static bool MakesNwf(ExportTask task) => task.Settings.MakeNwf && task.Children.Count == 0;
        public static string FolderOf(ExportTask task) => Get(task, s => s.Folder);
        public static string NwcFolderOf(ExportTask task) => Get(task, s => s.NwcFolder);
        public static string NwfFolderOf(ExportTask task) => Get(task, s => s.NwfFolder);
        public static string ReportFolderOf(ExportTask task) => Get(task, s => s.ReportFolder);
        public static string RvtFolderOf(ExportTask task) => Get(task, s => s.RvtFolder) ?? "";
        public static string StartServerFolder(ExportTask task) => Get(task, s => s.ServerFolder) ?? "";
        public static List<string> FormatsOf(ExportTask task) => Get(task, s => s.ReportFormats);

        /// <summary>Параметры выгрузки строки: свои или уровня выше поверх общих; нигде — общие.</summary>
        public static ExportSettings ExportOf(ExportTask task, ExportSettings common) =>
            OwnerOf(task, s => s.Export)?.Settings.Export.ApplyTo(common) ?? common;

        /// <summary>Форматы отчётов Clash Detective — как в Navisworks 2023 (ru-RU\LcOdpClash.name); значение {Формат} — это название.</summary>
        public static readonly string[] ReportFormatNames = { "XML", "HTML", "HTML (Табличное представление)", "Текст", "Как точки обзора" };

        /// <summary>Вид для выгрузки: заданный в дереве или из общих.</summary>
        public static string ViewOf(ExportTask task, ExportSettings common) => OwnerOf(task, s => s.ViewName)?.Settings.ViewName ?? common.Nwc.ViewName;

        // ── блоки ──

        /// <summary>Блок модели: вписанный руками, иначе — из имени («S1»); нет — пусто.</summary>
        public static string BlockOf(ExportModel model, ExportSettings common) =>
            !string.IsNullOrWhiteSpace(model.Block) ? model.Block.Trim() : ExportNames.Block(model.Path, common.BlockPrefix);

        /// <summary>Блоки по моделям — по порядку номеров (S1, S2, S10); модели без блока — последней группой с пустым именем.</summary>
        public static List<BlockGroup> Blocks(IEnumerable<ExportModel> models, ExportSettings common)
        {
            var groups = models.GroupBy(m => BlockOf(m, common), StringComparer.OrdinalIgnoreCase)
                .Select(g => new BlockGroup { Name = g.Key, Models = g.ToList() }).ToList();
            groups.Sort((a, b) => a.Name.Length == 0 ? 1 : b.Name.Length == 0 ? -1 : NaturalCompare(a.Name, b.Name));
            return groups;
        }

        /// <summary>Сравнение «по-человечески»: S2 раньше S10.</summary>
        public static int NaturalCompare(string a, string b)
        {
            var ra = Regex.Split(a ?? "", @"(\d+)");
            var rb = Regex.Split(b ?? "", @"(\d+)");
            for (var i = 0; i < Math.Min(ra.Length, rb.Length); i++)
            {
                int c;
                if (long.TryParse(ra[i], out var na) && long.TryParse(rb[i], out var nb)) c = na.CompareTo(nb);
                else c = string.Compare(ra[i], rb[i], StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
            }
            return ra.Length.CompareTo(rb.Length);
        }

        // ── строки главного окна ──

        /// <summary>
        /// Строки главного окна: задача «1», подзадачи «1.1», «1.1.1»…; свёрнутые (collapsed) — без вложенных. Номера ставятся сами.
        /// </summary>
        public static List<TreeRow> Rows(IList<ExportTask> roots, ISet<ExportTask> collapsed)
        {
            var rows = new List<TreeRow>();
            for (var i = 0; i < roots.Count; i++) AddRows(rows, roots[i], (i + 1).ToString(), 0, collapsed);
            return rows;
        }

        private static void AddRows(List<TreeRow> rows, ExportTask task, string number, int level, ISet<ExportTask> collapsed)
        {
            rows.Add(new TreeRow { Number = number, Level = level, Item = task });
            if (collapsed.Contains(task)) return;
            for (var i = 0; i < task.Children.Count; i++) AddRows(rows, task.Children[i], number + "." + (i + 1), level + 1, collapsed);
        }

        /// <summary>Краткая информация строки: подзадачи, модели (свои или от кого), файлы проверок, NWF — если строка её собирает.</summary>
        public static RowColumns Columns(TreeRow row)
        {
            var task = row.Item;
            const string none = "—";
            var models = ModelsOf(task) ?? new List<ExportModel>();
            var tests = TestsOf(task) ?? new List<string>();
            return new RowColumns
            {
                Subtasks = task.Children.Count > 0 ? task.Children.Count.ToString() : none,
                Models = models.Count.ToString(),
                ModelsFrom = InheritedFrom(task, s => s.Models),
                Tests = tests.Count > 0 ? string.Join(", ", tests.Select(FileName)) : none,
                TestsFrom = tests.Count > 0 ? InheritedFrom(task, s => s.TestsFiles) : null,
                Nwf = MakesNwf(task) ? NwfNameOf(task) + ".nwf" : none,
                NwfOn = MakesNwf(task),
                NwcState = FlagState(task, RowFlag.Nwc),
                NwfState = FlagState(task, RowFlag.Nwf),
                ReportsState = FlagState(task, RowFlag.Reports),
                ReportsEnabled = FlagRows(task, RowFlag.Reports).Any(),
                RvtState = FlagState(task, RowFlag.Rvt),
                ResaveState = FlagState(task, RowFlag.Resave),
            };
        }

        // ── выключатели списка задач ──

        /// <summary>
        /// К каким строкам относится выключатель: NWC — строка и все ниже (у строки с подзадачами — её модели, которых нет ниже);
        /// NWF и отчёты — строки без подзадач (каждая галочка — и по отдельности, 08.11.2026); RVT — как NWC (08.11.2026).
        /// </summary>
        private static IEnumerable<ExportTask> FlagRows(ExportTask task, RowFlag flag) =>
            flag == RowFlag.Nwc || flag == RowFlag.Rvt || flag == RowFlag.Resave ? task.Self() : task.Self().Where(t => t.Children.Count == 0);

        private static bool FlagOf(ExportTask t, RowFlag flag) =>
            flag == RowFlag.Nwc ? !t.Settings.NoNwc : flag == RowFlag.Nwf ? t.Settings.MakeNwf
            : flag == RowFlag.Rvt ? t.Settings.MakeRvt : flag == RowFlag.Resave ? t.Settings.Resave : t.Settings.MakeReports;

        private static void SetFlagOf(ExportTask t, RowFlag flag, bool on)
        {
            if (flag == RowFlag.Nwc) t.Settings.NoNwc = !on;
            else if (flag == RowFlag.Nwf) t.Settings.MakeNwf = on;
            else if (flag == RowFlag.Rvt) t.Settings.MakeRvt = on;
            else if (flag == RowFlag.Resave) t.Settings.Resave = on;
            else t.Settings.MakeReports = on;
        }

        /// <summary>Выключатель строки: у всех её строк включено — да, ни у одной — нет, по-разному — null; строк нет — нет.</summary>
        public static bool? FlagState(ExportTask task, RowFlag flag)
        {
            var rows = FlagRows(task, flag).ToList();
            var on = rows.Count(t => FlagOf(t, flag));
            return rows.Count == 0 || on == 0 ? false : on == rows.Count ? true : (bool?)null;
        }

        /// <summary>
        /// Выключатель в списке (у строки с подзадачами — сразу у всех ниже), с теми же проверками, что при «Сохранить» в панели;
        /// ошибка — всё как было, текст ошибки; null — сделано. Заменяет SetNwf (06.11.2026, только NWF и только у строки без подзадач).
        /// </summary>
        public static string SetFlag(ExportTask task, RowFlag flag, bool on, ExportSettings common)
        {
            var rows = FlagRows(task, flag).ToList();
            if (rows.Count == 0) return "Здесь нечего включать.";
            var was = rows.Select(t => FlagOf(t, flag)).ToList();
            foreach (var t in rows) SetFlagOf(t, flag, on);
            string error = null;
            if (flag == RowFlag.Nwf && on)
                error = rows.Select(t => CheckNwfName(NwfNameOf(t)) is string e ? (rows.Count > 1 ? "«" + t.Name + "»: " : "") + e : null)
                            .FirstOrDefault(e => e != null)
                        ?? CheckNwf(Root(task), common);
            if (error != null)
                for (var i = 0; i < rows.Count; i++) SetFlagOf(rows[i], flag, was[i]);
            return error;
        }

        /// <summary>Название строки выше, от которой взято значение; своё или у задачи — null.</summary>
        private static string InheritedFrom(ExportTask task, Func<NodeSettings, object> field)
        {
            var owner = OwnerOf(task, field);
            return task.Parent == null || owner == null || owner == task ? null : owner.Name;
        }

        public static string Summary(TreeRow row)
        {
            var task = row.Item;
            var parts = new List<string>();
            if (task.Children.Count > 0) parts.Add(task.Children.Count + " " + ExportTask.SubtasksWord(task.Children.Count));
            var models = ModelsOf(task);
            var owner = OwnerOf(task, s => s.Models);
            parts.Add(models.Count + " " + ExportTask.ModelsWord(models.Count) +
                      (task.Parent == null || owner == task || owner == null ? "" : " от «" + owner.Name + "»"));
            var tests = TestsOf(task);
            if (tests.Count > 0) parts.Add(string.Join(", ", tests.Select(FileName)));
            if (MakesNwf(task)) parts.Add(NwfNameOf(task) + ".nwf");
            return string.Join(" · ", parts);
        }

        // ── где что лежит ──

        /// <summary>Метки строки папки: {Блок}, {Отдел} — по модели (нет модели — пусто), {Дата}, {Формат}.</summary>
        public static Dictionary<string, string> Labels(string block, ExportModel model, ExportSettings common, DateTime date, string format = "") =>
            new Dictionary<string, string>
            {
                ["Блок"] = block ?? "",
                ["Отдел"] = model == null ? "" : ExportNames.Department(model.Path, common.Departments.Select(d => d.Department)),
                ["Дата"] = DateLabel(date, common.DateFormat),
                ["Формат"] = format ?? "",
            };

        /// <summary>
        /// {Дата} — дата в выбранном формате и время записи («08.11.2026 17-55»; разработчик, 08.11.2026: «не перезаписывать проверки,
        /// сделанные в одну дату — пусть папка с датой создаётся ещё и со временем»). Двоеточия в имени папки быть не может — дефис.
        /// {Дата} — только в папке отчётов (NWC и NWF — постоянные).
        /// </summary>
        public static string DateLabel(DateTime when, string format) => when.ToString(format) + " " + when.ToString("HH-mm");

        /// <summary>Строка папки — внутри папки проекта; полный путь («\\сервер\…», «D:\…») — как есть.</summary>
        public static string InProject(string projectFolder, string folder)
        {
            var f = (folder ?? "").Trim();
            if (f.StartsWith(@"\\") || Regex.IsMatch(f, @"^[A-Za-z]:")) return f;
            var root = (projectFolder ?? "").Trim().TrimEnd('\\');
            return root.Length == 0 ? f : f.Length == 0 ? root : root + "\\" + f.TrimStart('\\');
        }

        /// <summary>Файл NWC модели строки — по папке проекта и папке для NWC этой строки (у ветки обычно общие — один NWC на все критичности).</summary>
        public static string NwcPath(ExportTask task, ExportModel model, ExportSettings common) =>
            InProject(FolderOf(task), ExportNames.Fill(NwcFolderOf(task), Labels(BlockOf(model, common), model, common, DateTime.Today))) + "\\" +
            NwcNameOf(model, common) + ".nwc";

        /// <summary>Постоянная NWF строки: папка — строка по дереву, имя — своё или название строки.</summary>
        public static string NwfPath(ExportTask task, ExportSettings common)
        {
            var labels = Labels("", null, common, DateTime.Today);
            return InProject(FolderOf(task), ExportNames.Fill(NwfFolderOf(task), labels)) + "\\" + ExportNames.Fill(NwfNameOf(task), labels).Trim() + ".nwf";
        }

        /// <summary>
        /// RVT модели строки (выгрузка RVT, 08.11.2026): папка — «Папка для RVT» по дереву с метками {Блок}, {Отдел}, {Дата} (время
        /// запуска), пусто — папка задачи; имя — из имени модели по правилу общих настроек («обрезать всё после …», пусто — как есть).
        /// </summary>
        public static string RvtPath(ExportTask task, ExportModel model, ExportSettings common, DateTime runDate) =>
            InProject(FolderOf(task), ExportNames.Fill(RvtFolderOf(task), Labels(BlockOf(model, common), model, common, runDate))) + "\\" +
            RvtNameOf(model, RvtOptionsOf(task, common)) + ".rvt";

        /// <summary>Имя RVT модели: из имени модели по правилу «Экспорта RVT» (своё «обрезать всё после …»; пусто — имя модели).</summary>
        public static string RvtNameOf(ExportModel model, ExportSettings common) => RvtNameOf(model, common.Rvt);

        public static string RvtNameOf(ExportModel model, RvtOptions rvt) =>
            ExportNames.NwcName(model.Path, (rvt ?? new RvtOptions()).TrimAfter, out _);

        /// <summary>«Настройки RVT» строки: свои или уровня выше (панель → «Изменить»); нигде — общие.</summary>
        public static RvtOptions RvtOptionsOf(ExportTask task, ExportSettings common) =>
            OwnerOf(task, s => s.Rvt)?.Settings.Rvt ?? common.Rvt ?? new RvtOptions();

        /// <summary>«Настройки пересохранения» строки: свои или уровня выше; нигде — общие.</summary>
        public static ResaveOptions ResaveOptionsOf(ExportTask task, ExportSettings common) =>
            OwnerOf(task, s => s.ResaveOptions)?.Settings.ResaveOptions ?? common.Resave ?? new ResaveOptions();

        /// <summary>Папка отчётов строки на дату запуска (одна на весь запуск) и формат («XML», «HTML»).</summary>
        public static string ReportPath(ExportTask task, ExportSettings common, DateTime runDate, string format) =>
            InProject(FolderOf(task), ExportNames.Fill(ReportFolderOf(task), Labels("", null, common, runDate, format)));

        // ── модели ──

        /// <summary>Добавить модели в список без повторов; сколько добавлено.</summary>
        public static int AddModels(List<ExportModel> models, IEnumerable<string> paths) =>
            AddModels(models, paths.Select(p => new ExportModel { Path = p }));

        /// <summary>Добавить модели (с версией Revit) без повторов по пути; сколько добавлено.</summary>
        public static int AddModels(List<ExportModel> models, IEnumerable<ExportModel> picked)
        {
            var added = 0;
            foreach (var m in picked)
            {
                if (string.IsNullOrWhiteSpace(m.Path) ||
                    models.Any(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase))) continue;
                models.Add(m.Clone());
                added++;
            }
            return added;
        }

        /// <summary>
        /// Свои модели строки — и в списки уровней выше (разработчик, 03.11.2026: «модели из подзадач передаются в задачу»): у
        /// каждой строки выше со своим списком — чего в нём нет. Сколько добавлено.
        /// </summary>
        public static int PushModelsUp(ExportTask task)
        {
            var own = task.Settings.Models;
            if (own == null) return 0;
            var added = 0;
            for (var up = task.Parent; up != null; up = up.Parent)
            {
                var list = up.Settings.Models;
                if (list == null) continue;
                foreach (var m in own)
                    if (!list.Any(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(m.Clone());
                        added++;
                    }
            }
            return added;
        }

        /// <summary>Папка проекта на Revit Server по модели: «RSN://сервер/первая папка» — для «Добавить модели…»; не сервер — пусто.</summary>
        public static string ServerFolderOf(string modelPath)
        {
            if (!IsServerPath(modelPath)) return "";
            var parts = modelPath.Trim().Substring("RSN://".Length).Split('/');
            return parts.Length >= 3 ? "RSN://" + parts[0] + "/" + parts[1] : "";
        }

        /// <summary>Имя NWC модели: поправленное руками или из имени модели по общему правилу.</summary>
        public static string NwcNameOf(ExportModel model, ExportSettings common) =>
            !string.IsNullOrWhiteSpace(model.NwcName)
                ? model.NwcName.Trim()
                : ExportNames.NwcName(model.Path, common.TrimAfter, out _);

        /// <summary>Путь Revit Server: «RSN://сервер/папка/модель.rvt».</summary>
        public static bool IsServerPath(string path) =>
            (path ?? "").Trim().StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) &&
            (path ?? "").Trim().EndsWith(".rvt", StringComparison.OrdinalIgnoreCase);

        // ── проверки окна настроек ──

        /// <summary>Папка для NWC: только {Блок}, {Отдел} — даты нет, NWF ищет NWC по постоянному пути; null — всё верно.</summary>
        public static string CheckNwcFolder(string folder) =>
            CheckLabels(folder, "Папка для NWC", NwcFolderLabels, "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка для NWF: без меток — NWF постоянная (регламент); null — всё верно.</summary>
        public static string CheckNwfFolder(string folder) =>
            CheckLabels(folder, "Папка для NWF", NwfFolderLabels, "NWF постоянная — папка без меток");

        /// <summary>Имя NWF (уже с названием строки, если поле пусто): без меток, знаки — как в имени файла; null — всё верно.</summary>
        public static string CheckNwfName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Нужно имя NWF.";
            var labels = CheckLabels(name, "Имя NWF", NwfNameLabels, "NWF постоянная — имя без меток");
            if (labels != null) return labels;
            var plain = ExportNames.Fill(name, NwfNameLabels.ToDictionary(l => l, l => "x"));
            return plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Имя NWF: недопустимые знаки в имени файла." : null;
        }

        /// <summary>
        /// Строки, которые собирают NWF, — по всему дереву: есть модели; две строки не пишут в одну NWF (копия со старым именем).
        /// null — всё верно.
        /// </summary>
        public static string CheckNwf(ExportTask root, ExportSettings common)
        {
            var seen = new Dictionary<string, ExportTask>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in root.Self().Where(MakesNwf))
            {
                if (ModelsOf(t).Count == 0) return "«" + t.Name + "»: моделей нет — NWF не из чего собрать.";
                var path = NwfPath(t, common);
                if (seen.TryGetValue(path, out var other))
                    return "NWF «" + FileName(path) + "» — и у «" + other.Name + "», и у «" + t.Name + "»: поменяйте имя или папку NWF.";
                seen[path] = t;
            }
            return null;
        }

        /// <summary>Папка для RVT: {Блок}, {Отдел}, {Дата}; null — всё верно.</summary>
        public static string CheckRvtFolder(string folder) =>
            CheckLabels(folder, "Папка для RVT", RvtFolderLabels, "в папке RVT — только {Блок}, {Отдел} и {Дата}");

        /// <summary>Папка для отчётов: только {Дата}, {Формат}; null — всё верно.</summary>
        public static string CheckReportFolder(string folder) =>
            CheckLabels(folder, "Папка для отчётов", ReportFolderLabels, "в папке отчётов — только {Дата} и {Формат}");

        private static string CheckLabels(string text, string what, string[] allowed, string why)
        {
            var labels = ExportNames.LabelsIn(text);
            var unknown = labels.FirstOrDefault(l => !ExportNames.BuiltInLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
            if (unknown != null)
                return what + ": метки {" + unknown + "} нет — есть только " +
                       string.Join(", ", ExportNames.BuiltInLabels.Select(l => "{" + l + "}")) + ".";
            var forbidden = labels.FirstOrDefault(l => !allowed.Contains(l, StringComparer.OrdinalIgnoreCase));
            return forbidden != null ? what + ": {" + forbidden + "} — нельзя, " + why + "." : null;
        }

        /// <summary>Название: есть и не повторяется рядом — две одинаковые строки одного уровня не различить; null — всё верно.</summary>
        public static string CheckName(string name, IEnumerable<string> neighbours)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Нужно название.";
            return neighbours.Any(o => string.Equals((o ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase))
                ? "«" + n + "» на этом уровне уже есть — назовите иначе."
                : null;
        }

        /// <summary>
        /// Файлы проверок: .xml, без повторов; одно название проверки — только в одном файле (отчёт называется по проверке, в
        /// одной папке прогона отчёты заменили бы друг друга). names — названия проверок файла; не прочитался — исключение,
        /// файл тогда не сравнивается (сеть недоступна — сохранить всё равно можно). null — всё верно.
        /// </summary>
        public static string CheckTestsFiles(IList<string> files, Func<string, IEnumerable<string>> names)
        {
            var notXml = files.FirstOrDefault(f => !f.Trim().EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (notXml != null) return "Файл проверок «" + FileName(notXml) + "» — не .xml из Clash Detective.";
            var twice = files.GroupBy(f => f.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) return "Файл проверок «" + FileName(twice.Key) + "» — дважды.";
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                IEnumerable<string> list;
                try
                {
                    list = names(f);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var n in list)
                {
                    var key = Regex.Replace(n.Trim(), @"\s+", " ");
                    if (seen.TryGetValue(key, out var other) && !string.Equals(other, f, StringComparison.OrdinalIgnoreCase))
                        return "Проверка «" + n.Trim() + "» — и в «" + FileName(other) + "», и в «" + FileName(f) + "»: отчёты заменят друг друга.";
                    seen[key] = f;
                }
            }
            return null;
        }

        /// <summary>
        /// Имена NWC моделей строки: допустимые знаки, и две модели не ложатся в один файл (имена в одной папке должны
        /// различаться); null — всё верно.
        /// </summary>
        public static string CheckNwcNames(ExportTask task, ExportSettings common)
        {
            var models = ModelsOf(task);
            foreach (var m in models)
            {
                var name = NwcNameOf(m, common);
                if (string.IsNullOrWhiteSpace(name)) return "У модели «" + FileName(m.Path) + "» пустое имя NWC.";
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "В имени NWC «" + name + "» — недопустимые знаки.";
            }
            var twice = models.GroupBy(m => NwcPath(task, m, common), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return twice == null ? null
                : "Модели «" + string.Join("», «", twice.Select(m => FileName(m.Path))) + "» дают один NWC «" + FileName(twice.Key) + "»: файлы заменят друг друга.";
        }

        /// <summary>Коды отделов: буквы, без повторов (ищутся в имени модели как «KJ» или «KJ01»); null — всё верно.</summary>
        public static string CheckDepartmentCodes(IEnumerable<string> codes)
        {
            var list = codes.Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).ToList();
            var bad = list.FirstOrDefault(c => !c.All(char.IsLetter));
            if (bad != null) return "Код отдела «" + bad + "» — только буквы: цифры после кода в имени модели найдутся сами (KJ → KJ01).";
            var twice = list.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return twice == null ? null : "Отдел «" + twice.Key + "» — дважды.";
        }

        /// <summary>
        /// Имя файла для сообщений — всё после последней косой черты: Path.GetFileName в .NET 8 у «\\сервер\файл» отдаёт пусто
        /// (считает его корнем сетевой папки).
        /// </summary>
        public static string FileName(string path)
        {
            var s = (path ?? "").Trim();
            var i = s.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }
    }
}
