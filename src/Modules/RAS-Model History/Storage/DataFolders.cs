using System;
using RASv2.Core;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Папки данных модуля — от ядра (PRINCIPLES.md, раздел 4): общие данные — Data\RAS-Model History на сервере (там же
    /// общие настройки), личное (вид панели, тема) — %AppData%\RAS_v_2.0\RAS-Model History, на компьютере (кэш снимков,
    /// неотправленные записи, время синхронизаций) — %LocalAppData%\RAS_v_2.0\RAS-Model History. Журналы команды — в
    /// Data\RAS-Model History или в папке из общих настроек (UseJournalFolder).
    /// Init — первым в App.Start, до любого обращения к папкам.
    /// </summary>
    internal static class DataFolders
    {
        /// <summary>
        /// Общая папка журналов команды — единственная: и запись, и чтение; на весь сеанс Revit. Прежнюю папку отдельного
        /// плагина (\\10.10.1.77\Shared\BIM\Полетаев Кирилл\RAS-Model History) модуль не читает — решение разработчика (MODULE.md).
        /// </summary>
        public static string LogFolder { get; private set; }

        /// <summary>Data\RAS-Model History на сервере плагина — место общих настроек и папка журналов по умолчанию.</summary>
        public static string SharedDir { get; private set; }

        public static string UserDataDir { get; private set; }

        public static string LocalDataDir { get; private set; }

        public static void Init(IModuleContext context)
        {
            SharedDir = context.SharedDataDir;
            LogFolder = SharedDir;
            UserDataDir = context.UserDataDir;
            LocalDataDir = context.LocalDataDir;
        }

        /// <summary>
        /// Папка журналов из общих настроек (пусто — по умолчанию); %…% в пути раскрываются. Вызывается один раз при запуске:
        /// сменили в настройках — новая папка с перезапуска Revit (у каждого).
        /// </summary>
        public static void UseJournalFolder(string configured) => LogFolder = Resolve(configured);

        public static string Resolve(string configured) =>
            string.IsNullOrWhiteSpace(configured) ? SharedDir : Environment.ExpandEnvironmentVariables(configured.Trim());
    }
}
