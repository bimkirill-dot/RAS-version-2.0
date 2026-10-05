using System;
using System.IO;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Перенос личных папок с прежних мест при первом запуске (PRINCIPLES.md, раздел 8). Прежний отдельный плагин хранил их
    /// в %AppData%\RAS-Model History и %LocalAppData%\RAS-Model History, а ещё раньше, под названием SpyPlugin, —
    /// в %AppData%\SpyPlugin и %LocalAppData%\SpyPlugin. Там вид панели, личные настройки, кэш снимков, неотправленные
    /// записи и время синхронизаций. Теперь это папки модуля от ядра: %AppData%\RAS_v_2.0\RAS-Model History и
    /// %LocalAppData%\RAS_v_2.0\RAS-Model History.
    /// Ничего не теряется: что уже есть в новой папке, не перезаписывается (остаётся в прежней), а неотправленные записи
    /// с тем же именем файла переносятся под другим именем и досылаются (LogStore.DeliverSpool).
    /// </summary>
    internal static class LegacyFolders
    {
        /// <summary>Прежние имена — от новых к старым: более новые данные переносятся первыми и не заменяются старыми.</summary>
        private static readonly string[] OldNames = { "RAS-Model History", "SpyPlugin" };

        private const string SpoolName = "Spool";

        /// <summary>Перенести прежние папки. Вызывается первым в App.Start — до чтения настроек и вида панели.</summary>
        public static void Migrate(string userDataDir, string localDataDir)
        {
            foreach (var name in OldNames)
            {
                Merge(Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), name), userDataDir);
                Merge(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), name), localDataDir);
            }
        }

        private static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        /// <summary>
        /// Перенести содержимое oldDir в newDir. Новой папки нет — прежняя переносится целиком; не вышло (файл занят,
        /// другой диск) или новая уже есть — по одному файлу. Пустая прежняя папка удаляется.
        /// </summary>
        public static void Merge(string oldDir, string newDir)
        {
            try
            {
                if (string.IsNullOrEmpty(oldDir) || !Directory.Exists(oldDir)) return;
                if (!Directory.Exists(newDir))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newDir));
                    try
                    {
                        Directory.Move(oldDir, newDir);
                        Diag.Log("Перенесена прежняя папка: " + oldDir + " → " + newDir);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // целиком не переносится — переносим по одному файлу ниже
                    }
                }
                MergeInto(oldDir, newDir, inSpool: false);
                Diag.Log("Прежняя папка перенесена по файлам: " + oldDir + " → " + newDir);
            }
            catch (Exception ex)
            {
                Diag.Log("Legacy folder migration failed: " + oldDir, ex);
            }
        }

        private static void MergeInto(string oldDir, string newDir, bool inSpool)
        {
            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.GetFiles(oldDir))
            {
                var name = Path.GetFileName(file);
                var target = Path.Combine(newDir, name);
                if (File.Exists(target))
                {
                    // неотправленные записи с тем же именем — отдельным файлом «к досылке», остальное остаётся в прежней папке
                    if (!inSpool || !IsSpoolRecords(name)) continue;
                    target = Path.Combine(newDir, SpoolBaseName(name) + ".legacy" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sending");
                }
                try { File.Move(file, target); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Diag.Log("Файл занят, остался в прежней папке: " + file, ex);
                }
            }
            foreach (var dir in Directory.GetDirectories(oldDir))
            {
                var name = Path.GetFileName(dir);
                MergeInto(dir, Path.Combine(newDir, name), inSpool || string.Equals(name, SpoolName, StringComparison.OrdinalIgnoreCase));
            }
            // что-то осталось (занято или уже есть в новой папке) — прежняя папка остаётся вместе с ним, это не мешает
            try { Directory.Delete(oldDir, recursive: false); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Файлы записей в буфере: «…_2026-10.jsonl» и недосланные «…_2026-10.jsonl.&lt;id&gt;.sending».</summary>
        private static bool IsSpoolRecords(string name) =>
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sending", StringComparison.OrdinalIgnoreCase);

        /// <summary>Имя файла журнала, в который досылаются записи: «…_2026-10.jsonl».</summary>
        private static string SpoolBaseName(string name)
        {
            var i = name.IndexOf(".jsonl", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? name.Substring(0, i + 6) : name;
        }
    }
}
