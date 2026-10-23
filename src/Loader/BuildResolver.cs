using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RASv2.Loader
{
    /// <summary>Откуда и для кого брать сборку. Заполняет LoaderConfig, тесты — свои значения.</summary>
    public sealed class LoaderSettings
    {
        /// <summary>Папка выпусков: Releases на сервере или своя папка разработчика (loader.ini).</summary>
        public string ReleasesRoot;
        /// <summary>Список тестовой группы (имена пользователей Windows по одному в строке).</summary>
        public string TestersFile;
        /// <summary>Канал задан явно (loader.ini); null — «тест» для тестовой группы, иначе «все».</summary>
        public string Channel;
        /// <summary>Локальные копии сборок этой версии Revit: %LocalAppData%\RAS_v_2.0\Builds\&lt;год&gt;.</summary>
        public string CacheRoot;
        public int Year;
        public string UserName;
        /// <summary>Сколько ждать сервер при запуске Revit; не успели — запускается прежняя сборка.</summary>
        public int TimeoutMs = 15000;
    }

    /// <summary>Что запускать.</summary>
    public sealed class BuildChoice
    {
        public string BuildId;
        /// <summary>Локальная копия сборки; null — запускать нечего.</summary>
        public string Dir;
        public string Channel;
        /// <summary>Почему запущена не сборка канала (сервер недоступен и т. п.); null — всё штатно.</summary>
        public string Note;
    }

    /// <summary>
    /// Выбор сборки при запуске Revit (PRINCIPLES.md, раздел 10). Без Revit API — проверяется тестами.
    /// Указатель канала (Releases\&lt;год&gt;\&lt;канал&gt;.txt) называет сборку; она копируется в локальную папку
    /// и запускается оттуда — файлы на сервере Revit не держит, выпускать можно в любой момент.
    /// Выпущенная сборка не меняется, поэтому готовая локальная копия больше не сверяется с сервером.
    /// </summary>
    public static class BuildResolver
    {
#if NAVISWORKS
        // загрузчик для Navisworks (src\Navisworks\Loader) — тот же выбор сборки, другая главная сборка
        public const string EntryAssembly = "RASv2.ClashAnalysis.Navisworks.dll";
#else
        public const string EntryAssembly = "RASv2.Core.dll";
#endif
        /// <summary>Копия скопирована целиком (метка ставится до переименования временной папки).</summary>
        public const string ReadyMark = ".ready";
        /// <summary>Сборка не запустилась на этом компьютере — больше её не берём.</summary>
        public const string BadMark = ".bad";
        /// <summary>Последняя сборка, которая запустилась, — запасной вариант без сервера.</summary>
        public const string LastGoodFile = "last.txt";
        public const string TestChannel = "test";
        public const string AllChannel = "all";

        public static BuildChoice Resolve(LoaderSettings s, Action<string> log)
        {
            Directory.CreateDirectory(s.CacheRoot);
            // сервер может «висеть» десятки секунд — ждём не дольше TimeoutMs; копирование при этом не прерывается
            // и закончится в фоне, а сборка подхватится при следующем запуске
            var task = Task.Run(() => FetchFromSource(s, log));
            var fetch = task.Wait(s.TimeoutMs)
                ? task.Result
                : new Fetch { Channel = s.Channel, Error = "сервер не ответил за " + s.TimeoutMs / 1000 + " с" };

            if (fetch.Error == null && fetch.BuildId != null)
            {
                var dir = Path.Combine(s.CacheRoot, fetch.BuildId);
                if (IsUsable(dir)) return new BuildChoice { BuildId = fetch.BuildId, Dir = dir, Channel = fetch.Channel };
                fetch.Error = "копия сборки " + fetch.BuildId + " не готова";
            }

            var fallback = Fallback(s.CacheRoot);
            if (fallback == null) return new BuildChoice { Channel = fetch.Channel, Note = fetch.Error ?? "нет сборки" };
            return new BuildChoice
            {
                BuildId = Path.GetFileName(fallback),
                Dir = fallback,
                Channel = fetch.Channel,
                Note = (fetch.Error ?? "сборка канала недоступна") + "; запущена прежняя сборка"
            };
        }

        private sealed class Fetch
        {
            public string Channel;
            public string BuildId;
            public string Error;
        }

        private static Fetch FetchFromSource(LoaderSettings s, Action<string> log)
        {
            var f = new Fetch { Channel = s.Channel };
            try
            {
                var channel = string.IsNullOrEmpty(s.Channel) ? (IsTester(s) ? TestChannel : AllChannel) : s.Channel;
                var yearDir = Path.Combine(s.ReleasesRoot, s.Year.ToString());
                var id = ReadPointer(yearDir, channel);
                // у тестовой группы своей сборки может не быть — тогда та же, что у всех
                if (id == null && channel == TestChannel && string.IsNullOrEmpty(s.Channel))
                {
                    channel = AllChannel;
                    id = ReadPointer(yearDir, channel);
                }
                f.Channel = channel;
                if (id == null)
                {
                    f.Error = "нет выпущенной сборки для канала «" + channel + "»";
                    return f;
                }
                f.BuildId = id;
                var local = Path.Combine(s.CacheRoot, id);
                if (IsBad(local))
                {
                    f.Error = "сборка " + id + " не запустилась на этом компьютере";
                    return f;
                }
                if (!IsReady(local))
                {
                    log?.Invoke("Копирую сборку " + id + " с " + yearDir);
                    CopyBuild(Path.Combine(yearDir, id), local);
                }
            }
            catch (Exception ex)
            {
                f.Error = ex.Message;
            }
            return f;
        }

        private static bool IsTester(LoaderSettings s)
        {
            if (string.IsNullOrEmpty(s.TestersFile) || !File.Exists(s.TestersFile)) return false;
            return File.ReadAllLines(s.TestersFile)
                .Select(l => l.Trim())
                .Any(l => l.Length > 0 && !l.StartsWith("#") && string.Equals(l, s.UserName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Имя сборки из указателя канала; null — указателя нет или в нём не имя папки.</summary>
        public static string ReadPointer(string yearDir, string channel)
        {
            var file = Path.Combine(yearDir, channel + ".txt");
            if (!File.Exists(file)) return null;
            var id = File.ReadAllLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.StartsWith(".")) return null;
            return id;
        }

        /// <summary>Скопировать сборку во временную папку и переименовать: недокопированная копия не бывает «готовой».</summary>
        private static void CopyBuild(string source, string target)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("на сервере нет папки сборки: " + source);
            var temp = target + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            CopyDirectory(source, temp);
            if (!File.Exists(Path.Combine(temp, EntryAssembly))) throw new FileNotFoundException("в сборке нет " + EntryAssembly);
            File.WriteAllText(Path.Combine(temp, ReadyMark), DateTime.Now.ToString("O"));
            if (Directory.Exists(target))
            {
                // другой Revit скопировал её одновременно с нами
                TryDelete(temp);
                return;
            }
            Directory.Move(temp, target);
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        public static bool IsReady(string dir) =>
            File.Exists(Path.Combine(dir, ReadyMark)) && File.Exists(Path.Combine(dir, EntryAssembly));

        public static bool IsBad(string dir) => File.Exists(Path.Combine(dir, BadMark));

        private static bool IsUsable(string dir) => IsReady(dir) && !IsBad(dir);

        /// <summary>Без сервера: последняя запустившаяся сборка, иначе самая новая готовая.</summary>
        private static string Fallback(string cacheRoot)
        {
            var lastFile = Path.Combine(cacheRoot, LastGoodFile);
            if (File.Exists(lastFile))
            {
                var last = File.ReadAllText(lastFile).Trim();
                if (last.Length > 0 && IsUsable(Path.Combine(cacheRoot, last))) return Path.Combine(cacheRoot, last);
            }
            return Builds(cacheRoot).FirstOrDefault(IsUsable);
        }

        /// <summary>Локальные копии, новые первыми (имя сборки — «год.месяц.день-часыминутысекунды»).</summary>
        private static IEnumerable<string> Builds(string cacheRoot) =>
            Directory.Exists(cacheRoot)
                ? Directory.GetDirectories(cacheRoot).Where(d => !Path.GetFileName(d).Contains(".tmp-"))
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        public static void MarkGood(string cacheRoot, string buildId) =>
            File.WriteAllText(Path.Combine(cacheRoot, LastGoodFile), buildId);

        public static void MarkBad(string dir) =>
            File.WriteAllText(Path.Combine(dir, BadMark), DateTime.Now.ToString("O"));

        /// <summary>
        /// Убрать старые копии: остаются текущая, последняя запустившаяся и ещё несколько новых.
        /// Копия, которую держит другой открытый Revit, не удалится — и не нужно.
        /// </summary>
        public static void Cleanup(string cacheRoot, string current, int keep = 3)
        {
            var lastFile = Path.Combine(cacheRoot, LastGoodFile);
            var last = File.Exists(lastFile) ? File.ReadAllText(lastFile).Trim() : null;
            var kept = 0;
            foreach (var dir in Builds(cacheRoot))
            {
                var id = Path.GetFileName(dir);
                if (id == current || id == last) continue;
                if (kept++ < keep) continue;
                TryDelete(dir);
            }
            // недокопированные временные папки старше суток
            if (!Directory.Exists(cacheRoot)) return;
            foreach (var temp in Directory.GetDirectories(cacheRoot, "*.tmp-*"))
                if (Directory.GetCreationTime(temp) < DateTime.Now.AddDays(-1)) TryDelete(temp);
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception) { /* занята открытым Revit — уберём в другой раз */ }
        }
    }
}
