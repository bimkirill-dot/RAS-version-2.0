using System;

namespace RASv2.Core
{
    /// <summary>
    /// «Что нового» (разработчик, 23.10.2026: «когда я выпускаю новую версию для всех, пусть Revit при запуске единожды выводит
    /// окно с этим текстом, чтобы люди тоже видели»; src\Core\DECISIONS.md, «Что нового — окном при запуске»). Текст — тот, что
    /// введён при выпуске (revit.cmd): notes.txt в папке сборки (загрузчик копирует сборку целиком). Показано — номер сборки запоминается
    /// у человека (%AppData%\RAS_v_2.0\whats-new.txt), один на все версии Revit. Без Revit API — правила проверяются тестами.
    /// </summary>
    public static class WhatsNew
    {
        public const string NotesFile = "notes.txt";
        public const string SeenFile = "whats-new.txt";

        /// <summary>
        /// Показать ли окно: есть текст, сборка не разработчика (revit.cmd build — канал «разработка», без текста), и эту сборку человек
        /// ещё не видел.
        /// </summary>
        public static bool ShouldShow(string buildId, string channel, string notes, string lastSeen)
        {
            if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(notes)) return false;
            if (string.Equals(channel, "dev", StringComparison.OrdinalIgnoreCase)) return false;
            return !string.Equals((lastSeen ?? "").Trim(), buildId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Дата выпуска из номера сборки «2026.10.23-090930» → «23.10.2026»; не разобрать — пусто.</summary>
        public static string DateOf(string buildId)
        {
            if (string.IsNullOrEmpty(buildId) || buildId.Length < 10) return "";
            var d = buildId.Substring(0, 10).Split('.');
            return d.Length == 3 ? d[2] + "." + d[1] + "." + d[0] : "";
        }
    }
}
