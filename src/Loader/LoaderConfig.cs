using System;
using System.IO;

namespace RASv2.Loader
{
    /// <summary>
    /// Настройки загрузчика: по умолчанию — сервер компании. На компьютере разработчика build.cmd пишет
    /// %AppData%\RAS_v_2.0\loader.ini (releases=…, channel=dev), и загрузчик берёт свежую сборку из папки
    /// исходников. Удалить loader.ini — снова сервер.
    /// </summary>
    internal static class LoaderConfig
    {
        public static string IniPath => Path.Combine(RasPaths.User, "loader.ini");

        public static LoaderSettings Load(int year)
        {
            var s = new LoaderSettings
            {
                ReleasesRoot = RasPaths.Releases,
                TestersFile = Path.Combine(RasPaths.Config, "test-users.txt"),
                CacheRoot = Path.Combine(RasPaths.Local, "Builds", year.ToString()),
                Year = year,
                UserName = Environment.UserName
            };
            if (!File.Exists(IniPath)) return s;
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;
                if (key == "releases") s.ReleasesRoot = value;
                else if (key == "channel") s.Channel = value;
            }
            return s;
        }
    }
}
