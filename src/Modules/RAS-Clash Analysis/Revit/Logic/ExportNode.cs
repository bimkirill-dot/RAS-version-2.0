using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Модель проекта: файл (сетевая папка) или путь Revit Server (RSN://…).</summary>
    public sealed class ExportModel
    {
        public string Path { get; set; }
        /// <summary>Имя NWC, поправленное руками; null — из имени модели по общему правилу.</summary>
        public string NwcName { get; set; }
        /// <summary>Блок, вписанный руками (в имени модели его нет — «Генплан.rvt»); null — из имени модели.</summary>
        public string Block { get; set; }
    }

    /// <summary>
    /// Настройки проекта (разработчик, 03.11.2026): папка проекта, папка для NWC — общие для всех задач проекта (одна модель —
    /// один NWC на обе критичности). Папки — строки внутри папки проекта (полный путь — тоже можно).
    /// </summary>
    public sealed class ProjectSettings
    {
        /// <summary>Папка проекта на сетевом диске — от неё строки папок.</summary>
        public string Folder { get; set; } = "";
        /// <summary>
        /// Папка проекта на Revit Server (RSN://сервер/папка) — откуда уже добавляли модели этого проекта: с неё открывается
        /// «Добавить модели…»; ставится сама, в окне её нет (разработчик, 02.11.2026: «убрать „Модели на сервере“ совсем»).
        /// </summary>
        public string ServerFolder { get; set; } = "";
        /// <summary>NWC — метки {Блок}, {Отдел}: в одну папку, по блокам или по отделам; даты нет — NWF ищет NWC по постоянному пути.</summary>
        public string NwcFolder { get; set; } = @"{Блок}\NWC";
        /// <summary>Вид для выгрузки; null — из общих настроек.</summary>
        public string ViewName { get; set; }
    }

    /// <summary>
    /// Настройки задачи (например, «Высокая критичность»; разработчик, 03.11.2026): всё, что зависит от регламента, задаёт
    /// человек — плагин сам ничего из этого не решает. Вторая критичность — копия задачи с другим файлом проверок; будущий
    /// регламент (обе проверки в одной NWF, «Отчеты\Дата») — правка строк.
    /// </summary>
    public sealed class TaskSettings
    {
        /// <summary>Файлы проверок Navisworks (.xml, выгрузка Clash Detective) — все в NWF блока.</summary>
        public List<string> TestsFiles { get; set; } = new List<string>();
        /// <summary>Имя NWF блока — строка с {Блок}: «{Блок}_ВК» → «S1_ВК.nwf».</summary>
        public string NwfName { get; set; } = "{Блок}";
        /// <summary>Папка для NWF — строка с {Блок}; постоянная (регламент 22.10): обновляется, а не собирается заново.</summary>
        public string NwfFolder { get; set; } = "{Блок}";
        /// <summary>Папка для отчётов — строка с {Блок}, {Дата}, {Формат}; по умолчанию — регламент 15.10.</summary>
        public string ReportFolder { get; set; } = @"{Блок}\{Дата}";
        public bool Xml { get; set; } = true;
        public bool Html { get; set; } = true;
        /// <summary>Блоки проекта, которые эта задача не делает; по умолчанию — делает все.</summary>
        public List<string> SkippedBlocks { get; set; } = new List<string>();

        public TaskSettings Clone() => new TaskSettings
        {
            TestsFiles = (TestsFiles ?? new List<string>()).ToList(), NwfName = NwfName, NwfFolder = NwfFolder, ReportFolder = ReportFolder,
            Xml = Xml, Html = Html, SkippedBlocks = (SkippedBlocks ?? new List<string>()).ToList(),
        };
    }

    /// <summary>Блок проекта: имя (пусто — «без блока») и его модели.</summary>
    public sealed class BlockGroup
    {
        public string Name { get; set; }
        public List<ExportModel> Models { get; set; } = new List<ExportModel>();
    }

    /// <summary>Строка главного окна: проект, задача или блок задачи (Block не пусто).</summary>
    public sealed class TreeRow
    {
        public string Number { get; set; }
        public int Level { get; set; }
        /// <summary>Проект или задача; у строки блока — его задача.</summary>
        public ExportTask Item { get; set; }
        /// <summary>Блок — у строки блока; иначе null.</summary>
        public BlockGroup Block { get; set; }
    }

    /// <summary>Блоки, где что лежит, строки окна, проверки окон проекта и задачи.</summary>
    public static class ExportNodes
    {
        /// <summary>Метки, которые можно ставить в строки (подсказка под полем и проверка при сохранении).</summary>
        public static readonly string[] NwcFolderLabels = { "Блок", "Отдел" };
        public static readonly string[] NwfFolderLabels = { "Блок" };
        public static readonly string[] NwfNameLabels = { "Блок" };
        public static readonly string[] ReportFolderLabels = { "Блок", "Дата", "Формат" };

        public static ExportTask Root(ExportTask task)
        {
            while (task.Parent != null) task = task.Parent;
            return task;
        }

        /// <summary>Блок модели: вписанный руками, иначе — из имени («S1»); нет — пусто.</summary>
        public static string BlockOf(ExportModel model, ExportSettings common) =>
            !string.IsNullOrWhiteSpace(model.Block) ? model.Block.Trim() : ExportNames.Block(model.Path, common.BlockPrefix);

        /// <summary>Блоки проекта по его моделям — по порядку номеров (S1, S2, S10); модели без блока — последней группой с пустым именем.</summary>
        public static List<BlockGroup> Blocks(ExportTask project, ExportSettings common)
        {
            var groups = project.ModelFiles.GroupBy(m => BlockOf(m, common), StringComparer.OrdinalIgnoreCase)
                .Select(g => new BlockGroup { Name = g.Key, Models = g.ToList() }).ToList();
            groups.Sort((a, b) => a.Name.Length == 0 ? 1 : b.Name.Length == 0 ? -1 : NaturalCompare(a.Name, b.Name));
            return groups;
        }

        /// <summary>Блоки, которые делает задача: блоки проекта без снятых; модели без блока — не делаются (некуда положить).</summary>
        public static List<BlockGroup> TaskBlocks(ExportTask task, ExportSettings common) =>
            Blocks(Root(task), common).Where(b => b.Name.Length > 0 &&
                !(task.Options.SkippedBlocks ?? new List<string>()).Contains(b.Name, StringComparer.OrdinalIgnoreCase)).ToList();

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

        /// <summary>
        /// Строки главного окна: проект «1», его задачи «1.1», блоки задачи «1.1.1»; свёрнутые (collapsed — проекты и задачи) —
        /// без вложенных. Номера ставятся сами.
        /// </summary>
        public static List<TreeRow> Rows(IList<ExportTask> projects, ISet<ExportTask> collapsed, ExportSettings common)
        {
            var rows = new List<TreeRow>();
            for (var p = 0; p < projects.Count; p++)
            {
                var project = projects[p];
                var pn = (p + 1).ToString();
                rows.Add(new TreeRow { Number = pn, Level = 0, Item = project });
                if (collapsed.Contains(project)) continue;
                for (var t = 0; t < project.Children.Count; t++)
                {
                    var task = project.Children[t];
                    var tn = pn + "." + (t + 1);
                    rows.Add(new TreeRow { Number = tn, Level = 1, Item = task });
                    if (collapsed.Contains(task)) continue;
                    var blocks = TaskBlocks(task, common);
                    for (var b = 0; b < blocks.Count; b++)
                        rows.Add(new TreeRow { Number = tn + "." + (b + 1), Level = 2, Item = task, Block = blocks[b] });
                }
            }
            return rows;
        }

        /// <summary>Краткая информация строки: проекта — задачи, модели, блоки; задачи — блоки и файлы проверок; блока — модели.</summary>
        public static string Summary(TreeRow row, ExportSettings common)
        {
            if (row.Block != null) return row.Block.Models.Count + " " + ExportTask.ModelsWord(row.Block.Models.Count);
            var item = row.Item;
            if (item.Parent == null)
            {
                var blocks = Blocks(item, common);
                var named = blocks.Count(b => b.Name.Length > 0);
                var parts = new List<string>
                {
                    item.Children.Count + " " + ExportTask.TasksWord(item.Children.Count),
                    item.Models + " " + ExportTask.ModelsWord(item.Models),
                    named + " " + ExportTask.BlocksWord(named),
                };
                var loose = blocks.Where(b => b.Name.Length == 0).Sum(b => b.Models.Count);
                if (loose > 0) parts.Add("без блока: " + loose);
                return string.Join(" · ", parts);
            }
            var taskBlocks = TaskBlocks(item, common);
            var files = item.Options.TestsFiles ?? new List<string>();
            return (taskBlocks.Count == 0 ? "блоков нет" : "блоки " + BlocksRange(taskBlocks)) + " · " +
                   (files.Count == 0 ? "файлов проверок нет" : string.Join(", ", files.Select(FileName)));
        }

        /// <summary>«S1–S9» или «S1, S3»: подряд — диапазоном, иначе — списком.</summary>
        private static string BlocksRange(List<BlockGroup> blocks) =>
            blocks.Count > 2 ? blocks[0].Name + "–" + blocks[blocks.Count - 1].Name + " (" + blocks.Count + ")" : string.Join(", ", blocks.Select(b => b.Name));

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

        /// <summary>Файл NWC модели проекта: одна модель — один NWC на все задачи.</summary>
        public static string NwcPath(ExportTask project, ExportModel model, ExportSettings common)
        {
            var p = Root(project).Project;
            return InProject(p.Folder, ExportNames.Fill(p.NwcFolder, Labels(BlockOf(model, common), model, common, DateTime.Today))) + "\\" +
                   NwcNameOf(model, common) + ".nwc";
        }

        /// <summary>Постоянный NWF блока задачи: папка и имя — строки задачи («{Блок}_ВК» → «S1_ВК.nwf»).</summary>
        public static string NwfPath(ExportTask task, string block, ExportSettings common)
        {
            var labels = Labels(block, null, common, DateTime.Today);
            return InProject(Root(task).Project.Folder, ExportNames.Fill(task.Options.NwfFolder, labels)) + "\\" +
                   ExportNames.Fill(task.Options.NwfName, labels) + ".nwf";
        }

        /// <summary>Папка отчётов блока задачи на дату запуска (одна на весь запуск) и формат («XML», «HTML»).</summary>
        public static string ReportPath(ExportTask task, string block, ExportSettings common, DateTime runDate, string format) =>
            InProject(Root(task).Project.Folder, ExportNames.Fill(task.Options.ReportFolder, Labels(block, null, common, runDate, format)));

        /// <summary>Вид для выгрузки: проекта или из общих.</summary>
        public static string ViewOf(ExportTask task, ExportSettings common) => Root(task).Project.ViewName ?? common.Nwc.ViewName;

        /// <summary>Добавить модели в проект без повторов; сколько добавлено.</summary>
        public static int AddModels(ExportTask project, IEnumerable<string> paths)
        {
            var added = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    project.ModelFiles.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                project.ModelFiles.Add(new ExportModel { Path = path });
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

        /// <summary>Папка для NWC: только {Блок}, {Отдел} — даты нет, NWF ищет NWC по постоянному пути; null — всё верно.</summary>
        public static string CheckNwcFolder(string folder) =>
            CheckLabels(folder, "Папка для NWC", NwcFolderLabels, "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка для NWF: только {Блок} — NWF постоянная (регламент), одна на блок задачи; null — всё верно.</summary>
        public static string CheckNwfFolder(string folder) =>
            CheckLabels(folder, "Папка для NWF", NwfFolderLabels, "NWF постоянная — одна на блок задачи, без даты и отдела");

        /// <summary>Имя NWF: только {Блок} (иначе у блоков задачи — один файл), знаки — как в имени файла; null — всё верно.</summary>
        public static string CheckNwfName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Нужно имя NWF.";
            var labels = CheckLabels(name, "Имя NWF", NwfNameLabels, "NWF одна на блок задачи");
            if (labels != null) return labels;
            if (!ExportNames.LabelsIn(name).Any()) return "Имя NWF — с меткой {Блок}: иначе у всех блоков задачи один файл.";
            var plain = ExportNames.Fill(name, NwfNameLabels.ToDictionary(l => l, l => "x"));
            return plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Имя NWF: недопустимые знаки в имени файла." : null;
        }

        /// <summary>
        /// NWF задачи — не та же, что у другой задачи проекта: копия задачи со старым именем NWF писала бы проверки двух критичностей
        /// в один файл. Сравнивается по блокам проекта (нет — «S1»); null — всё верно.
        /// </summary>
        public static string CheckNwfTaken(ExportTask task, string nwfFolder, string nwfName, ExportSettings common)
        {
            var project = Root(task);
            var blocks = Blocks(project, common).Where(b => b.Name.Length > 0).Select(b => b.Name).DefaultIfEmpty("S1").ToList();
            foreach (var other in project.Children.Where(c => c != task))
                foreach (var b in blocks)
                {
                    var labels = Labels(b, null, common, DateTime.Today);
                    if (string.Equals(NwfFile(project, nwfFolder, nwfName, labels), NwfFile(project, other.Options.NwfFolder, other.Options.NwfName, labels),
                            StringComparison.OrdinalIgnoreCase))
                        return "NWF «" + ExportNames.Fill(nwfName, labels) + ".nwf» — та же, что у задачи «" + other.Name + "»: поменяйте имя или папку NWF.";
                }
            return null;
        }

        private static string NwfFile(ExportTask project, string folder, string name, Dictionary<string, string> labels) =>
            InProject(project.Project.Folder, ExportNames.Fill(folder ?? "", labels)) + "\\" + ExportNames.Fill(name ?? "", labels).Trim();

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

        /// <summary>Название проекта: есть и не повторяется — две одинаковые строки в общем списке не различить; null — всё верно.</summary>
        public static string CheckProjectName(string name, IEnumerable<string> others)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Нужно название.";
            return others.Any(o => string.Equals((o ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase))
                ? "Проект «" + n + "» уже есть — назовите иначе."
                : null;
        }

        /// <summary>Название задачи: есть и не повторяется в проекте; null — всё верно.</summary>
        public static string CheckTaskName(ExportTask task, string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Нужно название.";
            var twin = task.Parent?.Children.FirstOrDefault(c => c != task && string.Equals((c.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
            return twin == null ? null : "Задача «" + n + "» в проекте уже есть.";
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
        /// Имена NWC моделей проекта: допустимые знаки, и две модели не ложатся в один файл (одна папка для NWC — имена должны
        /// различаться); null — всё верно.
        /// </summary>
        public static string CheckNwcNames(ExportTask project, ExportSettings common)
        {
            foreach (var m in project.ModelFiles)
            {
                var name = NwcNameOf(m, common);
                if (string.IsNullOrWhiteSpace(name)) return "У модели «" + FileName(m.Path) + "» пустое имя NWC.";
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "В имени NWC «" + name + "» — недопустимые знаки.";
            }
            var twice = project.ModelFiles.GroupBy(m => NwcPath(project, m, common), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
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
