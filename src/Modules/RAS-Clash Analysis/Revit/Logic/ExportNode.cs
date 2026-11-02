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
    /// Своё у узла (карандаш) — только то, что различается между проектами (разбор настроек, 02.11.2026): папки и вид; каждая
    /// строка наследуется отдельно, null — как у узла выше, у задачи — пусто (папки) или из общих (вид). Модели — у самого узла
    /// (ExportTask.ModelFiles), остальное — общее (шестерёнка, ExportSettings).
    /// </summary>
    public sealed class NodeSettings
    {
        /// <summary>Папка NWC — строка с метками {Блок}, {Отдел}; даты нет: NWF ссылается на NWC по постоянному пути.</summary>
        public string NwcFolder { get; set; }
        /// <summary>Папка отчётов (этап 2; пока только хранится) — строка с метками {Блок}, {Дата}; критичность — текстом.</summary>
        public string ReportFolder { get; set; }
        /// <summary>Вид для выгрузки; null — как выше, у задачи — из общих.</summary>
        public string ViewName { get; set; }
    }

    /// <summary>Наследование по дереву, где лежат модели, проверки окна узла.</summary>
    public static class ExportNodes
    {
        /// <summary>Значение от узлов выше: ближайший, у кого оно своё; нет ни у кого — (null, null).</summary>
        public static (T Value, ExportTask From) Inherited<T>(ExportTask task, Func<NodeSettings, T> pick) where T : class
        {
            for (var p = task.Parent; p != null; p = p.Parent)
            {
                var v = pick(p.Own);
                if (v != null) return (v, p);
            }
            return (null, null);
        }

        /// <summary>Действующее значение узла: своё, иначе от узлов выше; null — нигде не задано.</summary>
        public static T Effective<T>(ExportTask task, Func<NodeSettings, T> pick) where T : class =>
            pick(task.Own) ?? Inherited(task, pick).Value;

        public static ExportTask Root(ExportTask task)
        {
            while (task.Parent != null) task = task.Parent;
            return task;
        }

        /// <summary>Ближайший узел выше со своими моделями (у критичности — её блок); null — нет.</summary>
        public static ExportTask ModelsAbove(ExportTask task)
        {
            for (var p = task.Parent; p != null; p = p.Parent)
                if (p.Models > 0) return p;
            return null;
        }

        /// <summary>Вложенные узлы со своими моделями, по порядку.</summary>
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
        /// Можно ли узлу держать модели: модели выгружаются один раз — у одного узла на ветке (обычно у блока); ни выше, ни ниже
        /// их быть не должно. Проект без блоков держит модели сам.
        /// </summary>
        public static bool CanHaveModels(ExportTask task) => task.Models > 0 || ModelsAbove(task) == null && ModelsBelow(task).Count == 0;

        /// <summary>Папка NWC нужна узлу, если выше нет узла с моделями: у критичности её нет — NWC выгружает блок.</summary>
        public static bool HasNwcFolder(ExportTask task) => ModelsAbove(task) == null;

        /// <summary>Узел проекта (кроме except), у которого уже есть эта модель; null — нет.</summary>
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

        /// <summary>Папка NWC: метки известны, {Дата} нет — NWF ссылается на NWC по постоянному пути; null — всё верно.</summary>
        public static string CheckNwcFolder(string folder) =>
            CheckFolder(folder, "Папка NWC", "Дата", "даты в папке NWC быть не должно: NWF ищет NWC по постоянному пути");

        /// <summary>Папка отчётов: метки известны, {Отдел} нет — отчёт один на блок, не на модель; null — всё верно.</summary>
        public static string CheckReportFolder(string folder) =>
            CheckFolder(folder, "Папка отчётов", "Отдел", "отчёт один на блок, а не на модель отдела");

        private static string CheckFolder(string folder, string what, string forbidden, string why)
        {
            var labels = ExportNames.LabelsIn(folder);
            var unknown = labels.FirstOrDefault(l => !ExportNames.BuiltInLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
            if (unknown != null)
                return what + ": метки {" + unknown + "} нет — есть только " +
                       string.Join(", ", ExportNames.BuiltInLabels.Select(l => "{" + l + "}")) + ".";
            return labels.Any(l => string.Equals(l, forbidden, StringComparison.OrdinalIgnoreCase))
                ? what + ": {" + forbidden + "} — нельзя, " + why + "."
                : null;
        }

        /// <summary>Имена NWC моделей узла: допустимые знаки, без повторов; null — всё верно.</summary>
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
