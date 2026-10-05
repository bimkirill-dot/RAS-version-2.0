using System;
using System.IO;

namespace RASv2
{
    /// <summary>
    /// Адреса плагина — одно место для загрузчика и ядра (файл подключён в оба проекта).
    /// Скрипт выпуска (tools\Release.ps1) берёт адрес сервера отсюда же.
    /// </summary>
    internal static class RasPaths
    {
        /// <summary>Папка на сервере: выпущенные сборки, общие настройки, общие данные модулей.</summary>
        public const string ServerRoot = @"\\10.10.1.77\Shared\BIM\00_Плагины\00_RAS\RAS_v_2.0";

        /// <summary>Имя папок плагина на компьютере пользователя.</summary>
        public const string FolderName = "RAS_v_2.0";

        public static string Releases => Path.Combine(ServerRoot, "Releases");
        public static string Config => Path.Combine(ServerRoot, "Config");
        public static string SharedData => Path.Combine(ServerRoot, "Data");

        /// <summary>%LocalAppData%\RAS_v_2.0 — копии сборок, кэш модулей, журналы.</summary>
        public static string Local => Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), FolderName);

        /// <summary>%AppData%\RAS_v_2.0 — личные настройки и вид окон.</summary>
        public static string User => Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), FolderName);

        public static string Logs => Path.Combine(Local, "Logs");

        private static string Folder(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) ? Path.GetTempPath() : path;
        }
    }
}
