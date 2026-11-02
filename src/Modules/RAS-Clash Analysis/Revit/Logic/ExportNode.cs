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
        /// <summary>Имя NWC, поправленное руками; null — из имени модели по правилам узла.</summary>
        public string NwcName { get; set; }
    }

    /// <summary>Папки узла — строки с метками {Блок} и {Дата}, остальное — текстом (разработчик, 02.11.2026).</summary>
    public sealed class FolderSettings
    {
        public string NwcFolder { get; set; } = "";
        /// <summary>Папка отчётов — этап 2; пока только хранится.</summary>
        public string ReportFolder { get; set; } = "";
        /// <summary>Формат метки {Дата} (.NET); как в папках отчётов сейчас — «01.10.2026».</summary>
        public string DateFormat { get; set; } = "dd.MM.yyyy";
        public FolderSettings Clone() => (FolderSettings)MemberwiseClone();
    }

    /// <summary>Имя NWC из имени модели; раздел и отдел — из той же части имени.</summary>
    public sealed class NameSettings
    {
        public bool Trim { get; set; } = true;
        /// <summary>Сколько частей через «_» оставить в имени NWC.</summary>
        public int Parts { get; set; } = 7;
        /// <summary>Какая часть имени (с 1) — раздел («KJ01»); отдел — его буквы до цифр («KJ»).</summary>
        public int SectionPart { get; set; } = 5;
        public NameSettings Clone() => (NameSettings)MemberwiseClone();
    }

    /// <summary>Что удалить из копии модели перед выгрузкой.</summary>
    public sealed class RemoveSettings
    {
        public bool RevitLinks { get; set; } = true;
        public bool Cad { get; set; } = true;
        public bool PointClouds { get; set; } = true;
        public bool Ifc { get; set; } = true;
        public RemoveSettings Clone() => (RemoveSettings)MemberwiseClone();
    }

    public sealed class DepartmentTemplate
    {
        public string Department { get; set; }
        /// <summary>Путь к файлу шаблона проекта отдела (.rte).</summary>
        public string Etalon { get; set; }
        /// <summary>Имя шаблона вида для выгрузки в эталоне.</summary>
        public string ViewTemplate { get; set; }
        public DepartmentTemplate Clone() => (DepartmentTemplate)MemberwiseClone();
    }

    /// <summary>
    /// Свои настройки узла (карандаш); null — как у узла выше, у самого верхнего — по умолчанию (папки, имя, удаление) или
    /// общие (параметры выгрузки NWC — шестерёнка).
    /// </summary>
    public sealed class NodeSettings
    {
        public FolderSettings Folders { get; set; }
        public NameSettings Names { get; set; }
        public List<DepartmentTemplate> Departments { get; set; }
        public RemoveSettings Remove { get; set; }
        public ExportSettings Nwc { get; set; }
    }

    /// <summary>Наследование настроек по дереву и проверки окна узла.</summary>
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

        /// <summary>Уровень узла: задача — 0, блок — 1, критичность — 2.</summary>
        public static int Level(ExportTask task)
        {
            var level = 0;
            for (var p = task.Parent; p != null; p = p.Parent) level++;
            return level;
        }

        /// <summary>
        /// Значение {Блок}: имя узла второго уровня, в котором узел лежит (у блока — он сам, у критичности — её блок); у задачи —
        /// для примера первый блок; блоков нет — null.
        /// </summary>
        public static string BlockName(ExportTask task)
        {
            var level = Level(task);
            if (level == 0) return task.Children.FirstOrDefault()?.Name;
            var t = task;
            while (Level(t) > 1) t = t.Parent;
            return t.Name;
        }

        /// <summary>Имя NWC модели: поправленное руками или из имени модели по правилам узла.</summary>
        public static string NwcNameOf(ExportModel model, NameSettings names) =>
            !string.IsNullOrWhiteSpace(model.NwcName)
                ? model.NwcName.Trim()
                : ExportNames.NwcName(model.Path, names.Trim, names.Parts, out _);

        /// <summary>Путь Revit Server: «RSN://сервер/папка/модель.rvt».</summary>
        public static bool IsServerPath(string path) =>
            (path ?? "").Trim().StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) &&
            (path ?? "").Trim().EndsWith(".rvt", StringComparison.OrdinalIgnoreCase);

        /// <summary>Строка папки: только известные метки; null — всё верно.</summary>
        public static string CheckFolder(string folder, string what)
        {
            var unknown = ExportNames.LabelsIn(folder).FirstOrDefault(l => !ExportNames.BuiltInLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
            return unknown == null ? null
                : what + ": метки {" + unknown + "} нет — есть только " + string.Join(" и ", ExportNames.BuiltInLabels.Select(l => "{" + l + "}")) + ".";
        }

        /// <summary>Имена NWC моделей узла: допустимые знаки, без повторов; null — всё верно.</summary>
        public static string CheckNwcNames(IEnumerable<string> names)
        {
            var list = names.ToList();
            var empty = list.Any(string.IsNullOrWhiteSpace);
            if (empty) return "У модели пустое имя NWC.";
            var bad = list.FirstOrDefault(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
            if (bad != null) return "В имени NWC «" + bad + "» — недопустимые знаки.";
            var twice = list.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return twice == null ? null : "Имя NWC «" + twice.Key + "» — у двух моделей: файлы заменят друг друга.";
        }
    }
}
