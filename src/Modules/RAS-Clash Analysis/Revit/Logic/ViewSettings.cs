using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Личные настройки вида коллизии — кнопка «Настройки вида» на ленте (DECISIONS.md, «Мост в Revit…»): у каждого свои,
    /// файл — в личной папке модуля. По умолчанию: 3D, обрезка по размеру элементов, «Тонированный», красить — да,
    /// изолировать — нет, предупреждения — включены. Незнакомое и испорченное — значения по умолчанию.
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

        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "isolate")] public bool Isolate { get; set; }
        /// <summary>Запас обрезки — по размеру элементов; иначе — MarginMeters.</summary>
        [DataMember(Name = "marginBySize")] public bool MarginBySize { get; set; }
        [DataMember(Name = "marginMeters")] public double MarginMeters { get; set; }
        [DataMember(Name = "style")] public string Style { get; set; }
        [DataMember(Name = "paint")] public bool Paint { get; set; }
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
            Style = StyleShaded;
            Paint = true;
            WarnUnloaded = true;
            WarnMissingLink = true;
        }

        /// <summary>Незнакомый тип или стиль — по умолчанию, запас — в разумных пределах.</summary>
        public ViewSettings Normalize()
        {
            if (!Kinds.Contains(Kind)) Kind = Kind3D;
            if (!Styles.Contains(Style)) Style = StyleShaded;
            if (double.IsNaN(MarginMeters) || double.IsInfinity(MarginMeters)) MarginMeters = 1;
            MarginMeters = Math.Max(MinMargin, Math.Min(MaxMargin, MarginMeters));
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
    }
}
