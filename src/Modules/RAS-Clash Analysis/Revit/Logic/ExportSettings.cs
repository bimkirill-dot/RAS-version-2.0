using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Что выгружать: «Целый проект» или «Вид» — вид для выгрузки («Экспорт» окна экспортёра; «Выбор» не нужен — модель открывает плагин).</summary>
    public enum NwcScope { Model, View }

    /// <summary>«Конвертировать параметры объектов»: «Нет» / «Объекты» / «Все».</summary>
    public enum NwcParameters { None, Elements, All }

    /// <summary>
    /// Параметры выгрузки NWC — те же, что в окне экспортёра Navisworks (разработчик, 02.11.2026: «главные настройки — те же самые,
    /// что открываются при экспорте в NWC стандартным экспортёром»; значения по умолчанию — с его снимка окна). Без Revit API:
    /// в NavisworksExportOptions — при выгрузке.
    /// </summary>
    public sealed class NwcOptions
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
        /// <summary>Вид для выгрузки (при «Экспорт: Вид») по умолчанию; задача может задать свой. Нет в модели — создаётся.</summary>
        public string ViewName { get; set; } = "3D_Navisworks";
        public bool RoomGeometry { get; set; }

        public NwcOptions Clone() => (NwcOptions)MemberwiseClone();

        /// <summary>Коэффициент фасетизации из текста: «1», «0,5», «0.5»; больше нуля.</summary>
        public static bool TryFaceting(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>Отдел: код в имени модели («KJ» — в «…_KJ01_…») → эталон → шаблон вида для выгрузки.</summary>
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
    /// Общие настройки «Задач выгрузки» — шестерёнка главного окна: всё, что одно на компанию (разбор настроек, 02.11.2026):
    /// параметры выгрузки NWC, что удалить из модели перед выгрузкой, правило имени модели и метки, отделы. У
    /// проекта и задач — только своё (ExportNode.cs). Хранятся на сервере (ExportStore).
    /// </summary>
    public sealed class ExportSettings
    {
        public NwcOptions Nwc { get; set; } = new NwcOptions();

        // что удалить из копии модели перед выгрузкой (разработчик, 02.11.2026: «можно вынести в общие настройки»); связанное
        // в NWC и так не попадёт, импортированный DWG — попадёт
        public bool RemoveRevitLinks { get; set; } = true;
        public bool RemoveCad { get; set; } = true;
        public bool RemovePointClouds { get; set; } = true;
        public bool RemoveIfc { get; set; } = true;

        /// <summary>Обрезать имя NWC: всё после части «М3» (разработчик, 03.11.2026) — ExportNames.NwcName.</summary>
        public bool TrimName { get; set; } = true;

        /// <summary>{Блок} — часть имени модели вида «буква и цифры»: «S6», «S10» (разработчик, 02.11.2026: «зачем по частям делить»).</summary>
        public string BlockPrefix { get; set; } = "S";

        /// <summary>{Дата} — формат (.NET); как в папках отчётов сейчас — «01.10.2026».</summary>
        public string DateFormat { get; set; } = "dd.MM.yyyy";

        /// <summary>Отделы — их коды ищутся в имени модели ({Отдел}), по ним же эталон и шаблон вида.</summary>
        public List<DepartmentTemplate> Departments { get; set; } = new List<DepartmentTemplate>();


        /// <summary>Форматы {Дата} на выбор.</summary>
        public static readonly string[] DateFormats = { "dd.MM.yyyy", "yyyy-MM-dd", "yyyyMMdd", "yyyy.MM.dd" };

        /// <summary>Взять всё из другого (сохранить окно или импорт): объект общий с окном задач — меняется на месте.</summary>
        public void CopyFrom(ExportSettings s)
        {
            Nwc = s.Nwc.Clone();
            RemoveRevitLinks = s.RemoveRevitLinks;
            RemoveCad = s.RemoveCad;
            RemovePointClouds = s.RemovePointClouds;
            RemoveIfc = s.RemoveIfc;
            TrimName = s.TrimName;
            BlockPrefix = s.BlockPrefix;
            DateFormat = s.DateFormat;
            Departments = s.Departments.Select(d => d.Clone()).ToList();
        }

        public ExportSettings Clone()
        {
            var copy = (ExportSettings)MemberwiseClone();
            copy.Nwc = Nwc.Clone();
            copy.Departments = Departments.Select(d => d.Clone()).ToList();
            return copy;
        }
    }

    /// <summary>Метки в строке папки и имя NWC из имени модели.</summary>
    public static class ExportNames
    {
        /// <summary>Метки строки папки: блок и отдел — из имени модели, дата — день выгрузки; остальное пишется текстом.</summary>
        public static readonly string[] BuiltInLabels = { "Блок", "Отдел", "Дата", "Формат" };

        private static readonly Regex LabelRx = new Regex(@"\{([^{}]+)\}");

        /// <summary>Метки строки, по порядку, без повторов.</summary>
        public static List<string> LabelsIn(string template) =>
            LabelRx.Matches(template ?? "").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

        /// <summary>Подставить значения меток; метка без значения остаётся как есть («{Шифр}»).</summary>
        public static string Fill(string template, IDictionary<string, string> values) =>
            LabelRx.Replace(template ?? "", m =>
                values.TryGetValue(m.Groups[1].Value.Trim(), out var v) && !string.IsNullOrEmpty(v) ? v : m.Value);

        /// <summary>
        /// Имя модели без папки и расширения. Не Path.GetFileNameWithoutExtension: в .NET 8 у «\\сервер\файл.rvt» он отдаёт пусто
        /// (считает его корнем сетевой папки).
        /// </summary>
        private static string BareName(string modelFile)
        {
            var name = ExportNodes.FileName(modelFile);
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>Части имени модели через «_», каждая — до пробела: «…_M3 еще текст» → «M3».</summary>
        private static IEnumerable<string> Parts(string modelFile) =>
            BareName(modelFile).Split('_').Select(p =>
            {
                var space = p.IndexOf(' ');
                return (space >= 0 ? p.Substring(0, space) : p).Trim();
            });

        /// <summary>Блок — первая часть имени вида «префикс и цифры»: «S6», «S10», «S01»; нет — пусто.</summary>
        public static string Block(string modelFile, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return "";
            var rx = new Regex("^" + Regex.Escape(prefix.Trim()) + @"\d+$", RegexOptions.IgnoreCase);
            return Parts(modelFile).FirstOrDefault(p => rx.IsMatch(p)) ?? "";
        }

        /// <summary>
        /// Отдел — первый слева код из списка, найденный частью имени: сам код или код с цифрами («KJ», «KJ01»); подстрока не
        /// считается («AR» в «ArmanQala» — не отдел). Возвращается код, как в списке; не нашлось — пусто.
        /// </summary>
        public static string Department(string modelFile, IEnumerable<string> codes)
        {
            var list = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            foreach (var part in Parts(modelFile))
                foreach (var code in list)
                    if (Regex.IsMatch(part, "^" + Regex.Escape(code) + @"\d*$", RegexOptions.IgnoreCase)) return code;
            return "";
        }

        /// <summary>
        /// Имя NWC из имени модели. trim — до части «М3» включительно, остальное отрезается (разработчик, 03.11.2026: пример
        /// «0000_01_01_S01_01_М3_BIM_Alma → 0000_01_01_S01_01_М3»; «части Р3 не будет»): «…_M3 еще текст.rvt» и «…_M3_еще
        /// текст.rvt» → «…_M3». Буква «М» — латинская или русская, без учёта регистра; так режется по смыслу, а не по счёту частей —
        /// с отделом в имени и без. Части «М3» нет — имя как есть, matches = false («не по шаблону»).
        /// </summary>
        public static string NwcName(string modelFile, bool trim, out bool matches)
        {
            var name = BareName(modelFile).Trim();
            var split = name.Split('_');
            var at = Array.FindIndex(split, p => StageRx.IsMatch(p));
            matches = at >= 0;
            if (!trim || !matches) return name;
            var kept = split.Take(at + 1).ToArray();
            var space = kept[at].IndexOf(' ');
            if (space >= 0) kept[at] = kept[at].Substring(0, space);
            return string.Join("_", kept);
        }

        // часть «М3» — сама или с текстом через пробел («М3 еще текст»); М — латинская или русская
        private static readonly Regex StageRx = new Regex(@"^[MМ]3(\s|$)", RegexOptions.IgnoreCase);
    }
}
