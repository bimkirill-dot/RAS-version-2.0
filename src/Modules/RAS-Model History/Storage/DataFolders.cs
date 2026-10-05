using RASv2.Core;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Папки данных модуля — от ядра (PRINCIPLES.md, раздел 4): журналы команды — Data\RAS-Model History на сервере,
    /// личное (вид панели, личные настройки) — %AppData%\RAS_v_2.0\RAS-Model History, на компьютере (кэш снимков,
    /// неотправленные записи, время синхронизаций) — %LocalAppData%\RAS_v_2.0\RAS-Model History.
    /// Init — первым в App.Start, до любого обращения к папкам.
    /// </summary>
    internal static class DataFolders
    {
        /// <summary>
        /// Общая папка журналов команды — единственная: и запись, и чтение. Прежнюю папку отдельного плагина
        /// (\\10.10.1.77\Shared\BIM\Полетаев Кирилл\RAS-Model History) модуль не читает — решение разработчика (MODULE.md).
        /// </summary>
        public static string LogFolder { get; private set; }

        public static string UserDataDir { get; private set; }

        public static string LocalDataDir { get; private set; }

        public static void Init(IModuleContext context)
        {
            LogFolder = context.SharedDataDir;
            UserDataDir = context.UserDataDir;
            LocalDataDir = context.LocalDataDir;
        }
    }
}
