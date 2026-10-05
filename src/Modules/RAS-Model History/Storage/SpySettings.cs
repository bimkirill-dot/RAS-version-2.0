using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Настройки плагина. Общие для команды (что отслеживать, лимиты) — из RasModelHistory.settings.json рядом с DLL,
    /// личные (тема, период по умолчанию) — из settings.json в DataFolders.UserDataDir (см. Load).
    /// Папка журналов в настройках не задаётся — её даёт ядро (DataFolders.LogFolder).
    /// </summary>
    public sealed class SpySettings
    {
        /// <summary>Детализация «было → стало» по параметрам.</summary>
        public bool TrackParameters { get; set; } = true;
        /// <summary>При открытии в фоне запоминать сведения обо всех элементах и снимки параметров видов, шаблонов, типов.</summary>
        public bool SnapshotOnOpen { get; set; } = true;
        /// <summary>
        /// Полный снимок: при открытии запоминать параметры ВСЕХ элементов модели (во временном файле на диске),
        /// чтобы знать «было» для любого изменения и значения параметров удалённых элементов.
        /// </summary>
        public bool SnapshotAllElements { get; set; } = true;
        /// <summary>
        /// Сохранять снимки между сеансами: при следующем открытии той же модели переснимаются
        /// только элементы, изменившиеся с прошлого раза (проверка по версии элемента Revit).
        /// </summary>
        public bool PersistSnapshots { get; set; } = true;
        /// <summary>Удалять сохранённые снимки моделей, которые не открывались дольше N дней.</summary>
        public int SnapshotCacheDays { get; set; } = 30;
        /// <summary>Фиксировать изменения других пользователей, пришедшие при синхронизации/обновлении/открытии.</summary>
        public bool DetectRemoteChanges { get; set; } = true;
        /// <summary>Записывать служебные элементы без категории (эскизы, внутренние объекты Revit).</summary>
        public bool RecordNoise { get; set; } = false;
        /// <summary>Отслеживать изменения в редакторе семейств.</summary>
        public bool TrackFamilyDocuments { get; set; } = false;
        /// <summary>Сколько записей максимум на одну транзакцию (остальное — одной сводной записью).</summary>
        public int MaxRecordsPerTransaction { get; set; } = 5000;
        /// <summary>Для скольких элементов одной транзакции вычислять разницу параметров.</summary>
        public int MaxParamDiffPerTransaction { get; set; } = 500;
        /// <summary>Сколько выделенных элементов «фотографировать» заранее.</summary>
        public int MaxSelectionSnapshot { get; set; } = 300;
        /// <summary>Сколько снимков параметров держать в оперативной памяти (остальные — во временном файле на диске).</summary>
        public int MaxSnapshotElements { get; set; } = 3000;
        /// <summary>Период по умолчанию в панели, дней (0 — всё время).</summary>
        public int DefaultPeriodDays { get; set; } = 7;
        /// <summary>Максимум строк в таблице.</summary>
        public int MaxDisplayedRows { get; set; } = 50000;
        /// <summary>Тема панели: «Auto» — как в Revit, «Light» — светлая, «Dark» — тёмная.</summary>
        public string UiTheme { get; set; } = "Auto";

        public static string UserSettingsPath => Path.Combine(DataFolders.UserDataDir, "settings.json");

        public static string CompanySettingsPath
        {
            get
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                return Path.Combine(dir, "RasModelHistory.settings.json");
            }
        }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Что отслеживать и лимиты — одинаковые у всей команды: только из общего файла RasModelHistory.settings.json рядом
        /// с плагином (его раздаёт BIM-менеджер). Личный файл (UserSettingsPath) задаёт только тему
        /// и период по умолчанию. Раньше личный файл целиком заменял общий: после любого «Сохранить» правки общего файла
        /// до человека не доходили, и любой мог выключить запись изменений.
        /// </summary>
        public static SpySettings Load()
        {
            SpySettings s = null;
            try
            {
                if (File.Exists(CompanySettingsPath)) s = Parse(File.ReadAllText(CompanySettingsPath));
            }
            catch (Exception ex)
            {
                Diag.Log("Settings load failed: " + CompanySettingsPath, ex);
            }
            s ??= new SpySettings();
            try
            {
                if (File.Exists(UserSettingsPath))
                {
                    // в личных файлах прежних версий записаны все поля — берём только личные
                    var json = LogFolderRx.Replace(File.ReadAllText(UserSettingsPath), "\"LogFolder\": \"\"");
                    var personal = JsonSerializer.Deserialize<PersonalSettings>(json, Options);
                    if (!string.IsNullOrEmpty(personal?.UiTheme)) s.UiTheme = personal.UiTheme;
                    if (personal?.DefaultPeriodDays != null) s.DefaultPeriodDays = personal.DefaultPeriodDays.Value;
                }
            }
            catch (Exception ex)
            {
                Diag.Log("Settings load failed: " + UserSettingsPath, ex);
            }
            return s.Normalize();
        }

        /// <summary>Личные настройки — то, что каждый выбирает сам.</summary>
        private sealed class PersonalSettings
        {
            public string UiTheme { get; set; }
            public int? DefaultPeriodDays { get; set; }
        }

        /// <summary>
        /// Разбор настроек. В файлах прежних версий есть поле "LogFolder" — папка теперь зашита в плагин, и оно
        /// не нужно. Но путь там бывает записан с одинарными обратными слешами ("\\server\BIM"), и тогда файл
        /// не разобрался бы целиком, поэтому значение вырезается до разбора: остальные настройки сохраняются.
        /// </summary>
        public static SpySettings Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            json = LogFolderRx.Replace(json, "\"LogFolder\": \"\"");
            return JsonSerializer.Deserialize<SpySettings>(json, Options);
        }

        private static readonly Regex LogFolderRx =
            new Regex("\"LogFolder\"\\s*:\\s*\"[^\"\\r\\n]*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Сохранить личное (тема, период по умолчанию). Общие для команды настройки — только в общем файле.</summary>
        public void Save()
        {
            Directory.CreateDirectory(DataFolders.UserDataDir);
            var personal = new PersonalSettings { UiTheme = UiTheme, DefaultPeriodDays = DefaultPeriodDays };
            File.WriteAllText(UserSettingsPath, JsonSerializer.Serialize(personal, Options));
        }

        public SpySettings Clone()
        {
            return (SpySettings)MemberwiseClone();
        }

        /// <summary>Скопировать значения из другого объекта (общий экземпляр настроек не пересоздаётся).</summary>
        public void CopyFrom(SpySettings other)
        {
            foreach (var p in typeof(SpySettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite) p.SetValue(this, p.GetValue(other));
            }
            Normalize();
        }

        public SpySettings Normalize()
        {
            if (!string.Equals(UiTheme, "Light", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(UiTheme, "Dark", StringComparison.OrdinalIgnoreCase))
                UiTheme = "Auto";
            MaxRecordsPerTransaction = Clamp(MaxRecordsPerTransaction, 50, 1_000_000);
            MaxParamDiffPerTransaction = Clamp(MaxParamDiffPerTransaction, 0, 1_000_000);
            MaxSelectionSnapshot = Clamp(MaxSelectionSnapshot, 0, 100_000);
            MaxSnapshotElements = Clamp(MaxSnapshotElements, 100, 5_000_000);
            DefaultPeriodDays = Clamp(DefaultPeriodDays, 0, 36500);
            MaxDisplayedRows = Clamp(MaxDisplayedRows, 1000, 5_000_000);
            SnapshotCacheDays = Clamp(SnapshotCacheDays, 1, 3650);
            return this;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }
}
