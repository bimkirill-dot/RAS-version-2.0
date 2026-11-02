using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Что выгружать: «Целый проект» или «Вид» — вид для выгрузки («Экспорт» окна экспортёра; «Выбор» не нужен — модель открывает плагин).</summary>
    public enum NwcScope { Model, View }

    /// <summary>«Конвертировать параметры объектов»: «Нет» / «Объекты» / «Все».</summary>
    public enum NwcParameters { None, Elements, All }

    /// <summary>
    /// Общие настройки «Задач выгрузки» — шестерёнка главного окна: параметры выгрузки NWC, те же, что в окне экспортёра Navisworks
    /// (разработчик, 02.11.2026: «главные настройки — те же самые, что открываются при экспорте в NWC стандартным экспортёром»;
    /// значения по умолчанию — с его снимка окна) и что удалить из модели перед выгрузкой. Папки, имя NWC, отделы — в
    /// настройках узла (карандаш). Пока — в памяти; хранение на сервере — следующим шагом. Без Revit API: в NavisworksExportOptions —
    /// при выгрузке.
    /// </summary>
    public sealed class ExportSettings
    {
        public bool ConvertElementIds => true;   // всегда: по номерам элементов работают «Показать в Revit» и «Коллизии элемента»
        public NwcParameters Parameters { get; set; } = NwcParameters.All;
        /// <summary>Координаты: общие (true) или внутренние.</summary>
        public bool SharedCoordinates { get; set; } = true;
        public double FacetingFactor { get; set; } = 1;
        public bool ConvertElementProperties { get; set; } = true;
        public bool ConvertUrls { get; set; }
        public bool ConvertLights { get; set; }
        public bool RoomAsAttribute { get; set; } = true;
        public bool ConvertLinkedCad { get; set; }
        public bool ConvertLinkedFiles { get; set; }
        public bool ConvertParts { get; set; } = true;
        public bool FindMissingMaterials { get; set; }
        public bool DivideIntoLevels { get; set; } = true;
        public NwcScope Scope { get; set; } = NwcScope.View;
        /// <summary>Вид для выгрузки (при «Экспорт: вид»); нет в модели — создаётся.</summary>
        public string ViewName { get; set; } = "3D_Navisworks";
        public bool RoomGeometry { get; set; }

        // что удалить из копии модели перед выгрузкой — одно правило для всех задач (разработчик, 02.11.2026: «можно вынести
        // в общие настройки»); связанное в NWC и так не попадёт, импортированный DWG — попадёт
        public bool RemoveRevitLinks { get; set; } = true;
        public bool RemoveCad { get; set; } = true;
        public bool RemovePointClouds { get; set; } = true;
        public bool RemoveIfc { get; set; } = true;

        /// <summary>
        /// Формат метки {Дата} (.NET); как в папках отчётов сейчас — «01.10.2026». Метки — в общих (разработчик, 02.11.2026:
        /// «настройки меток тоже добавь в общие настройки»).
        /// </summary>
        public string DateFormat { get; set; } = "dd.MM.yyyy";

        /// <summary>
        /// {Блок} — часть имени модели с этим номером (с 1): «S6» в «0000_01_01_S6_KJ01_01_M3» (разработчик, 02.11.2026: «метка
        /// блок и отдел — просто из названия файла»).
        /// </summary>
        public int BlockPart { get; set; } = 4;
        /// <summary>{Отдел} — буквы до цифр части с этим номером: «KJ01» → «KJ»; по отделу же — эталон и шаблон вида.</summary>
        public int DepartmentPart { get; set; } = 5;

        /// <summary>Форматы {Дата} на выбор.</summary>
        public static readonly string[] DateFormats = { "dd.MM.yyyy", "yyyy-MM-dd", "yyyyMMdd", "yyyy.MM.dd" };

        public ExportSettings Clone() => (ExportSettings)MemberwiseClone();

        /// <summary>Коэффициент фасетизации из текста: «1», «0,5», «0.5»; больше нуля.</summary>
        public static bool TryFaceting(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>Метки в строке папки и имя NWC из имени модели.</summary>
    public static class ExportNames
    {
        /// <summary>
        /// Метки строки папки: блок и отдел — из имени модели, дата — день выгрузки (разработчик, 02.11.2026); остальное пишется
        /// текстом.
        /// </summary>
        public static readonly string[] BuiltInLabels = { "Блок", "Отдел", "Дата" };

        /// <summary>Значения меток для модели: {Блок} и {Отдел} — из её имени (нет такой части — пусто), {Дата} — date.</summary>
        public static Dictionary<string, string> LabelValues(string modelFile, ExportSettings common, DateTime date) =>
            new Dictionary<string, string>
            {
                ["Блок"] = Section(modelFile, common.BlockPart),
                ["Отдел"] = Department(Section(modelFile, common.DepartmentPart)),
                ["Дата"] = date.ToString(common.DateFormat),
            };

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

        /// <summary>Часть имени модели с номером part (с 1), до пробела: «S6», «KJ01»; нет такой части — пусто.</summary>
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
