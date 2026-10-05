using System;
using System.IO;

namespace RASv2.Core
{
    /// <summary>
    /// Журнал ошибок плагина: %LocalAppData%\RAS_v_2.0\Logs\ras.log. Только на компьютере пользователя
    /// (PRINCIPLES.md, раздел 7); окно «О плагине» открывает его, чтобы файл можно было переслать.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Sync = new object();

        public static string FilePath => Path.Combine(RasPaths.Logs, "ras.log");

        public static void Write(string tag, string message, Exception ex = null)
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
                    var line = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " [" + tag + "] " + message +
                               " (сборка " + (Host.BuildId ?? "?") + ")" +
                               (ex == null ? "" : Environment.NewLine + ex) + Environment.NewLine;
                    File.AppendAllText(FilePath, line);
                }
            }
            catch (Exception)
            {
                // журнал не записался — работа пользователя важнее
            }
        }
    }

    /// <summary>Журнал с меткой модуля — его модуль получает из контекста.</summary>
    public sealed class ModuleLog
    {
        private readonly string _tag;

        internal ModuleLog(string tag) => _tag = tag;

        public void Info(string message) => Log.Write(_tag, message);

        public void Error(string message, Exception ex) => Log.Write(_tag, message, ex);
    }
}
