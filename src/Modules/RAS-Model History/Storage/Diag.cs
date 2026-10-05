using System;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Журнал ошибок модуля — общий журнал ядра (%LocalAppData%\RAS_v_2.0\Logs\ras.log) с меткой модуля:
    /// у плагина один журнал ошибок (PRINCIPLES.md, раздел 7).
    /// </summary>
    public static class Diag
    {
        public const string Tag = "ModelHistory";

        public static string LogPath => RASv2.Core.Log.FilePath;

        public static void Log(string message, Exception ex = null) => RASv2.Core.Log.Write(Tag, message, ex);
    }
}
