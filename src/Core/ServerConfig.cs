using System;
using System.IO;
using System.Threading.Tasks;

namespace RASv2.Core
{
    /// <summary>
    /// Файлы общих настроек компании на сервере (Config) с локальной копией: %LocalAppData%\RAS_v_2.0\Config.
    /// Сервер недоступен — действует последняя загруженная копия: пропажа сети не должна ни включать выключенное,
    /// ни выключать всё. Сервер ответил, а файла нет — настроек нет, копия удаляется. Сервер может «висеть» десятки
    /// секунд — ждём не дольше таймаута, запуск Revit не задерживается.
    /// </summary>
    internal static class ServerConfig
    {
        private const int TimeoutMs = 3000;

        private enum Answer { Found, Absent, Unreachable }

        /// <summary>Строки файла; null — настроек нет. note — пояснение, если взята копия (для «О плагине»), иначе null.</summary>
        public static string[] ReadLines(string fileName, out string note)
        {
            note = null;
            var server = Path.Combine(RasPaths.Config, fileName);
            var cache = Path.Combine(RasPaths.Local, "Config", fileName);

            string[] lines = null;
            var answer = Answer.Unreachable;
            try
            {
                var task = Task.Run(() =>
                {
                    if (File.Exists(server)) return (Answer.Found, File.ReadAllLines(server));
                    return (Directory.Exists(RasPaths.ServerRoot) ? Answer.Absent : Answer.Unreachable, (string[])null);
                });
                if (task.Wait(TimeoutMs)) (answer, lines) = task.Result;
            }
            catch (Exception ex)
            {
                Log.Write("Ядро", "Не удалось прочитать " + server, ex);
            }

            switch (answer)
            {
                case Answer.Found:
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cache));
                        File.WriteAllLines(cache, lines);
                    }
                    catch (Exception ex) { Log.Write("Ядро", "Не удалось сохранить копию " + fileName, ex); }
                    return lines;
                case Answer.Absent:
                    try { if (File.Exists(cache)) File.Delete(cache); }
                    catch (Exception ex) { Log.Write("Ядро", "Не удалось удалить копию " + fileName, ex); }
                    return null;
                default:
                    if (!File.Exists(cache)) return null;
                    note = "сервер недоступен — действует последняя загруженная копия";
                    try { return File.ReadAllLines(cache); }
                    catch (Exception ex)
                    {
                        Log.Write("Ядро", "Не удалось прочитать копию " + cache, ex);
                        return null;
                    }
            }
        }
    }
}
