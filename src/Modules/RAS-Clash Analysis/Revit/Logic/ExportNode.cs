using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Модель в выгрузке узла: файл (сетевая папка) или путь Revit Server (RSN://…).</summary>
    public sealed class ExportModel
    {
        public string Path { get; set; }
        /// <summary>Имя NWC, поправленное руками; null — из имени модели по общему правилу.</summary>
        public string NwcName { get; set; }
    }

    /// <summary>
    /// Проверка задачи (02.11.2026, разработчик: «у блока S1 нужно 2 NWF и 2 отчёта — высокая и средняя критичность»): задаётся
    /// один раз у задачи и действует у всех её блоков; на каждый блок — свой NWF и свой отчёт. Разделения по критичности нет —
    /// одна проверка; проверок нет — только NWC.
    /// </summary>
    public sealed class CheckSet
    {
        /// <summary>Постоянный ключ: по нему блок помнит снятую проверку, даже если её переименуют.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>Название — как папка критичности («Высокая критичность»): по ней сайт коллизий узнаёт критичность.</summary>
        public string Name { get; set; } = "";
        /// <summary>Файл проверок Navisworks (.xml, выгрузка Clash Detective).</summary>
        public string TestsFile { get; set; } = "";
        /// <summary>Имя NWF — строка с {Блок}; пусто — «{Блок}_название».</summary>
        public string NwfName { get; set; } = "";
        /// <summary>Папка отчётов — строка с {Блок}, {Дата}; критичность — текстом.</summary>
        public string ReportFolder { get; set; } = "";

        public string EffectiveNwfName => string.IsNullOrWhiteSpace(NwfName) ? "{Блок}_" + Name.Trim() : NwfName.Trim();

        public CheckSet Clone() => (CheckSet)MemberwiseClone();
    }

    /// <summary>
    /// Своё у строки (карандаш) — только то, что различается между проектами (разбор настроек, 02.11.2026): папки NWC и NWF,
    /// вид — каждая строка наследуется отдельно, null — как у строки выше, у задачи — пусто (папки) или из общих (вид);
    /// проверки — только у задачи. Модели — у самой строки (ExportTask.ModelFiles), остальное — общее (шестерёнка).
    /// </summary>
    public sealed class NodeSettings
    {
        /// <summary>Папка NWC — строка с метками {Блок}, {Отдел}; даты нет: NWF ссылается на NWC по постоянному пути.</summary>
        public string NwcFolder { get; set; }
        /// <summary>Папка NWF — строка с метками {Блок}, {Дата}.</summary>
        public string NwfFolder { get; set; }
        /// <summary>Вид для выгрузки; null — как выше, у задачи — из общих.</summary>
        public string ViewName { get; set; }
        /// <summary>Проверки — у задачи (верхняя строка); null — нет.</summary>
        public List<CheckSet> Checks { get; set; }
    }

    /// <summary>Наследование по дереву, где лежат модели, проверки, проверки окна строки.</summary>
    public static class ExportNodes
    {
        /// <summary>Метки, которые можно ставить в строки (подсказка под полем и проверка при сохранении).</summary>
        public static readonly string[] NwcFolderLabels = { "Блок", "Отдел" };
        public static readonly string[] NwfFolderLabels = { "Блок", "Дата" };
        public static readonly string[] NwfNameLabels = { "Блок" };
        public static readonly string[] ReportFolderLabels = { "Блок", "Дата" };

        /// <summary>Значение от строк выше: ближайшая, у которой оно своё; нет ни у кого — (null, null).</summary>
        public static (T Value, ExportTask From) Inherited<T>(ExportTask task, Func<NodeSettings, T> pick) where T : class
        {
            for (var p = task.Parent; p != null; p = p.Parent)
            {
                var v = pick(p.Own);
                if (v != null) return (v, p);
            }
            return (null, null);
        }

        /// <summary>Действующее значение строки: своё, иначе от строк выше; null — нигде не задано.</summary>
        public static T Effective<T>(ExportTask task, Func<NodeSettings, T> pick) where T : class =>
            pick(task.Own) ?? Inherited(task, pick).Value;

        public static ExportTask Root(ExportTask task)
        {
            while (task.Parent != null) task = task.Parent;
            return task;
        }

        /// <summary>Проверки задачи этой строки (заданы у задачи); нет — пусто.</summary>
        public static List<CheckSet> ChecksOf(ExportTask task) => Root(task).Own.Checks ?? new List<CheckSet>();

        /// <summary>Проверки, которые строка делает: проверки задачи без снятых у строки.</summary>
        public static List<CheckSet> ActiveChecks(ExportTask task) =>
            ChecksOf(task).Where(c => !task.SkippedChecks.Contains(c.Id)).ToList();

        /// <summary>Ближайшая строка выше со своими моделями; null — нет.</summary>
        public static ExportTask ModelsAbove(ExportTask task)
        {
            for (var p = task.Parent; p != null; p = p.Parent)
                if (p.Models > 0) return p;
            return null;
        }

        /// <summary>Вложенные строки со своими моделями, по порядку.</summary>
        public static List<ExportTask> ModelsBelow(ExportTask task)
        {
            var list = new List<ExportTask>();
            foreach (var c in task.Children)
            {
                if (c.Models > 0) list.Add(c);
                list.AddRange(ModelsBelow(c));
            }
            return list;
        }

        /// <summary>
        /// Можно ли строке держать модели: модели выгружаются один раз — у одной строки на ветке (обычно у блока); ни выше, ни
        /// ниже их быть не должно. Проект без блоков держит модели сам.
        /// </summary>
        public static bool CanHaveModels(ExportTask task) => task.Models > 0 || ModelsAbove(task) == null && ModelsBelow(task).Count == 0;

        /// <summary>Папки NWC и NWF нужны строке, если выше нет строки с моделями.</summary>
        public static bool HasNwcFolder(ExportTask task) => ModelsAbove(task) == null;

        /// <summary>Строка проекта (кроме except), у которой уже есть эта модель; null — нет.</summary>
        public static ExportTask FindModel(ExportTask root, string path, ExportTask except)
        {
            if (root != except && root.ModelFiles.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) return root;
            foreach (var c in root.Children)
            {
                var found = FindModel(c, path, except);
                if (found != null) return found;
            }
            return null;
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
            CheckLabels(folder, "Папка NWC", NwcFolderLabels, "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка NWF: только {Блок}, {Дата} — NWF один на блок и проверку, не на отдел; null — всё верно.</summary>
        public static string CheckNwfFolder(string folder) =>
            CheckLabels(folder, "Папка NWF", NwfFolderLabels, "NWF один на блок, а не на модель отдела");

        /// <summary>Папка отчётов: только {Блок}, {Дата} — отчёт один на блок; null — всё верно.</summary>
        public static string CheckReportFolder(string folder, string what = "Папка отчётов") =>
            CheckLabels(folder, what, ReportFolderLabels, "отчёт один на блок, а не на модель отдела");

        /// <summary>Имя NWF: только {Блок}, знаки — как в имени файла; null — всё верно.</summary>
        public static string CheckNwfName(string name, string what)
        {
            var labels = CheckLabels(name, what, NwfNameLabels, "NWF один на блок и проверку");
            if (labels != null) return labels;
            var plain = ExportNames.Fill(name, NwfNameLabels.ToDictionary(l => l, l => "x"));
            return plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? what + ": недопустимые знаки в имени файла." : null;
        }

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

        /// <summary>Проверки задачи: названия есть и не повторяются, строки — с верными метками, имена NWF — разные; null — всё верно.</summary>
        public static string CheckChecks(IList<CheckSet> checks)
        {
            if (checks.Any(c => string.IsNullOrWhiteSpace(c.Name))) return "У проверки нет названия.";
            var twice = checks.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) return "Проверка «" + twice.Key + "» — дважды.";
            foreach (var c in checks)
            {
                var error = CheckNwfName(c.EffectiveNwfName, "«" + c.Name.Trim() + "», имя NWF")
                            ?? CheckReportFolder(c.ReportFolder, "«" + c.Name.Trim() + "», папка отчётов");
                if (error != null) return error;
            }
            var sameNwf = checks.GroupBy(c => c.EffectiveNwfName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return sameNwf == null ? null : "Имя NWF «" + sameNwf.Key + "» — у двух проверок: файлы заменят друг друга.";
        }

        /// <summary>Имена NWC моделей строки: допустимые знаки, без повторов; null — всё верно.</summary>
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
