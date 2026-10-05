using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Идентификатор модели для журнала. Для модели с совместной работой — путь к файлу-хранилищу,
    /// поэтому все локальные копии пишут в одну папку журнала.
    /// </summary>
    public sealed class ModelIdentity
    {
        public string Key { get; private set; }
        /// <summary>
        /// Ключи, под которыми журнал этой модели писали прежние версии плагина (без текущего).
        /// Их подпапки тоже читаются — иначе история модели разделится.
        /// </summary>
        public IReadOnlyList<string> LegacyKeys { get; private set; } = Array.Empty<string>();
        public string Title { get; private set; }
        public string Path { get; private set; }
        public bool IsWorkshared { get; private set; }

        /// <summary>Ключ относится к этой модели: текущий или прежний.</summary>
        public bool HasKey(string key)
        {
            if (string.Equals(key, Key, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var k in LegacyKeys)
            {
                if (string.Equals(key, k, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static ModelIdentity From(Document doc)
        {
            string path = "";
            bool workshared = false;
            try { workshared = doc.IsWorkshared; } catch { /* ignore */ }

            if (workshared)
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch { /* ignore */ }
            }
            if (string.IsNullOrEmpty(path))
            {
                try { path = doc.PathName; } catch { /* ignore */ }
            }
            path = ToUniversalPath(path);

            // Revit Server: у каждой модели-хранилища есть собственный GUID — надёжнее пути
            Guid? centralGuid = null;
            if (workshared && path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var g = doc.WorksharingCentralGUID;
                    if (g != Guid.Empty) centralGuid = g;
                }
                catch { /* модель старше Revit 2013 или не серверная — остаётся путь */ }
            }

            string title;
            try { title = doc.Title; } catch { title = "Модель"; }

            string name = !string.IsNullOrEmpty(path) ? GetFileName(path) : title;
            var key = KeyFor(path, title, centralGuid);

            return new ModelIdentity
            {
                Key = key,
                LegacyKeys = LegacyKeysFor(path, title, key),
                Title = string.IsNullOrEmpty(name) ? title : name,
                Path = path,
                IsWorkshared = workshared
            };
        }

        /// <summary>
        /// Ключ (имя подпапки журнала) = имя модели + короткий хэш того, что однозначно её определяет:
        /// для Revit Server — GUID модели-хранилища, для остальных — полного пути к файлу.
        /// Поэтому разные файлы с одинаковым именем никогда не смешиваются.
        /// </summary>
        public static string KeyFor(string path, string title, Guid? centralGuid = null)
        {
            string name = !string.IsNullOrEmpty(path) ? GetFileName(path) : title;
            var basis = centralGuid.HasValue && centralGuid.Value != Guid.Empty
                ? "rsn-guid:" + centralGuid.Value.ToString("N")
                : KeyBasis(path, title);
            return Sanitize(name) + "_" + ShortHash(basis);
        }

        /// <summary>
        /// Что хэшируется, если GUID модели-хранилища недоступен. Для Revit Server (RSN://сервер/папка/модель.rvt)
        /// имя сервера отбрасывается: в разных офисах одна и та же модель открывается через разные Host-серверы
        /// (или по имени и по IP), а журнал должен быть общим.
        /// </summary>
        public static string KeyBasis(string path, string title)
        {
            if (string.IsNullOrEmpty(path)) return "unsaved:" + title;
            var p = path.Trim().Replace('\\', '/');
            const string rsn = "rsn://";
            if (p.StartsWith(rsn, StringComparison.OrdinalIgnoreCase))
            {
                var rest = p.Substring(rsn.Length);
                var slash = rest.IndexOf('/');
                var modelPath = slash >= 0 ? rest.Substring(slash + 1) : rest;
                return "rsn:" + modelPath.Trim('/').ToLowerInvariant();
            }
            return path.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Прежние ключи модели. До версии с GUID модели-хранилища ключ модели на Revit Server считался
        /// по полному пути вместе с именем сервера. Кроме того, ключ по пути без имени сервера получает
        /// тот, у кого GUID хранилища не прочитался. Для остальных моделей ключ не менялся.
        /// </summary>
        public static List<string> LegacyKeysFor(string path, string title, string currentKey)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(path)) return result;
            var name = Sanitize(GetFileName(path));
            foreach (var key in new[] { name + "_" + ShortHash(path.Trim().ToLowerInvariant()), KeyFor(path, title) })
            {
                if (string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Contains(key, StringComparer.OrdinalIgnoreCase)) result.Add(key);
            }
            return result;
        }

        private static string GetFileName(string path)
        {
            try
            {
                var p = path.Replace('/', '\\');
                var i = p.LastIndexOf('\\');
                var file = i >= 0 ? p.Substring(i + 1) : p;
                var dot = file.LastIndexOf('.');
                return dot > 0 ? file.Substring(0, dot) : file;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Z:\Проекты\... → \\server\share\Проекты\... — чтобы у пользователей с разными
        /// буквами сетевых дисков журнал модели попадал в одну папку.
        /// </summary>
        public static string ToUniversalPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':' || !char.IsLetter(path[0])) return path;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 2048;
                buffer = Marshal.AllocHGlobal(size);
                int rc = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref size);
                if (rc == ErrorMoreData)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(size);
                    rc = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref size);
                }
                if (rc != 0) return path; // локальный диск или ошибка — оставляем как есть
                var unc = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));
                return string.IsNullOrEmpty(unc) ? path : unc;
            }
            catch
            {
                return path;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private const int UniversalNameInfoLevel = 1;
        private const int ErrorMoreData = 234;

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetUniversalNameW")]
        private static extern int WNetGetUniversalName(string lpLocalPath, int dwInfoLevel, IntPtr lpBuffer, ref int lpBufferSize);

        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "model";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.Trim())
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 || ch == ' ' || ch == '@' ? '_' : ch);
            }
            var r = sb.ToString();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        private static string ShortHash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public override bool Equals(object obj) => obj is ModelIdentity m && m.Key == Key;
        public override int GetHashCode() => Key?.GetHashCode() ?? 0;
    }
}
