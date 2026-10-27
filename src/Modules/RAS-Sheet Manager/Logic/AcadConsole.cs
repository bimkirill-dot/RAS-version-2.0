using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RASv2.SheetManager.Logic
{
    /// <summary>
    /// AutoCAD Core Console (accoreconsole.exe — AutoCAD без окна): DWG ↔ DXF при очистке подписей (решение разработчика,
    /// 27.10.2026: без сторонних библиотек, как в dwg_clean.py). Сценарий — файл .scr: пути в нём — только латиницей и без
    /// пробелов (Core Console читает его не в UTF-8, а пробел в сценарии — это Enter: путь «RAS-Sheet Manager\…» разрывался,
    /// и AutoCAD ждал ввода вечно — 27.10.2026). Пути с кириллицей и пробелами — только в командной строке (/i).
    /// Не ответил за минуту или окно закрыли — процесс снимается.
    /// </summary>
    public static class AcadConsole
    {
        public const string ExeName = "accoreconsole.exe";
        /// <summary>Подпись AutoCAD переводит за секунды; минута — с запасом на большой исходный DWG.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

        /// <summary>Путь из настроек, если файл есть; иначе — самый новый AutoCAD в Program Files; нет — null.</summary>
        public static string Find(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim())) return configured.Trim();
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
                return Directory.Exists(root)
                    ? Directory.GetDirectories(root, "AutoCAD*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, ExeName)).FirstOrDefault(File.Exists)
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>DWG → DXF (как есть, точность 16 знаков). null — AutoCAD не сохранил DXF.</summary>
        public static string ToDxf(string exe, string dwg, string workDir, string tag, CancellationToken cancel = default)
        {
            var dxf = Path.Combine(workDir, tag + ".dxf");
            if (File.Exists(dxf)) File.Delete(dxf);
            Run(exe, dwg, new[] { "FILEDIA", "0", "_.DXFOUT", Slash(dxf), "16", "_.QUIT", "_Y" }, workDir, tag, cancel);
            return File.Exists(dxf) ? dxf : null;
        }

        /// <summary>
        /// Чистый DXF → DWG (формат 2018): полилинии — лёгкие (CONVERTPOLY), единицы — мм, лишнее вычищено (PURGE).
        /// false — AutoCAD не сохранил DWG.
        /// </summary>
        public static bool ToDwg(string exe, string dxf, string dwg, string workDir, string tag, CancellationToken cancel = default)
        {
            var temp = Path.Combine(workDir, tag + ".dwg");
            if (File.Exists(temp)) File.Delete(temp);
            Run(exe, dxf, new[]
            {
                "FILEDIA", "0",
                "_.CONVERTPOLY", "_L", "_ALL", "",
                "INSUNITS", "4",
                "_.-PURGE", "_ALL", "*", "_N",
                "_.-PURGE", "_REGAPPS", "*", "_N",
                "_.SAVEAS", "2018", Slash(temp),
                "_.QUIT", "_Y",
            }, workDir, tag, cancel);
            if (!File.Exists(temp)) return false;
            if (File.Exists(dwg)) File.Delete(dwg);
            File.Move(temp, dwg);
            return true;
        }

        private static string Slash(string path)
        {
            if (!SignFolder.ScriptSafe(path)) throw new InvalidOperationException("путь для сценария AutoCAD — только латиницей и без пробелов: " + path);
            return path.Replace('\\', '/');
        }

        private static void Run(string exe, string input, IEnumerable<string> script, string workDir, string tag, CancellationToken cancel)
        {
            var scr = Path.Combine(workDir, tag + ".scr");
            File.WriteAllText(scr, string.Join("\r\n", script) + "\r\n", Encoding.ASCII);
            var info = new ProcessStartInfo(exe, "/i \"" + input + "\" /s \"" + scr + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            using (var process = Process.Start(info))
            {
                if (process == null) return;
                var until = DateTime.UtcNow + Timeout;
                while (!process.WaitForExit(250))
                {
                    if (!cancel.IsCancellationRequested && DateTime.UtcNow < until) continue;
                    try { process.Kill(); } catch (InvalidOperationException) { /* уже завершился */ }
                    break;
                }
            }
        }
    }
}
