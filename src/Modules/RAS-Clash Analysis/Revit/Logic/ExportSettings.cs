using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Общие настройки «Задач выгрузки» — шестерёнка главного окна (DECISIONS.md модуля, «„Задачи выгрузки“…»): действуют у
    /// всех задач, узел дерева может переопределить (кроме меток и таблицы отделов). Пока — в памяти; хранение на сервере —
    /// следующим шагом. Без Revit API.
    /// </summary>
    public sealed class ExportSettings
    {
        /// <summary>Папка NWC — строка с метками; даты в ней нет: NWF ссылается на NWC по постоянному пути.</summary>
        public string NwcFolder { get; set; } = @"{Проект}\{Блок}\NWC";
        /// <summary>Папка отчётов (этап 2).</summary>
        public string ReportFolder { get; set; } = @"{Проект}\{Блок}\{Дата}";
        /// <summary>Формат метки {Дата} (.NET: yyyy-MM-dd и т. п.).</summary>
        public string DateFormat { get; set; } = "yyyy-MM-dd";
        /// <summary>Свои метки — без скобок; значение задаётся у узла и наследуется.</summary>
        public List<string> Labels { get; set; } = new List<string>();

        /// <summary>Обрезать имя NWC по шаблону имени модели.</summary>
        public bool TrimName { get; set; } = true;
        /// <summary>Сколько частей через «_» оставить в имени NWC.</summary>
        public int NameParts { get; set; } = 7;
        /// <summary>Какая часть имени (с 1) — раздел («KJ01»); отдел — его буквы до цифр («KJ»).</summary>
        public int SectionPart { get; set; } = 5;

        /// <summary>Отдел → эталон (.rte) → шаблон вида для выгрузки.</summary>
        public List<DepartmentTemplate> Departments { get; set; } = new List<DepartmentTemplate>();

        /// <summary>Вид для выгрузки; нет в модели — создаётся.</summary>
        public string ViewName { get; set; } = "3D_Navisworks";

        // что удалить из копии модели перед выгрузкой
        public bool RemoveRevitLinks { get; set; } = true;
        public bool RemoveCad { get; set; } = true;
        public bool RemovePointClouds { get; set; } = true;
        public bool RemoveIfc { get; set; } = true;

        /// <summary>Временная папка для копий моделей — личная (у каждого свой диск), хранится у человека, не на сервере.</summary>
        public string TempFolder { get; set; } = Path.Combine(Path.GetTempPath(), "RAS_v_2.0", "NWC");
    }

    public sealed class DepartmentTemplate
    {
        public string Department { get; set; }
        /// <summary>Путь к файлу шаблона проекта отдела (.rte).</summary>
        public string Etalon { get; set; }
        /// <summary>Имя шаблона вида для выгрузки в эталоне.</summary>
        public string ViewTemplate { get; set; }
    }

    /// <summary>Метки в строке папки и имя NWC из имени модели.</summary>
    public static class ExportNames
    {
        /// <summary>Встроенные метки: уровни дерева, дата, раздел и отдел из имени модели.</summary>
        public static readonly string[] BuiltInLabels = { "Проект", "Блок", "Критичность", "Дата", "Отдел", "Раздел" };

        private static readonly Regex LabelRx = new Regex(@"\{([^{}]+)\}");

        /// <summary>Метки строки, по порядку, без повторов.</summary>
        public static List<string> LabelsIn(string template) =>
            LabelRx.Matches(template ?? "").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

        /// <summary>Подставить значения меток; метка без значения остаётся как есть («{Шифр}»).</summary>
        public static string Fill(string template, IDictionary<string, string> values) =>
            LabelRx.Replace(template ?? "", m =>
                values.TryGetValue(m.Groups[1].Value.Trim(), out var v) && !string.IsNullOrEmpty(v) ? v : m.Value);

        /// <summary>
        /// Имя NWC из имени модели. trim — первые parts частей через «_», в последней — до первого пробела:
        /// «0000_01_01_S6_KJ01_01_M3 еще текст.rvt» и «…_M3_еще текст.rvt» → «0000_01_01_S6_KJ01_01_M3». По счёту частей, а не
        /// по «M3» — на этом месте бывает и «Р3». Частей меньше — имя как есть, matches = false («не по шаблону»).
        /// </summary>
        public static string NwcName(string modelFile, bool trim, int parts, out bool matches)
        {
            var name = Path.GetFileNameWithoutExtension(modelFile ?? "").Trim();
            var split = name.Split('_');
            matches = parts > 0 && split.Length >= parts;
            if (!trim || !matches) return name;
            var kept = split.Take(parts).ToArray();
            var last = kept[parts - 1];
            var space = last.IndexOf(' ');
            if (space >= 0) kept[parts - 1] = last.Substring(0, space);
            return string.Join("_", kept);
        }

        /// <summary>Раздел — часть имени модели с номером part (с 1): «KJ01»; нет такой части — пусто.</summary>
        public static string Section(string modelFile, int part)
        {
            var split = Path.GetFileNameWithoutExtension(modelFile ?? "").Split('_');
            if (part < 1 || part > split.Length) return "";
            var s = split[part - 1];
            var space = s.IndexOf(' ');
            return (space >= 0 ? s.Substring(0, space) : s).Trim();
        }

        /// <summary>Отдел — буквы раздела до первой цифры: «KJ01» → «KJ», «AR» → «AR».</summary>
        public static string Department(string section)
        {
            var sb = new StringBuilder();
            foreach (var c in section ?? "")
            {
                if (char.IsDigit(c)) break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
