using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Модель задачи: файл (сетевая папка) или путь Revit Server (RSN://…).</summary>
    public sealed class ExportModel
    {
        public string Path { get; set; }
        /// <summary>Имя NWC, поправленное руками; null — из имени модели по общему правилу.</summary>
        public string NwcName { get; set; }
    }

    /// <summary>
    /// Настройки проекта — общие для всех его задач (разработчик, 02.11.2026: «проект — как каталог, в нём задачи-блоки, у
    /// проекта — настройки, общие для блоков»). Папки — строки внутри папки проекта (полный путь — тоже можно), по умолчанию —
    /// по регламенту 15.10 / 22.10.2026: «&lt;проект&gt;\&lt;блок&gt;\&lt;дата&gt;\&lt;отчёты&gt;», один постоянный NWF на блок.
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
        /// <summary>NWC — метки {Блок}, {Отдел}; даты нет: NWF ссылается на NWC по постоянному пути.</summary>
        public string NwcFolder { get; set; } = @"{Блок}\NWC";
        /// <summary>NWF — один постоянный на задачу, имя — название задачи; метка {Блок}.</summary>
        public string NwfFolder { get; set; } = "{Блок}";
        /// <summary>Отчёты прогона — метки {Блок}, {Дата}; прямо в папке прогона, без папок критичности.</summary>
        public string ReportFolder { get; set; } = @"{Блок}\{Дата}";
        /// <summary>
        /// Файлы проверок Navisworks (.xml, выгрузка Clash Detective) — у всех задач, если у задачи нет своих (разработчик,
        /// 02.11.2026: «возможность добавлять несколько файлов проверок»); все — в NWF задачи.
        /// </summary>
        public List<string> TestsFiles { get; set; } = new List<string>();
        /// <summary>Вид для выгрузки; null — из общих настроек.</summary>
        public string ViewName { get; set; }
    }

    /// <summary>Где что лежит у задачи, проверки окон проекта и задачи.</summary>
    public static class ExportNodes
    {
        /// <summary>Метки, которые можно ставить в строки (подсказка под полем и проверка при сохранении).</summary>
        public static readonly string[] NwcFolderLabels = { "Блок", "Отдел" };
        public static readonly string[] NwfFolderLabels = { "Блок" };
        public static readonly string[] ReportFolderLabels = { "Блок", "Дата" };

        public static ExportTask Root(ExportTask task)
        {
            while (task.Parent != null) task = task.Parent;
            return task;
        }

        /// <summary>
        /// Блок задачи — тот, что чаще всех находится в именах её моделей («S1» у «S1 высокая» и «S1 средняя»); моделей с блоком
        /// нет — название задачи.
        /// </summary>
        public static string BlockOf(ExportTask task, ExportSettings common)
        {
            var block = task.ModelFiles.Select(m => ExportNames.Block(m.Path, common.BlockPrefix)).Where(b => b.Length > 0)
                .GroupBy(b => b, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            return block ?? (task.Name ?? "").Trim();
        }

        /// <summary>Метки задачи: {Блок} — блок задачи, {Отдел} — по модели (пусто — без модели), {Дата} — date.</summary>
        public static Dictionary<string, string> Labels(ExportTask task, ExportModel model, ExportSettings common, DateTime date) =>
            new Dictionary<string, string>
            {
                ["Блок"] = BlockOf(task, common),
                ["Отдел"] = model == null ? "" : ExportNames.Department(model.Path, common.Departments.Select(d => d.Department)),
                ["Дата"] = date.ToString(common.DateFormat),
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

        /// <summary>Файл NWC модели задачи.</summary>
        public static string NwcPath(ExportTask task, ExportModel model, ExportSettings common)
        {
            var p = Root(task).Project;
            return InProject(p.Folder, ExportNames.Fill(p.NwcFolder, Labels(task, model, common, DateTime.Today))) + "\\" +
                   NwcNameOf(model, common) + ".nwc";
        }

        /// <summary>Постоянный NWF задачи — имя по названию задачи («S1.nwf», «S1 высокая.nwf»).</summary>
        public static string NwfPath(ExportTask task, ExportSettings common)
        {
            var p = Root(task).Project;
            return InProject(p.Folder, ExportNames.Fill(p.NwfFolder, Labels(task, null, common, DateTime.Today))) + "\\" +
                   (task.Name ?? "").Trim() + ".nwf";
        }

        /// <summary>Папка отчётов прогона задачи на дату запуска (одна на весь запуск проекта).</summary>
        public static string ReportPath(ExportTask task, ExportSettings common, DateTime runDate)
        {
            var p = Root(task).Project;
            return InProject(p.Folder, ExportNames.Fill(p.ReportFolder, Labels(task, null, common, runDate)));
        }

        /// <summary>Файлы проверок задачи: свои или проекта.</summary>
        public static List<string> TestsFilesOf(ExportTask task) => task.TestsFiles ?? Root(task).Project.TestsFiles ?? new List<string>();

        /// <summary>Вид для выгрузки: проекта или из общих.</summary>
        public static string ViewOf(ExportTask task, ExportSettings common) => Root(task).Project.ViewName ?? common.Nwc.ViewName;

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

        /// <summary>Папка NWC: только {Блок}, {Отдел} — даты нет, NWF ищет NWC по постоянному пути; null — всё верно.</summary>
        public static string CheckNwcFolder(string folder) =>
            CheckLabels(folder, "NWC", NwcFolderLabels, "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка NWF: только {Блок} — NWF постоянный (регламент), один на задачу; null — всё верно.</summary>
        public static string CheckNwfFolder(string folder) =>
            CheckLabels(folder, "NWF", NwfFolderLabels, "NWF постоянный — один на задачу, без даты и отдела");

        /// <summary>Папка отчётов: только {Блок}, {Дата} — отчёт один на задачу, не на модель отдела; null — всё верно.</summary>
        public static string CheckReportFolder(string folder) =>
            CheckLabels(folder, "Отчёты", ReportFolderLabels, "отчёт один на задачу, а не на модель отдела");

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

        /// <summary>Название задачи — оно же имя NWF: знаки как в имени файла, не повторяется в проекте; null — всё верно.</summary>
        public static string CheckTaskName(ExportTask task, string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Нужно название.";
            if (task.Parent == null) return null;
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "В названии задачи — недопустимые знаки: по нему называется NWF.";
            var twin = task.Parent.Children.FirstOrDefault(c => c != task && string.Equals((c.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
            return twin == null ? null : "Задача «" + n + "» в проекте уже есть: у двух задач был бы один NWF.";
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
                        return "Проверка «" + n.Trim() + "» — и в «" + FileName(other) + "», и в «" + FileName(f) +
                               "»: отчёты заменят друг друга.";
                    seen[key] = f;
                }
            }
            return null;
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

        /// <summary>Имена NWC моделей задачи: допустимые знаки, без повторов; null — всё верно.</summary>
        public static string CheckNwcNames(IEnumerable<string> names)
        {
            var list = names.ToList();
            if (list.Any(string.IsNullOrWhiteSpace)) return "У модели пустое имя NWC.";
            var bad = list.FirstOrDefault(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
            if (bad != null) return "В имени NWC «" + bad + "» — недопустимые знаки.";
            var twice = list.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return twice == null ? null : "Имя NWC «" + twice.Key + "» — у двух моделей: файлы заменят друг друга.";
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
    }
}
