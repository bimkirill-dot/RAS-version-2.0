using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Связанный (или отклонённый) журнал: ключ — папка журнала, кто и когда связал.</summary>
    public sealed class JournalLinkEntry
    {
        [JsonPropertyName("key")] public string Key { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("by")] public string By { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    }

    /// <summary>links.json в папке журнала модели — общий для всех.</summary>
    public sealed class JournalLinksFile
    {
        /// <summary>Журналы прежней истории этой модели: читаются вместе с её журналом.</summary>
        [JsonPropertyName("linked")] public List<JournalLinkEntry> Linked { get; set; } = new List<JournalLinkEntry>();
        /// <summary>«Это другая модель»: найденный журнал с тем же путём больше не предлагать.</summary>
        [JsonPropertyName("rejected")] public List<JournalLinkEntry> Rejected { get; set; } = new List<JournalLinkEntry>();
    }

    /// <summary>Кто писал журнал и за какие месяцы — по именам файлов («пользователь@компьютер_ГГГГ-ММ.jsonl»).</summary>
    public sealed class JournalSummary
    {
        public List<string> Users = new List<string>();
        public string FirstMonth, LastMonth;
    }

    /// <summary>
    /// Связь журналов (DECISIONS.md модуля, «Связь журналов»): модель узнаётся по номеру хранилища; хранилище, восстановленное
    /// из копии, получает новый номер — и журнал начинается в новой папке. Прежний журнал находится по тому же пути на Revit
    /// Server (без имени сервера) и связывается: его записи читаются вместе. Связь — в links.json папки журнала, общая для всех.
    /// Без Revit API — проверяется тестами.
    /// </summary>
    public static class JournalLinks
    {
        public const string FileName = "links.json";
        private const int MaxJournals = 50;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static JournalLinksFile Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JournalLinksFile();
            try
            {
                var file = JsonSerializer.Deserialize<JournalLinksFile>(json, Json) ?? new JournalLinksFile();
                file.Linked = (file.Linked ?? new List<JournalLinkEntry>()).Where(e => !string.IsNullOrWhiteSpace(e?.Key)).ToList();
                file.Rejected = (file.Rejected ?? new List<JournalLinkEntry>()).Where(e => !string.IsNullOrWhiteSpace(e?.Key)).ToList();
                return file;
            }
            catch (JsonException)
            {
                return new JournalLinksFile(); // испорченный файл — как будто связей нет (журнал модели читается всё равно)
            }
        }

        public static string Serialize(JournalLinksFile file) => JsonSerializer.Serialize(file, Json);

        /// <summary>
        /// Место модели без имени сервера: «RSN://сервер/папка/модель.rvt» → «rsn:папка/модель.rvt» (одна модель открывается
        /// через разные серверы); остальное — путь целиком; без учёта регистра. Как ModelIdentity.KeyBasis.
        /// </summary>
        public static string Place(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path.Trim().Replace('\\', '/');
            const string rsn = "rsn://";
            if (p.StartsWith(rsn, StringComparison.OrdinalIgnoreCase))
            {
                var rest = p.Substring(rsn.Length);
                var slash = rest.IndexOf('/');
                return "rsn:" + (slash >= 0 ? rest.Substring(slash + 1) : rest).Trim('/').ToLowerInvariant();
            }
            return p.ToLowerInvariant();
        }

        /// <summary>Имя модели в ключе журнала — всё до последнего «_хэш»: по нему из списка папок выбираются возможные журналы.</summary>
        public static string NameOf(string key)
        {
            var underscore = key?.LastIndexOf('_') ?? -1;
            return underscore > 0 ? key.Substring(0, underscore) : key ?? "";
        }

        /// <summary>
        /// Какие журналы читать: свой, прежние ключи модели и связанные — со связями связанных (цепочка восстановлений); без повторов
        /// и кругов, не больше 50.
        /// </summary>
        public static List<string> ReadOrder(string key, IEnumerable<string> legacyKeys, Func<string, JournalLinksFile> linksOf)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            void Add(string k)
            {
                if (string.IsNullOrWhiteSpace(k) || result.Count >= MaxJournals || !seen.Add(k)) return;
                result.Add(k);
                queue.Enqueue(k);
            }
            Add(key);
            foreach (var k in legacyKeys ?? Enumerable.Empty<string>()) Add(k);
            while (queue.Count > 0)
            {
                var file = linksOf(queue.Dequeue());
                foreach (var e in file?.Linked ?? new List<JournalLinkEntry>()) Add(e.Key);
            }
            return result;
        }

        /// <summary>
        /// Журналы, похожие на прежнюю историю этой модели: то же место (путь без сервера), другой ключ, ещё не читаются и не
        /// отклонены («Это другая модель»). journals — ключ → путь из model.json.
        /// </summary>
        public static List<string> Candidates(string currentPath, IEnumerable<KeyValuePair<string, string>> journals,
            ICollection<string> reading, JournalLinksFile links)
        {
            var place = Place(currentPath);
            if (place.Length == 0) return new List<string>();
            var rejected = new HashSet<string>((links?.Rejected ?? new List<JournalLinkEntry>()).Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
            var read = new HashSet<string>(reading ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return journals
                .Where(j => !read.Contains(j.Key) && !rejected.Contains(j.Key) && Place(j.Value) == place)
                .Select(j => j.Key)
                .ToList();
        }

        /// <summary>Связать: добавить в linked (и убрать из rejected); уже связан — ничего.</summary>
        public static void Link(JournalLinksFile file, JournalLinkEntry entry)
        {
            file.Rejected.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (!file.Linked.Any(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase))) file.Linked.Add(entry);
        }

        /// <summary>Отвязать или «Это другая модель»: убрать из linked; reject — больше не предлагать.</summary>
        public static void Unlink(JournalLinksFile file, JournalLinkEntry entry, bool reject)
        {
            file.Linked.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (reject && !file.Rejected.Any(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase))) file.Rejected.Add(entry);
        }

        /// <summary>Кто писал и за какие месяцы — по именам файлов журнала.</summary>
        public static JournalSummary Summarize(IEnumerable<string> fileNames)
        {
            var summary = new JournalSummary();
            var months = new List<string>();
            foreach (var raw in fileNames ?? Enumerable.Empty<string>())
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(raw ?? "");
                var at = name.IndexOf('@');
                var underscore = name.LastIndexOf('_');
                if (at <= 0 || underscore <= at) continue;
                var user = name.Substring(0, at);
                if (!summary.Users.Contains(user, StringComparer.OrdinalIgnoreCase)) summary.Users.Add(user);
                var month = name.Substring(underscore + 1);
                if (month.Length == 7 && month[4] == '-') months.Add(month);
            }
            months.Sort(StringComparer.Ordinal);
            summary.FirstMonth = months.FirstOrDefault();
            summary.LastMonth = months.LastOrDefault();
            return summary;
        }

        /// <summary>«10.2026» или «09.2026 – 10.2026».</summary>
        public static string Period(JournalSummary s)
        {
            string M(string ym) => ym == null ? "" : ym.Substring(5, 2) + "." + ym.Substring(0, 4);
            if (s?.FirstMonth == null) return "";
            return s.FirstMonth == s.LastMonth ? M(s.FirstMonth) : M(s.FirstMonth) + " – " + M(s.LastMonth);
        }
    }
}
