using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Где лежат настройки (DECISIONS.md, «Настройки — у BIM, общие — на сервере», 27.10.2026):
    /// исходные — RasModelHistory.settings.json в сборке (рядом с DLL); общие для команды — settings.json в Data\RAS-Model History
    /// на сервере (место постоянное — от папки журналов не зависит), их копия — на компьютере (сервер недоступен — работаем
    /// по последним известным); личная тема — settings.json в личной папке модуля.
    /// </summary>
    internal static class SettingsFiles
    {
        public const string TeamFileName = "settings.json";

        /// <summary>Сколько ждать сервер: недоступный сетевой путь иначе держит Revit десятки секунд.</summary>
        private static readonly TimeSpan ServerWait = TimeSpan.FromSeconds(5);

        public static string BuiltPath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "RasModelHistory.settings.json");

        public static string TeamPath => Path.Combine(DataFolders.SharedDir, TeamFileName);

        private static string CachePath => Path.Combine(DataFolders.LocalDataDir, "team-settings.json");

        private static string PersonalPath => Path.Combine(DataFolders.UserDataDir, "settings.json");

        /// <summary>Общие настройки в последнем Load взяты с сервера (иначе — копия на компьютере или исходные из сборки).</summary>
        public static bool FromServer { get; private set; }

        /// <summary>Сервер в последнем Load не ответил.</summary>
        public static bool ServerUnavailable { get; private set; }

        public static SpySettings Load()
        {
            var built = ReadLocal(BuiltPath);
            string team = null;
            FromServer = false;
            ServerUnavailable = false;
            var server = OnServer(() => File.Exists(TeamPath) ? File.ReadAllText(TeamPath) : "");
            if (server == null)
            {
                ServerUnavailable = true;
                team = ReadLocal(CachePath); // сервер не ответил — последние известные
            }
            else if (server.Length > 0)
            {
                if (SpySettings.IsValid(server))
                {
                    team = server;
                    FromServer = true;
                    WriteLocal(CachePath, server);
                }
                else
                {
                    Diag.Log("Общие настройки на сервере не разбираются — взяты последние известные: " + TeamPath);
                    team = ReadLocal(CachePath);
                }
            }
            // файла на сервере нет — общие настройки ещё не сохраняли: исходные из сборки
            return SpySettings.Compose(built, team, ReadLocal(PersonalPath));
        }

        /// <summary>Записать общие настройки на сервер (сначала во временный файл, потом замена). Не вышло — исключение.</summary>
        public static void SaveTeam(SpySettings s)
        {
            var json = s.ToTeamJson();
            var ok = OnServer(() =>
            {
                Directory.CreateDirectory(DataFolders.SharedDir);
                var temp = TeamPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, TeamPath, overwrite: true);
                return "";
            }, rethrow: true);
            if (ok == null) throw new IOException("сервер не ответил за " + ServerWait.TotalSeconds + " с");
            WriteLocal(CachePath, json);
        }

        public static void SavePersonal(SpySettings s)
        {
            Directory.CreateDirectory(DataFolders.UserDataDir);
            File.WriteAllText(PersonalPath, s.ToPersonalJson());
        }

        /// <summary>Можно ли вести журналы в папке: есть (или создаётся) и туда пишется. null — можно, иначе — почему нет.</summary>
        public static string CheckFolder(string folder)
        {
            try
            {
                var ok = OnServer(() =>
                {
                    Directory.CreateDirectory(folder);
                    var probe = Path.Combine(folder, ".ras-проверка-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return "";
                }, rethrow: true);
                return ok == null ? "папка не ответила за " + ServerWait.TotalSeconds + " с" : null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Действие с сетевой папкой с ограничением по времени: не ответила — null.</summary>
        private static string OnServer(Func<string> action, bool rethrow = false)
        {
            var task = Task.Run(action);
            try
            {
                return task.Wait(ServerWait) ? task.Result : null;
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException ?? ex;
                if (rethrow) throw inner;
                Diag.Log("Общие настройки: сервер недоступен (" + TeamPath + ")", inner);
                return null;
            }
        }

        private static string ReadLocal(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex)
            {
                Diag.Log("Настройки не прочитаны: " + path, ex);
                return null;
            }
        }

        private static void WriteLocal(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
                File.WriteAllText(path, text);
            }
            catch (Exception ex) { Diag.Log("Копия общих настроек не записана: " + path, ex); }
        }
    }
}
