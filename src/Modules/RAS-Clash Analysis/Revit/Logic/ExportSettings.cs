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
    /// значения по умолчанию — с его снимка окна). Папки, имя NWC, отделы, что удалить, временная папка — в настройках узла
    /// (карандаш). Пока — в памяти; хранение на сервере — следующим шагом. Без Revit API: в NavisworksExportOptions —
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

        /// <summary>Коэффициент фасетизации из текста: «1», «0,5», «0.5»; больше нуля.</summary>
        public static bool TryFaceting(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>Метки в строке папки и имя NWC из имени модели.</summary>
    public static class ExportNames
    {
        /// <summary>Метки строки папки (разработчик, 02.11.2026: «оставим пока только блок и дату»); остальное пишется текстом.</summary>
        public static readonly string[] BuiltInLabels = { "Блок", "Дата" };

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
