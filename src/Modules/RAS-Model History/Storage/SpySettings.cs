using System;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Настройки модуля. Общие для команды (что отслеживать, лимиты, папка журналов, период по умолчанию) — в общем файле
    /// на сервере, меняют BIM-специалисты в окне «Настройки» (разработчик, 27.10.2026); личная — только тема. Откуда что
    /// читается и куда пишется — SettingsFiles; здесь — значения и правила, без файлов (проверяется тестами).
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
        /// <summary>Период по умолчанию в панели, дней (0 — всё время). Общий для команды.</summary>
        public int DefaultPeriodDays { get; set; } = 7;
        /// <summary>Максимум строк в таблице.</summary>
        public int MaxDisplayedRows { get; set; } = 50000;
        /// <summary>
        /// Папка журналов команды; пусто — по умолчанию (Data\RAS-Model History на сервере плагина). Действует с запуска Revit:
        /// журналы из прежней папки не переносятся.
        /// </summary>
        public string JournalFolder { get; set; } = "";
        /// <summary>Кто последним сохранил общие настройки (имя и логин) — показывается в окне.</summary>
        public string ChangedBy { get; set; }
        /// <summary>Когда («dd.MM.yyyy HH:mm»).</summary>
        public string ChangedAt { get; set; }
        /// <summary>Тема панели: «Auto» — как в Revit, «Light» — светлая, «Dark» — тёмная. Личная.</summary>
        public string UiTheme { get; set; } = "Auto";

        /// <summary>Единственная личная настройка: в общем файле не пишется и не действует.</summary>
        private const string PersonalKey = nameof(UiTheme);

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // имена по-русски — читаемо в файле
        };

        private static readonly JsonNodeOptions NodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        private static readonly JsonDocumentOptions DocOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        /// <summary>
        /// Настройки из трёх частей, каждая поверх предыдущей: исходные из сборки (RasModelHistory.settings.json), общий файл
        /// команды (с сервера или его копия на компьютере), личный файл — из него только тема (в личных файлах прежних
        /// версий записаны все поля: они не должны перебивать общие). Испорченная часть пропускается.
        /// </summary>
        public static SpySettings Compose(string builtJson, string teamJson, string personalJson)
        {
            var merged = new JsonObject(NodeOptions);
            Overlay(merged, builtJson, "из сборки", personal: false);
            Overlay(merged, teamJson, "общие", personal: false);
            Overlay(merged, personalJson, "личные", personal: true);
            SpySettings s = null;
            try { s = merged.Deserialize<SpySettings>(Options); }
            catch (Exception ex) { Diag.Log("Настройки не разобраны", ex); }
            return (s ?? new SpySettings()).Normalize();
        }

        private static void Overlay(JsonObject target, string json, string what, bool personal)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                if (!(JsonNode.Parse(LogFolderRx.Replace(json, "\"LogFolder\": \"\""), NodeOptions, DocOptions) is JsonObject part)) return;
                foreach (var kv in part)
                {
                    var isPersonal = string.Equals(kv.Key, PersonalKey, StringComparison.OrdinalIgnoreCase);
                    if (isPersonal != personal) continue;
                    target[kv.Key] = kv.Value?.DeepClone();
                }
            }
            catch (Exception ex) { Diag.Log("Настройки (" + what + ") не разобраны — пропущены", ex); }
        }

        /// <summary>Разбирается ли текст общего файла — перед тем как взять его с сервера.</summary>
        public static bool IsValid(string json)
        {
            try { return JsonNode.Parse(json, NodeOptions, DocOptions) is JsonObject; }
            catch (JsonException) { return false; }
        }

        /// <summary>Общий файл команды: всё, кроме личной темы.</summary>
        public string ToTeamJson()
        {
            var node = JsonSerializer.SerializeToNode(this, Options) as JsonObject ?? new JsonObject();
            node.Remove(PersonalKey);
            return node.ToJsonString(Options);
        }

        /// <summary>Личный файл: только тема.</summary>
        public string ToPersonalJson() => new JsonObject { [PersonalKey] = UiTheme }.ToJsonString(Options);

        /// <summary>
        /// В файлах прежних версий есть поле "LogFolder" (папку журналов тогда задавал каждый у себя) — оно не действует.
        /// Путь там бывает записан с одинарными обратными косыми ("\\server\BIM"), и файл не разобрался бы целиком, поэтому
        /// значение вырезается до разбора: остальные настройки сохраняются.
        /// </summary>
        private static readonly Regex LogFolderRx =
            new Regex("\"LogFolder\"\\s*:\\s*\"[^\"\\r\\n]*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            JournalFolder = (JournalFolder ?? "").Trim().Trim('"').Trim();
            return this;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }
}
