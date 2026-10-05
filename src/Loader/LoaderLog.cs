using System;
using System.IO;

namespace RASv2.Loader
{
    /// <summary>Журнал загрузчика: %LocalAppData%\RAS_v_2.0\Logs\loader.log (ядро ещё не запущено — пишем сами).</summary>
    internal static class LoaderLog
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Sync = new object();

        public static string FilePath => Path.Combine(RasPaths.Logs, "loader.log");

        public static void Write(string message) => Write(message, null);

        public static void Write(string message, Exception ex)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(RasPaths.Logs);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        var old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    var text = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " " + message +
                               (ex == null ? "" : Environment.NewLine + ex) + Environment.NewLine;
                    File.AppendAllText(FilePath, text);
                }
            }
            catch (Exception)
            {
                // журнал не записался — запуск Revit важнее
            }
        }
    }
}
