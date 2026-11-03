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
        /// <summary>Папка для NWF — строка с {Блок}; NWF постоянная (регламент 22.10): обновляется, а не собирается заново.</summary>
        [DataMember(Order = 7, EmitDefaultValue = false)] public string NwfFolder { get; set; }
        /// <summary>Папка для отчётов — строка с {Блок}, {Дата}, {Формат}.</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)] public string ReportFolder { get; set; }
        /// <summary>Форматы отчётов — оба свои или оба от уровня выше.</summary>
        [DataMember(Order = 9, EmitDefaultValue = false)] public bool? Xml { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public bool? Html { get; set; }
        /// <summary>Вид для выгрузки; нигде не задан — из общих настроек.</summary>
        [DataMember(Order = 11, EmitDefaultValue = false)] public string ViewName { get; set; }
        /// <summary>
        /// «Формировать NWF» — своё у строки, не наследуется; действует только у строки без подзадач (разработчик, 03.11.2026:
        /// «подзадача — как отдельный блок, обычно 4 модели: добавляем их, ставим галочку — они объединяются в NWF»).
        /// </summary>
        [DataMember(Order = 12, EmitDefaultValue = false)] public bool MakeNwf { get; set; }
        /// <summary>
        /// Скрыть в NWF элементы, имя которых содержит одну из строк (разработчик, 03.11.2026: «временные» скрываем — скрытые в
        /// проверки не попадают; «перенести в настройки задачи, передаётся подзадачам»). Своих строк компании в коде нет.
        /// </summary>
        [DataMember(Order = 13, EmitDefaultValue = false)] public List<string> HideNames { get; set; }

        /// <summary>Задача верхнего уровня: всё своё, по умолчанию — регламент 15.10 (вида нет — из общих настроек).</summary>
        public static NodeSettings Top() => new NodeSettings
        {
            Models = new List<ExportModel>(), TestsFiles = new List<string>(), HideNames = new List<string>(), Folder = "", NwcFolder = @"{Блок}\NWC",
            NwfFolder = "{Блок}", ReportFolder = @"{Блок}\{Дата}", Xml = true, Html = true,
        };

        public NodeSettings Clone() => new NodeSettings
        {
            Models = Models?.Select(m => new ExportModel { Path = m.Path, NwcName = m.NwcName, Block = m.Block }).ToList(),
            TestsFiles = TestsFiles?.ToList(), NwfName = NwfName, Folder = Folder, ServerFolder = ServerFolder, NwcFolder = NwcFolder,
            NwfFolder = NwfFolder, ReportFolder = ReportFolder, Xml = Xml, Html = Html, ViewName = ViewName, MakeNwf = MakeNwf,
            HideNames = HideNames?.ToList(),
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

    /// <summary>Настройка строки по дереву, блоки, где что лежит, строки окна, проверки окна настроек.</summary>
    public static class ExportNodes
    {
        /// <summary>Метки, которые можно ставить в строки (подсказка под полем и проверка при сохранении).</summary>
        public static readonly string[] NwcFolderLabels = { "Блок", "Отдел" };
        public static readonly string[] NwfFolderLabels = { "Блок" };
        public static readonly string[] NwfNameLabels = { "Блок" };
        public static readonly string[] ReportFolderLabels = { "Блок", "Дата", "Формат" };

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
        public static List<string> HideNamesOf(ExportTask task) => Get(task, s => s.HideNames);
        /// <summary>Имя NWF строки — своё (не наследуется), пусто — название строки.</summary>
        public static string NwfNameOf(ExportTask task) =>
            (string.IsNullOrWhiteSpace(task.Settings.NwfName) ? task.Name ?? "" : task.Settings.NwfName).Trim();
        /// <summary>Строка собирает NWF: галочка «Формировать NWF» и нет подзадач.</summary>
        public static bool MakesNwf(ExportTask task) => task.Settings.MakeNwf && task.Children.Count == 0;
        public static string FolderOf(ExportTask task) => Get(task, s => s.Folder);
        public static string NwcFolderOf(ExportTask task) => Get(task, s => s.NwcFolder);
        public static string NwfFolderOf(ExportTask task) => Get(task, s => s.NwfFolder);
        public static string ReportFolderOf(ExportTask task) => Get(task, s => s.ReportFolder);
        public static string StartServerFolder(ExportTask task) => Get(task, s => s.ServerFolder) ?? "";
        public static bool XmlOf(ExportTask task) => OwnerOf(task, s => s.Xml)?.Settings.Xml ?? true;
        public static bool HtmlOf(ExportTask task) => OwnerOf(task, s => s.Html)?.Settings.Html ?? true;

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

        /// <summary>
        /// {Блок} строки — для папок NWF и отчётов: блок, общий у всех её моделей (S1_ВК — модели S1 → «S1»); разные блоки или
        /// моделей нет — пусто.
        /// </summary>
        public static string BlockOfTask(ExportTask task, ExportSettings common)
        {
            var blocks = ModelsOf(task).Select(m => BlockOf(m, common)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return blocks.Count == 1 ? blocks[0] : "";
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
        public static string Summary(TreeRow row)
        {
            var task = row.Item;
            var parts = new List<string>();
            if (task.Children.Count > 0) parts.Add(task.Children.Count + " " + ExportTask.SubtasksWord(task.Children.Count));
            var models = ModelsOf(task);
            var owner = OwnerOf(task, s => s.Models);
            parts.Add(models.Count + " " + ExportTask.ModelsWord(models.Count) +
                      (task.Parent == null ? "" : owner == task ? " (свои)" : owner != null ? " от «" + owner.Name + "»" : ""));
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
                ["Дата"] = date.ToString(common.DateFormat),
                ["Формат"] = format ?? "",
            };

        /// <summary>Строка папки — внутри папки проекта; полный путь («\\сервер\…», «D:\…») — как есть.</summary>
        public static string InProject(string projectFolder, string folder)
        {
            var f = (folder ?? "").Trim();
            if (f.StartsWith(@"\\") || Regex.IsMatch(f, @"^[A-Za-z]:")) return f;
            var root = (projectFolder ?? "").Trim().TrimEnd('\\');
            return root.Length == 0 ? f : f.Length == 0 ? root : root + "\\" + f.TrimStart('\\');
        }

        /// <summary>
        /// Путь для примера под полем: внутри папки проекта — «…\S1\NWC\…» (папка проекта и так видна выше; разработчик,
        /// 02.11.2026: «полный путь не писать — троеточием отметить, что до этого места всё есть»); вне — целиком.
        /// </summary>
        public static string Short(string projectFolder, string path)
        {
            var root = (projectFolder ?? "").Trim().TrimEnd('\\');
            return root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? "…\\" + path.Substring(root.Length + 1)
                : path;
        }

        /// <summary>Файл NWC модели строки — по папке проекта и папке для NWC этой строки (у ветки обычно общие — один NWC на все критичности).</summary>
        public static string NwcPath(ExportTask task, ExportModel model, ExportSettings common) =>
            InProject(FolderOf(task), ExportNames.Fill(NwcFolderOf(task), Labels(BlockOf(model, common), model, common, DateTime.Today))) + "\\" +
            NwcNameOf(model, common) + ".nwc";

        /// <summary>Постоянная NWF строки: папка — строка по дереву ({Блок} — общий блок моделей строки), имя — своё или название строки.</summary>
        public static string NwfPath(ExportTask task, ExportSettings common)
        {
            var labels = Labels(BlockOfTask(task, common), null, common, DateTime.Today);
            return InProject(FolderOf(task), ExportNames.Fill(NwfFolderOf(task), labels)) + "\\" + ExportNames.Fill(NwfNameOf(task), labels).Trim() + ".nwf";
        }

        /// <summary>Папка отчётов строки на дату запуска (одна на весь запуск) и формат («XML», «HTML»).</summary>
        public static string ReportPath(ExportTask task, ExportSettings common, DateTime runDate, string format) =>
            InProject(FolderOf(task), ExportNames.Fill(ReportFolderOf(task), Labels(BlockOfTask(task, common), null, common, runDate, format)));

        // ── модели ──

        /// <summary>Добавить модели в список без повторов; сколько добавлено.</summary>
        public static int AddModels(List<ExportModel> models, IEnumerable<string> paths)
        {
            var added = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    models.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                models.Add(new ExportModel { Path = path });
                added++;
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
                : ExportNames.NwcName(model.Path, common.TrimName, common.NameParts, out _);

        /// <summary>Путь Revit Server: «RSN://сервер/папка/модель.rvt».</summary>
        public static bool IsServerPath(string path) =>
            (path ?? "").Trim().StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) &&
            (path ?? "").Trim().EndsWith(".rvt", StringComparison.OrdinalIgnoreCase);

        // ── проверки окна настроек ──

        /// <summary>Папка для NWC: только {Блок}, {Отдел} — даты нет, NWF ищет NWC по постоянному пути; null — всё верно.</summary>
        public static string CheckNwcFolder(string folder) =>
            CheckLabels(folder, "Папка для NWC", NwcFolderLabels, "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка для NWF: только {Блок} — NWF постоянная (регламент), одна на блок; null — всё верно.</summary>
        public static string CheckNwfFolder(string folder) =>
            CheckLabels(folder, "Папка для NWF", NwfFolderLabels, "NWF постоянная — одна на блок, без даты и отдела");

        /// <summary>Имя NWF (уже с названием строки, если поле пусто): метка — только {Блок}, знаки — как в имени файла; null — всё верно.</summary>
        public static string CheckNwfName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Нужно имя NWF.";
            var labels = CheckLabels(name, "Имя NWF", NwfNameLabels, "NWF постоянная — без даты и отдела");
            if (labels != null) return labels;
            var plain = ExportNames.Fill(name, NwfNameLabels.ToDictionary(l => l, l => "x"));
            return plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Имя NWF: недопустимые знаки в имени файла." : null;
        }

        /// <summary>
        /// Строки, которые собирают NWF, — по всему дереву: есть модели; {Блок} в имени и папках NWF и отчётов — только когда модели
        /// строки из одного блока; две строки не пишут в одну NWF (копия со старым именем). null — всё верно.
        /// </summary>
        public static string CheckNwf(ExportTask root, ExportSettings common)
        {
            var seen = new Dictionary<string, ExportTask>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in root.Self().Where(MakesNwf))
            {
                var models = ModelsOf(t);
                if (models.Count == 0) return "«" + t.Name + "»: моделей нет — NWF не из чего собрать.";
                var withBlock = new[] { NwfNameOf(t), NwfFolderOf(t), ReportFolderOf(t) }
                    .Any(s => ExportNames.LabelsIn(s).Contains("Блок", StringComparer.OrdinalIgnoreCase));
                if (withBlock && BlockOfTask(t, common).Length == 0)
                {
                    var blocks = models.Select(m => BlockOf(m, common)).Select(b => b.Length == 0 ? "без блока" : b)
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    return "«" + t.Name + "»: модели из разных блоков (" + string.Join(", ", blocks) +
                           ") — {Блок} в имени или папках NWF и отчётов не подставить.";
                }
                var path = NwfPath(t, common);
                if (seen.TryGetValue(path, out var other))
                    return "NWF «" + FileName(path) + "» — и у «" + other.Name + "», и у «" + t.Name + "»: поменяйте имя или папку NWF.";
                seen[path] = t;
            }
            return null;
        }

        /// <summary>Папка для отчётов: только {Блок}, {Дата}, {Формат} — отчёт на блок, не на модель отдела; null — всё верно.</summary>
        public static string CheckReportFolder(string folder) =>
            CheckLabels(folder, "Папка для отчётов", ReportFolderLabels, "отчёт один на блок, а не на модель отдела");

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
