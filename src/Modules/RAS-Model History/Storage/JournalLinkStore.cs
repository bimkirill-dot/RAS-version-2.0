using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>Журнал в общей папке: ключ (папка), название модели, путь из model.json, кто писал и когда.</summary>
    public sealed class JournalInfo
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Path { get; set; }
        public JournalSummary Summary { get; set; }
        public string Period => JournalLinks.Period(Summary);
        public string Users => Summary == null ? "" : string.Join(", ", Summary.Users);
    }

    /// <summary>
    /// Связи журналов в общей папке (DECISIONS.md модуля, «Связь журналов»): links.json в папке журнала модели, поиск журнала
    /// прежней истории по тому же месту на Revit Server. Правила — Tracking\JournalLinks (с тестами); здесь — файлы. Сетевая
    /// папка бывает медленной: вызывать не в потоке интерфейса; список папок — одним запросом, model.json — только у журналов
    /// с тем же именем модели.
    /// </summary>
    internal static class JournalLinkStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static string DirOf(string key) => Path.Combine(DataFolders.LogFolder, key);

        public static JournalLinksFile Read(string key)
        {
            try
            {
                var path = Path.Combine(DirOf(key), JournalLinks.FileName);
                return File.Exists(path) ? JournalLinks.Parse(File.ReadAllText(path, Utf8)) : new JournalLinksFile();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Diag.Log("Journal links read failed: " + key, ex);
                return new JournalLinksFile();
            }
        }

        /// <summary>Записать links.json (через временный файл: недописанный не выглядит целым).</summary>
        public static void Write(string key, JournalLinksFile file)
        {
            var dir = DirOf(key);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, JournalLinks.FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JournalLinks.Serialize(file), Utf8);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>Связанные журналы (со связями связанных), кроме самой модели и её прежних ключей.</summary>
        public static List<string> LinkedKeys(string key, IReadOnlyList<string> legacyKeys)
        {
            var own = new HashSet<string>(legacyKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase) { key };
            return JournalLinks.ReadOrder(key, legacyKeys, Read).Where(k => !own.Contains(k)).ToList();
        }

        /// <summary>Путь модели из model.json журнала; нет — пусто.</summary>
        public static string PathOf(string key)
        {
            try
            {
                var file = Path.Combine(DirOf(key), "model.json");
                if (!File.Exists(file)) return "";
                using (var doc = JsonDocument.Parse(File.ReadAllText(file, Utf8)))
                    return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return "";
            }
        }

        public static JournalInfo Info(string key)
        {
            JournalSummary summary;
            try { summary = JournalLinks.Summarize(Directory.GetFiles(DirOf(key), "*.jsonl").Select(Path.GetFileName)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { summary = new JournalSummary(); }
            return new JournalInfo { Key = key, Title = JournalLinks.NameOf(key), Path = PathOf(key), Summary = summary };
        }

        /// <summary>
        /// Журналы прежней истории этой модели: то же имя модели в ключе (одним списком папок), то же место на Revit Server
        /// (model.json), ещё не читаются и не отклонены.
        /// </summary>
        public static List<JournalInfo> FindCandidates(string key, string modelPath, ICollection<string> reading)
        {
            if (string.IsNullOrEmpty(JournalLinks.Place(modelPath))) return new List<JournalInfo>();
            var name = JournalLinks.NameOf(key);
            string[] dirs;
            try { dirs = Directory.GetDirectories(DataFolders.LogFolder, name + "_*"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new List<JournalInfo>(); }
            var journals = new List<KeyValuePair<string, string>>();
            foreach (var dir in dirs)
            {
                var k = Path.GetFileName(dir);
                if (!string.Equals(JournalLinks.NameOf(k), name, StringComparison.OrdinalIgnoreCase)) continue; // «Дом_S1_…» для «Дом»
                if (reading.Contains(k)) continue;
                journals.Add(new KeyValuePair<string, string>(k, PathOf(k)));
            }
            return JournalLinks.Candidates(modelPath, journals, reading, Read(key)).Select(Info).ToList();
        }

        /// <summary>Все журналы общей папки — для окна «Связанные журналы» (только имена папок: быстро и на медленной сети).</summary>
        public static List<string> AllKeys()
        {
            try
            {
                return Directory.GetDirectories(DataFolders.LogFolder).Select(Path.GetFileName)
                    .Where(k => !string.IsNullOrEmpty(k) && k.Contains('_'))
                    .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Diag.Log("Journal list failed", ex);
                return new List<string>();
            }
        }
    }
}
