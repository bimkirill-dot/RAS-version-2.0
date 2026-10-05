using System.Collections.Generic;
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
        /// Папка журналов прежнего отдельного плагина. Здесь история команды до RAS v. 2.0 и записи тех, кто ещё работает
        /// в прежнем плагине. Модуль её только читает (LogFoldersToRead); пишет — в LogFolder.
        /// </summary>
        public const string LegacyLogFolder = @"\\10.10.1.77\Shared\BIM\Полетаев Кирилл\RAS-Model History";

        /// <summary>Общая папка журналов команды: сюда пишутся новые записи.</summary>
        public static string LogFolder { get; private set; }

        public static string UserDataDir { get; private set; }

        public static string LocalDataDir { get; private set; }

        /// <summary>Откуда читать журналы: новая папка и прежняя. Одна и та же запись в обеих читается один раз (по номеру).</summary>
        public static IReadOnlyList<string> LogFoldersToRead => new[] { LogFolder, LegacyLogFolder };

        public static void Init(IModuleContext context)
        {
            LogFolder = context.SharedDataDir;
            UserDataDir = context.UserDataDir;
            LocalDataDir = context.LocalDataDir;
        }
    }
}
