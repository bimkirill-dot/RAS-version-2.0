using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Локальное состояние пользователя: последняя известная версия каждой модели.
    /// Нужна, чтобы при следующем открытии показать, что изменили другие с прошлого сеанса.
    /// </summary>
    public static class LocalState
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, string> _versions;

        private static string PathName => Path.Combine(SpySettings.LocalDataDir, "state.json");

        public static string GetLastVersion(string modelKey)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return _versions.TryGetValue(modelKey, out var v) ? v : null;
            }
        }

        public static void SetLastVersion(string modelKey, string versionGuid)
        {
            if (string.IsNullOrEmpty(modelKey) || string.IsNullOrEmpty(versionGuid)) return;
            Set(modelKey, versionGuid);
        }

        /// <summary>В том же файле, с приставкой: когда этот пользователь последний раз синхронизировался (сохранил одиночный файл).</summary>
        private const string CommitPrefix = "commit|";

        /// <summary>Время последней синхронизации этого пользователя с моделью (null — неизвестно).</summary>
        public static DateTimeOffset? GetLastCommit(string modelKey)
        {
            var s = string.IsNullOrEmpty(modelKey) ? null : GetLastVersion(CommitPrefix + modelKey);
            return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time) ? time : (DateTimeOffset?)null;
        }

        public static void SetLastCommit(string modelKey, DateTimeOffset time)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            Set(CommitPrefix + modelKey, time.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void Set(string key, string value)
        {
            lock (Sync)
            {
                EnsureLoaded();
                _versions[key] = value;
                try
                {
                    Directory.CreateDirectory(SpySettings.LocalDataDir);
                    File.WriteAllText(PathName, JsonSerializer.Serialize(_versions, SpyJson.Pretty));
                }
                catch (Exception ex)
                {
                    Diag.Log("State save failed", ex);
                }
            }
        }

        // ── несинхронизированные изменения, оставшиеся в локальных копиях ──

        public sealed class PendingLocal
        {
            public string Version { get; set; }
            public List<string> Sessions { get; set; } = new List<string>();
            public string ModelKey { get; set; }
            public DateTime SavedUtc { get; set; }
        }

        private static Dictionary<string, PendingLocal> _pending;
        private static string PendingPath => Path.Combine(SpySettings.LocalDataDir, "pending.json");

        public static PendingLocal GetPending(string localPath)
        {
            if (string.IsNullOrEmpty(localPath)) return null;
            lock (Sync)
            {
                EnsurePendingLoaded();
                return _pending.TryGetValue(localPath.ToLowerInvariant(), out var p) ? p : null;
            }
        }

        public static void SetPending(string localPath, PendingLocal entry)
        {
            if (string.IsNullOrEmpty(localPath)) return;
            lock (Sync)
            {
                EnsurePendingLoaded();
                if (entry == null) _pending.Remove(localPath.ToLowerInvariant());
                else _pending[localPath.ToLowerInvariant()] = entry;
                // старые записи (локальные копии, не открывавшиеся полгода) не храним
                foreach (var k in _pending.Where(x => x.Value.SavedUtc < DateTime.UtcNow.AddDays(-180)).Select(x => x.Key).ToList())
                    _pending.Remove(k);
                try
                {
                    Directory.CreateDirectory(SpySettings.LocalDataDir);
                    File.WriteAllText(PendingPath, JsonSerializer.Serialize(_pending, SpyJson.Pretty));
                }
                catch (Exception ex)
                {
                    Diag.Log("Pending state save failed", ex);
                }
            }
        }

        private static void EnsurePendingLoaded()
        {
            if (_pending != null) return;
            try
            {
                if (File.Exists(PendingPath))
                    _pending = IgnoreCase(JsonSerializer.Deserialize<Dictionary<string, PendingLocal>>(File.ReadAllText(PendingPath)));
            }
            catch (Exception ex)
            {
                Diag.Log("Pending state load failed", ex);
            }
            _pending ??= new Dictionary<string, PendingLocal>(StringComparer.OrdinalIgnoreCase);
        }

        private static void EnsureLoaded()
        {
            if (_versions != null) return;
            try
            {
                if (File.Exists(PathName))
                    _versions = IgnoreCase(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathName)));
            }
            catch (Exception ex)
            {
                Diag.Log("State load failed", ex);
            }
            _versions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ключи — ключи моделей и пути к файлам: регистр не важен. Прочитанный из файла словарь по умолчанию его
        /// учитывает, и после перезапуска Revit модель, открытая по пути в другом регистре, не находилась бы.
        /// </summary>
        private static Dictionary<string, T> IgnoreCase<T>(Dictionary<string, T> loaded)
        {
            if (loaded == null) return null;
            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in loaded) result[kv.Key] = kv.Value; // одинаковые с точностью до регистра — остаётся последний
            return result;
        }
    }
}
