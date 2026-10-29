using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Личные настройки вида коллизии — кнопка «Настройки вида» на ленте (DECISIONS.md, «Мост в Revit…»): у каждого свои,
    /// файл — в личной папке модуля. По умолчанию: 3D, обрезка «Авто» (по размеру места коллизии), «Тонированный», красить — да,
    /// изолировать — нет, прозрачность — нет, предупреждения — включены. Незнакомое и испорченное — значения по умолчанию.
    /// </summary>
    [DataContract]
    public sealed class ViewSettings
    {
        public const string Kind3D = "3d", KindPlan = "plan", KindSection = "section", KindAuto = "auto";
        public static readonly string[] Kinds = { Kind3D, KindPlan, KindSection, KindAuto };
        /// <summary>Классические стили Revit: «Каркас», «Скрытые линии», «Тонированный», «Согласованные цвета», «Реалистичный».</summary>
        public const string StyleWireframe = "wireframe", StyleHidden = "hidden", StyleShaded = "shaded", StyleConsistent = "consistent", StyleRealistic = "realistic";
        public static readonly string[] Styles = { StyleWireframe, StyleHidden, StyleShaded, StyleConsistent, StyleRealistic };
        public const double MinMargin = 0.1, MaxMargin = 20;
        /// <summary>Наибольшая прозрачность, %: у Revit — 100 (элемент не виден совсем).</summary>
        public const int MaxTransparency = 100;

        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "isolate")] public bool Isolate { get; set; }
        /// <summary>Запас обрезки — по размеру элементов; иначе — MarginMeters.</summary>
        [DataMember(Name = "marginBySize")] public bool MarginBySize { get; set; }
        [DataMember(Name = "marginMeters")] public double MarginMeters { get; set; }
        /// <summary>
        /// «Границы вида»: 3D-вид обрезается рамкой 3D-вида человека, а не вокруг места коллизии (разработчик, 29.10.2026);
        /// сильнее MarginBySize. Только для 3D-вида — план и разрез обрезаются, как задано MarginBySize.
        /// </summary>
        [DataMember(Name = "cropToView")] public bool CropToView { get; set; }
        [DataMember(Name = "style")] public string Style { get; set; }
        [DataMember(Name = "paint")] public bool Paint { get; set; }
        /// <summary>
        /// Прозрачность элементов коллизии и остальных, % (разработчик, 29.10.2026); и при «Изолировать» (разработчик, в тот же
        /// день): остаются видны обобщённые модели (отверстия) и элементы связи тех же категорий.
        /// </summary>
        [DataMember(Name = "clashTransparency")] public int ClashTransparency { get; set; }
        [DataMember(Name = "otherTransparency")] public int OtherTransparency { get; set; }
        [DataMember(Name = "warnUnloaded")] public bool WarnUnloaded { get; set; }
        [DataMember(Name = "warnMissingLink")] public bool WarnMissingLink { get; set; }

        public ViewSettings() => Defaults();

        // при чтении конструктор не вызывается: значения по умолчанию — до чтения, чтобы новые поля в старом файле были как надо
        [OnDeserializing]
        private void BeforeRead(StreamingContext context) => Defaults();

        private void Defaults()
        {
            Kind = Kind3D;
            Isolate = false;
            MarginBySize = true;
            MarginMeters = 1;
            CropToView = false;
            Style = StyleShaded;
            Paint = true;
            ClashTransparency = 0;
            OtherTransparency = 0;
            WarnUnloaded = true;
            WarnMissingLink = true;
        }

        /// <summary>Незнакомый тип или стиль — по умолчанию, запас и прозрачность — в разумных пределах.</summary>
        public ViewSettings Normalize()
        {
            if (!Kinds.Contains(Kind)) Kind = Kind3D;
            if (!Styles.Contains(Style)) Style = StyleShaded;
            if (double.IsNaN(MarginMeters) || double.IsInfinity(MarginMeters)) MarginMeters = 1;
            MarginMeters = Math.Max(MinMargin, Math.Min(MaxMargin, MarginMeters));
            ClashTransparency = Math.Max(0, Math.Min(MaxTransparency, ClashTransparency));
            OtherTransparency = Math.Max(0, Math.Min(MaxTransparency, OtherTransparency));
            return this;
        }

        public ViewSettings Copy() => (ViewSettings)MemberwiseClone();

        /// <summary>Прочитать; файла нет или он испорчен — значения по умолчанию.</summary>
        public static ViewSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new ViewSettings();
                using (var stream = File.OpenRead(path))
                    return ((ViewSettings)new DataContractJsonSerializer(typeof(ViewSettings)).ReadObject(stream) ?? new ViewSettings()).Normalize();
            }
            catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException)
            {
                return new ViewSettings();
            }
        }

        /// <summary>Записать: сначала во временный файл, потом заменить — оборванная запись не портит прежние настройки.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                new DataContractJsonSerializer(typeof(ViewSettings)).WriteObject(stream, this);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    /// <summary>Правила вида коллизии без Revit API: какой тип вида, сколько запаса вокруг пары.</summary>
    public static class ViewRules
    {
        /// <summary>
        /// Тип вида: выбранный в настройках, а при «Авто» — по категориям элементов (имена BuiltInCategory): перекрытие или
        /// фундаментная плита — план, стена — разрез, остальное (потолки, крыши и т. д.) — 3D (разработчик, 19.10.2026).
        /// </summary>
        public static string KindFor(string setting, System.Collections.Generic.IEnumerable<string> categories)
        {
            if (setting != ViewSettings.KindAuto) return ViewSettings.Kinds.Contains(setting) ? setting : ViewSettings.Kind3D;
            var list = (categories ?? Enumerable.Empty<string>()).ToList();
            if (list.Any(c => c == "OST_Floors" || c == "OST_StructuralFoundation")) return ViewSettings.KindPlan;
            if (list.Any(c => c == "OST_Walls")) return ViewSettings.KindSection;
            return ViewSettings.Kind3D;
        }

        /// <summary>Запас вокруг места коллизии, м: «по размеру» — половина наибольшего размера места коллизии, не меньше 0,5 м; иначе — заданный.</summary>
        public static double MarginMeters(ViewSettings settings, double pairSizeMeters) =>
            settings.MarginBySize ? Math.Max(0.5, 0.5 * pairSizeMeters) : settings.MarginMeters;

        /// <summary>«Границы вида» — только у 3D-вида (у плана и разреза — обрезка, как задано).</summary>
        public static bool CropToView(ViewSettings settings, string kind) => settings.CropToView && kind == ViewSettings.Kind3D;

        /// <summary>
        /// Прозрачность, которую ставить в виде, %. Остальные прозрачны — у элементов коллизии не меньше 1 %: 0 в настройке
        /// элемента Revit может принять за «не задано», и элемент взял бы прозрачность своей категории; 1 % на глаз не отличить от 0.
        /// </summary>
        public static (int Clash, int Other) Transparency(ViewSettings settings)
        {
            var other = settings.OtherTransparency;
            return (other > 0 ? Math.Max(1, settings.ClashTransparency) : settings.ClashTransparency, other);
        }
    }
}
