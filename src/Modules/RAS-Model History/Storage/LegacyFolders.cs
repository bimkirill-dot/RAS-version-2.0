using System;
using System.IO;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Плагин назывался SpyPlugin. Личные папки прежнего названия (%AppData%\SpyPlugin, %LocalAppData%\SpyPlugin:
    /// вид панели, личные настройки, кэш снимков, неотправленные записи, время синхронизаций) переносятся в новые
    /// при первом запуске. Если новая папка уже есть (её создал установщик), переносится то, чего в ней ещё нет.
    /// </summary>
    internal static class LegacyFolders
    {
        public const string OldName = "SpyPlugin";

        public static void Migrate()
        {
            Move(Environment.SpecialFolder.ApplicationData);
            Move(Environment.SpecialFolder.LocalApplicationData);
        }

        private static void Move(Environment.SpecialFolder root)
        {
            try
            {
                var baseDir = Environment.GetFolderPath(root);
                if (string.IsNullOrEmpty(baseDir)) return;
                var oldDir = Path.Combine(baseDir, OldName);
                var newDir = Path.Combine(baseDir, SpySettings.FolderName);
                if (!Directory.Exists(oldDir)) return;
                if (!Directory.Exists(newDir))
                {
                    Directory.Move(oldDir, newDir);
                    return;
                }
                foreach (var dir in Directory.GetDirectories(oldDir))
                {
                    var target = Path.Combine(newDir, Path.GetFileName(dir));
                    try { if (!Directory.Exists(target)) Directory.Move(dir, target); }
                    catch { /* занято — останется в старой папке */ }
                }
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    var target = Path.Combine(newDir, Path.GetFileName(file));
                    try { if (!File.Exists(target)) File.Move(file, target); }
                    catch { /* занято — останется в старой папке */ }
                }
                try { Directory.Delete(oldDir, recursive: false); }
                catch { /* что-то осталось — не мешает */ }
            }
            catch (Exception ex)
            {
                Diag.Log("Legacy folder migration failed (" + root + ")", ex);
            }
        }
    }
}
