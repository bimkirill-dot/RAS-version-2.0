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

    /// <summary>
    /// «Экспорт RVT» — общие настройки (шестерёнка, разработчик, 08.11.2026): проверка при открытии, рабочие наборы, имя RVT, что
    /// делать с уже лежащим RVT.
    /// </summary>
    public sealed class RvtOptions
    {
        /// <summary>Проверка (Audit) при открытии модели: Revit исправляет ошибки; открытие дольше. По умолчанию — нет.</summary>
        public bool Audit { get; set; }
        /// <summary>Рабочие наборы: оставить (новый центральный файл, все освобождены) — да; нет — обычный файл без рабочих наборов.</summary>
        public bool KeepWorksets { get; set; } = true;
        /// <summary>Имя RVT: обрезать всё после этой части имени модели (как у NWC); пусто — имя модели.</summary>
        public string TrimAfter { get; set; } = "";
        /// <summary>RVT с таким именем уже лежит: перезаписать — да; нет — пропустить.</summary>
        public bool Overwrite { get; set; } = true;

        // ── пересохранение на место (разработчик, 08.11.2026: «чтобы уменьшить вес файла») ──
        /// <summary>«Сжать модель» при пересохранении.</summary>
        public bool ResaveCompact { get; set; } = true;
        /// <summary>«Проверить модель» (Audit) при открытии для пересохранения.</summary>
        public bool ResaveAudit { get; set; } = true;
        /// <summary>Папка резервных копий — прежняя модель перед заменой (одна на модель); пусто — на этом компьютере (DefaultBackupDir).</summary>
        public string BackupFolder { get; set; } = "";

        public static string DefaultBackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAS_v_2.0", "RAS-Clash Analysis", "Пересохранение", "Резервные копии");

        /// <summary>Куда класть резервные копии: заданная папка или по умолчанию.</summary>
        public string BackupDir => string.IsNullOrWhiteSpace(BackupFolder) ? DefaultBackupDir : BackupFolder.Trim();

        public RvtOptions Clone() => (RvtOptions)MemberwiseClone();
    }

    /// <summary>
    /// Отдел: код в имени модели («KJ» — в «…_KJ01_…») — для {Отдел}. Эталона и шаблона вида больше нет (разработчик, 03.11.2026:
    /// «лабуду с rvt / rte убираем — просто проверяем свой вид Navisworks перед выгрузкой»); в прежних файлах эти поля не читаются.
    /// </summary>
    public sealed class DepartmentTemplate
    {
        public string Department { get; set; }
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

        /// <summary>Настройки отчётов Clash Detective (шестерёнка → «Настройки отчётов», 08.11.2026).</summary>
        public ReportOptions Report { get; set; } = ReportOptions.Defaults();

        /// <summary>Выгрузка RVT (шестерёнка → «Экспорт RVT», 08.11.2026).</summary>
        public RvtOptions Rvt { get; set; } = new RvtOptions();

        // что удалить из копии модели перед выгрузкой (разработчик, 02.11.2026: «можно вынести в общие настройки»); связанное
        // в NWC и так не попадёт, импортированный DWG — попадёт
        public bool RemoveRevitLinks { get; set; } = true;
        public bool RemoveCad { get; set; } = true;
        public bool RemovePointClouds { get; set; } = true;
        public bool RemoveIfc { get; set; } = true;

        /// <summary>
        /// Имя NWC: обрезать всё после этой части имени модели (разработчик, 07.11.2026: «Имя NWC: обрезать всё после — и пользователь
        /// сам вводит после чего»; было — галочка «по шаблону М3», 03.11.2026); пусто — имя как есть. ExportNames.NwcName.
        /// </summary>
        public string TrimAfter { get; set; } = "М3";

        /// <summary>Прежняя галочка «Обрезать по шаблону М3»: читается из файлов до 07.11.2026 (TrimAfter нет — «М3» или пусто), пишется
        /// для прежних сборок плагина у коллег.</summary>
        public bool TrimName { get; set; } = true;

        /// <summary>{Блок} — часть имени модели вида «буква и цифры»: «S6», «S10» (разработчик, 02.11.2026: «зачем по частям делить»).</summary>
        public string BlockPrefix { get; set; } = "S";

        /// <summary>{Дата} — формат (.NET); как в папках отчётов сейчас — «01.10.2026».</summary>
        public string DateFormat { get; set; } = "dd.MM.yyyy";

        /// <summary>
        /// Копий моделей Revit Server одновременно — наперёд, пока Revit выгружает текущую (разработчик, 04.11.2026: «по умолчанию
        /// 4»), от 1 до 8. Больше — быстрее, пока успевает Revit (~1 модель в минуту), но нагружает сервер коллег.
        /// </summary>
        public int ParallelCopies { get; set; } = 4;

        /// <summary>Отделы — их коды ищутся в имени модели ({Отдел}).</summary>
        public List<DepartmentTemplate> Departments { get; set; } = new List<DepartmentTemplate>();


        /// <summary>Форматы {Дата} на выбор.</summary>
        public static readonly string[] DateFormats = { "dd.MM.yyyy", "yyyy-MM-dd", "yyyyMMdd", "yyyy.MM.dd" };

        /// <summary>Взять всё из другого (сохранить окно или импорт): объект общий с окном задач — меняется на месте.</summary>
        public void CopyFrom(ExportSettings s)
        {
            Nwc = s.Nwc.Clone();
            Report = (s.Report ?? ReportOptions.Defaults()).Clone();
            Rvt = (s.Rvt ?? new RvtOptions()).Clone();
            RemoveRevitLinks = s.RemoveRevitLinks;
            RemoveCad = s.RemoveCad;
            RemovePointClouds = s.RemovePointClouds;
            RemoveIfc = s.RemoveIfc;
            TrimName = s.TrimName;
            TrimAfter = s.TrimAfter;
            BlockPrefix = s.BlockPrefix;
            DateFormat = s.DateFormat;
            ParallelCopies = s.ParallelCopies;
            Departments = s.Departments.Select(d => d.Clone()).ToList();
        }

        public ExportSettings Clone()
        {
            var copy = (ExportSettings)MemberwiseClone();
            copy.Nwc = Nwc.Clone();
            copy.Rvt = (Rvt ?? new RvtOptions()).Clone();
            copy.Report = Report?.Clone();
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
        public static string BareName(string modelFile)
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
        /// Имя NWC из имени модели: всё после части trimAfter отрезается (разработчик, 07.11.2026: «Имя NWC: обрезать всё после …»;
        /// пример «0000_01_01_S01_01_М3_BIM_Alma» и «М3» → «0000_01_01_S01_01_М3»). Часть — целая: до неё начало имени, «_», пробел
        /// или «-», после — конец, «_», пробел, «-», «.» («М3» не найдётся в «М31»). Без учёта регистра, русские и латинские буквы,
        /// одинаковые на вид (М и M, А и A…), — одно и то же. Пусто или части нет — имя как есть, matches = false.
        /// </summary>
        public static string NwcName(string modelFile, string trimAfter, out bool matches)
        {
            var name = BareName(modelFile).Trim();
            var marker = (trimAfter ?? "").Trim();
            matches = false;
            if (marker.Length == 0) return name;
            var n = Fold(name);
            var m = Fold(marker);
            for (var i = n.IndexOf(m, StringComparison.Ordinal); i >= 0; i = n.IndexOf(m, i + 1, StringComparison.Ordinal))
            {
                var end = i + m.Length;
                if ((i == 0 || "_ -".IndexOf(name[i - 1]) >= 0) && (end == name.Length || "_ -.".IndexOf(name[end]) >= 0))
                {
                    matches = true;
                    return name.Substring(0, end).TrimEnd();
                }
            }
            return name;
        }

        // заглавные; русские буквы, одинаковые на вид с латинскими, — латинскими (длина та же — места совпадают)
        private static string Fold(string s)
        {
            var chars = s.ToUpperInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var at = Cyrillic.IndexOf(chars[i]);
                if (at >= 0) chars[i] = Latin[at];
            }
            return new string(chars);
        }

        private const string Cyrillic = "АВЕКМНОРСТХУ";
        private const string Latin = "ABEKMHOPCTXY";
    }
}
