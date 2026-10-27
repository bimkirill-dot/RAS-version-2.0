using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.SheetManager.Logic
{
    /// <summary>
    /// Настройки кнопки «Подпись» — общие для всех, кто ею пользуется (шестерёнка в окне; файл — Data\RAS-Sheet Manager на
    /// сервере): папка, размер и очистка подписей; регламент наименования (слово «Подпись», кто со знаком «!») — только для
    /// чтения в окне. Таблица выбора и форма формулы — из самого семейства (окно «Подпись»), цвет — всегда синий
    /// (разработчик, 27.10.2026). По умолчанию — как в dwg_clean.bat: высота 7 мм, ширина не больше 22 мм, точность 0,02 мм.
    /// Незнакомое и испорченное — значения по умолчанию; поля прежних версий (таблица, цвет…) не читаются.
    /// </summary>
    [DataContract]
    public sealed class SignSettings
    {
        /// <summary>Папка с исходными подписями по умолчанию (обычно на сервере); пусто — выбрать в окне.</summary>
        [DataMember(Name = "folder")] public string Folder { get; set; }
        [DataMember(Name = "sizeMode")] public string SizeMode { get; set; }
        [DataMember(Name = "height")] public double Height { get; set; }
        [DataMember(Name = "maxWidth")] public double MaxWidth { get; set; }
        [DataMember(Name = "width")] public double Width { get; set; }
        [DataMember(Name = "maxHeight")] public double MaxHeight { get; set; }
        [DataMember(Name = "boxWidth")] public double BoxWidth { get; set; }
        [DataMember(Name = "boxHeight")] public double BoxHeight { get; set; }
        [DataMember(Name = "tolerance")] public double Tolerance { get; set; }
        [DataMember(Name = "hatch")] public string Hatch { get; set; }
        /// <summary>accoreconsole.exe; пусто — найти самый новый AutoCAD.</summary>
        [DataMember(Name = "acad")] public string Acad { get; set; }
        /// <summary>Слово в имени файла, после которого — фамилия.</summary>
        [DataMember(Name = "fileWord")] public string FileWord { get; set; }
        /// <summary>Кто со знаком «!» — на месте отдела (ГИП, ГАП).</summary>
        [DataMember(Name = "leaders")] public string Leaders { get; set; }
        /// <summary>
        /// Формула новой подписи, если на таблицу в семействе формул ещё нет: из какого столбца номер, по какому параметру,
        /// что — если фамилии нет. Если формулы есть — берутся их столбец, параметр и значение (у семейства ГИП — свои).
        /// </summary>
        [DataMember(Name = "formColumn")] public string FormColumn { get; set; }
        [DataMember(Name = "formKey")] public string FormKey { get; set; }
        [DataMember(Name = "formDefault")] public string FormDefault { get; set; }
        [DataMember(Name = "changedBy")] public string ChangedBy { get; set; }
        [DataMember(Name = "changedAt")] public string ChangedAt { get; set; }

        public SignSettings() => Defaults();

        // при чтении конструктор не вызывается: значения по умолчанию — до чтения, чтобы новые поля в старом файле были как надо
        [OnDeserializing]
        private void BeforeRead(StreamingContext context) => Defaults();

        private void Defaults()
        {
            Folder = "";
            SizeMode = SizeModes.Height;
            Height = 7; MaxWidth = 22;
            Width = 20; MaxHeight = 9;
            BoxWidth = 22; BoxHeight = 8;
            Tolerance = 0.02;
            Hatch = HatchModes.Outline;
            Acad = "";
            FileWord = "Подпись";
            Leaders = "ГИП, ГАП";
            FormColumn = "Да/Нет";
            FormKey = "Фамилия";
            FormDefault = "1000";
        }

        public SignSettings Normalize()
        {
            var d = new SignSettings();
            if (!SizeModes.All.Contains(SizeMode)) SizeMode = d.SizeMode;
            if (Hatch != HatchModes.Drop) Hatch = HatchModes.Outline;
            Height = Range(Height, 0.5, 200, d.Height); MaxWidth = Range(MaxWidth, 0, 500, d.MaxWidth);
            Width = Range(Width, 0.5, 500, d.Width); MaxHeight = Range(MaxHeight, 0, 200, d.MaxHeight);
            BoxWidth = Range(BoxWidth, 0.5, 500, d.BoxWidth); BoxHeight = Range(BoxHeight, 0.5, 200, d.BoxHeight);
            Tolerance = Range(Tolerance, 0.001, 1, d.Tolerance);
            Folder = (Folder ?? "").Trim();
            Acad = (Acad ?? "").Trim();
            FileWord = Text(FileWord, d.FileWord);
            Leaders = Leaders == null ? d.Leaders : string.Join(", ", SignFileName.List(Leaders));
            FormColumn = Text(FormColumn, d.FormColumn);
            FormKey = Text(FormKey, d.FormKey);
            FormDefault = Text(FormDefault, d.FormDefault).Replace(',', '.');
            if (!double.TryParse(FormDefault, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) FormDefault = d.FormDefault;
            return this;
        }

        private static double Range(double v, double min, double max, double fallback) =>
            double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max ? fallback : v;

        private static string Text(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

        /// <summary>«0,0,255» → (0,0,255); не разобрать — null.</summary>
        public static byte[] Rgb(string s)
        {
            var parts = (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return null;
            var b = new byte[3];
            for (var i = 0; i < 3; i++) if (!byte.TryParse(parts[i], out b[i])) return null;
            return b;
        }

        /// <summary>Проверить имя файла подписи по регламенту.</summary>
        public SignFileName CheckName(string fileName) =>
            SignFileName.Check(fileName, FileWord, new List<string>(), SignFileName.List(Leaders));

        /// <summary>Цвет подписей в Revit — всегда синий (разработчик, 27.10.2026: «пусть всегда будет такой»); очищенные DWG — тоже синие.</summary>
        public static readonly byte[] SignColor = { 0, 0, 255 };

        public CleanOptions Clean => new CleanOptions
        {
            Mode = SizeMode, Height = Height, MaxWidth = MaxWidth, Width = Width, MaxHeight = MaxHeight,
            BoxWidth = BoxWidth, BoxHeight = BoxHeight, Tolerance = Tolerance, Hatch = Hatch,
        };

        public SignSettings Copy() => (SignSettings)MemberwiseClone();

        public static SignSettings FromJson(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                    return ((SignSettings)new DataContractJsonSerializer(typeof(SignSettings)).ReadObject(stream) ?? new SignSettings()).Normalize();
            }
            catch (Exception ex) when (ex is SerializationException || ex is IOException || ex is ArgumentException || ex is InvalidCastException)
            {
                return null;
            }
        }

        public string ToJson()
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(SignSettings)).WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    /// <summary>Личное: последняя папка и масштаб для каждого семейства подписей (у ГИПов подпись крупнее).</summary>
    [DataContract]
    public sealed class SignPersonal
    {
        [DataMember(Name = "lastFolder")] public string LastFolder { get; set; } = "";
        [DataMember(Name = "scales")] public Dictionary<string, double> Scales { get; set; } = new Dictionary<string, double>();

        [OnDeserializing]
        private void BeforeRead(StreamingContext context)
        {
            LastFolder = "";
            Scales = new Dictionary<string, double>();
        }

        public double ScaleFor(string family) =>
            Scales != null && Scales.TryGetValue(family ?? "", out var k) && k > 0 && k < 100 ? k : 1;

        public static SignPersonal Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new SignPersonal();
                using (var stream = File.OpenRead(path))
                {
                    var p = (SignPersonal)new DataContractJsonSerializer(typeof(SignPersonal), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream) ?? new SignPersonal();
                    p.Scales = p.Scales ?? new Dictionary<string, double>();
                    return p;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException)
            {
                return new SignPersonal();
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                new DataContractJsonSerializer(typeof(SignPersonal), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(stream, this);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
