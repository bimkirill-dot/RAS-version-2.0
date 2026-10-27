using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RASv2.SheetManager.Logic
{
    /// <summary>Файл подписи в папке: исходный DWG и его очищенная копия «…_clean.dwg».</summary>
    public sealed class SignFile
    {
        public string Source;
        public string Clean;
        public long SourceBytes, CleanBytes;
        /// <summary>Ломаные подписи в мм (центр — 0,0) — для картинки в окне; null — не прочитать.</summary>
        public List<Polyline2> Lines;
        public double WidthMm, HeightMm;
        /// <summary>Очищена сейчас (а не была очищена раньше).</summary>
        public bool CleanedNow;
        /// <summary>Почему не очищена или не прочитана; null — всё хорошо.</summary>
        public string Error;
        public string Summary;
    }

    /// <summary>
    /// Папка подписей: исходные DWG и подпапка «clean» с очищенными «…_clean.dwg» (как у dwg_clean.py). Очищаются только
    /// новые — у которых очищенной копии ещё нет; очищенные не трогаются, только читаются для картинки (картинка — в кэше
    /// на компьютере, чтобы не запускать AutoCAD при каждом открытии окна). AutoCAD Core Console — несколько сразу.
    /// </summary>
    public static class SignFolder
    {
        public const string CleanDir = "clean";
        public const string Suffix = "_clean";

        /// <summary>Исходные подписи: *.dwg, *.dxf в папке, кроме уже очищенных.</summary>
        public static List<string> Sources(string folder) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder)
                    .Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
                : new List<string>();

        public static string CleanPathFor(string source) =>
            Path.Combine(Path.GetDirectoryName(source) ?? "", CleanDir, Path.GetFileNameWithoutExtension(source) + Suffix + ".dwg");

        /// <summary>
        /// Обработать папку: новые — очистить (DWG → DXF → чистка → DXF → DWG), остальные — прочитать для картинки.
        /// progress(сделано, всего). Ошибка одного файла — в его Error, остальные идут дальше.
        /// </summary>
        public static List<SignFile> Process(string folder, CleanOptions options, string acad, string workRoot, string cacheDir,
            int parallel, Action<int, int> progress, CancellationToken cancel)
        {
            var sources = Sources(folder);
            var files = sources.Select(s => new SignFile { Source = s, Clean = CleanPathFor(s) }).ToList();
            var done = 0;
            progress?.Invoke(0, files.Count);
            Directory.CreateDirectory(workRoot);
            var counter = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancel }, f =>
            {
                var work = Path.Combine(workRoot, "w" + Interlocked.Increment(ref counter).ToString("0000", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(work);
                try { One(f, options, acad, work, cacheDir, cancel); }
                catch (Exception ex) { f.Error = ex.Message; }
                finally
                {
                    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    progress?.Invoke(Interlocked.Increment(ref done), files.Count);
                }
            });
            return files;
        }

        private static void One(SignFile f, CleanOptions options, string acad, string work, string cacheDir, CancellationToken cancel)
        {
            f.SourceBytes = new FileInfo(f.Source).Length;
            if (!File.Exists(f.Clean))
            {
                if (acad == null) { f.Error = "не очищена: нет AutoCAD"; return; }
                var dxf = f.Source.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? f.Source : AcadConsole.ToDxf(acad, f.Source, work, "in", cancel);
                if (dxf == null) { f.Error = "AutoCAD не открыл файл"; return; }
                var sign = SignCleaner.Clean(DxfDocument.Load(dxf), options, out var reason);
                if (sign == null) { f.Error = reason; return; }
                var outDxf = Path.Combine(work, "out.dxf");
                File.WriteAllText(outDxf, DxfWriter.Write(sign), Encoding.ASCII);
                Directory.CreateDirectory(Path.GetDirectoryName(f.Clean) ?? "");
                if (!AcadConsole.ToDwg(acad, outDxf, f.Clean, work, "out", cancel)) { f.Error = "AutoCAD не сохранил очищенный DWG"; return; }
                f.CleanedNow = true;
                f.Lines = sign.Lines;
                f.WidthMm = sign.WidthMm;
                f.HeightMm = sign.HeightMm;
                f.Summary = sign.Summary;
                f.CleanBytes = new FileInfo(f.Clean).Length;
                SavePreview(cacheDir, f);
                return;
            }
            f.CleanBytes = new FileInfo(f.Clean).Length;
            if (LoadPreview(cacheDir, f)) return;
            if (acad == null) { f.Error = "картинки нет: нет AutoCAD"; return; }
            var cleanDxf = AcadConsole.ToDxf(acad, f.Clean, work, "clean", cancel);
            if (cleanDxf == null) { f.Error = "AutoCAD не открыл очищенный файл"; return; }
            var doc = DxfDocument.Load(cleanDxf);
            var geo = new SignGeometry(doc, 0.005 / doc.ToMillimetres);
            var k = doc.ToMillimetres;
            f.Lines = geo.Curves.Select(p => new Polyline2 { X = p.X.Select(x => x * k).ToList(), Y = p.Y.Select(y => y * k).ToList(), Closed = p.Closed }).ToList();
            var (minX, minY, maxX, maxY) = SignCleaner.Bounds(f.Lines);
            f.WidthMm = maxX - minX;
            f.HeightMm = maxY - minY;
            SavePreview(cacheDir, f);
        }

        // ── картинка в кэше: ключ — путь, размер и время очищенного файла ──

        private static string PreviewPath(string cacheDir, SignFile f)
        {
            var info = new FileInfo(f.Clean);
            var key = f.Clean.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            using (var sha = SHA1.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 20);
                return Path.Combine(cacheDir, hash + ".txt");
            }
        }

        /// <summary>Формат: строка «ширина высота», затем по строке на ломаную: «c x,y x,y …» (c — 1 замкнута, 0 нет).</summary>
        private static void SavePreview(string cacheDir, SignFile f)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append(f.WidthMm.ToString("0.###", inv)).Append(' ').Append(f.HeightMm.ToString("0.###", inv)).Append('\n');
                foreach (var p in f.Lines)
                {
                    sb.Append(p.Closed ? '1' : '0');
                    for (var i = 0; i < p.Count; i++) sb.Append(' ').Append(p.X[i].ToString("0.###", inv)).Append(',').Append(p.Y[i].ToString("0.###", inv));
                    sb.Append('\n');
                }
                File.WriteAllText(PreviewPath(cacheDir, f), sb.ToString());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* без кэша — в следующий раз прочитаем заново */ }
        }

        private static bool LoadPreview(string cacheDir, SignFile f)
        {
            try
            {
                var path = PreviewPath(cacheDir, f);
                if (!File.Exists(path)) return false;
                var inv = CultureInfo.InvariantCulture;
                var lines = File.ReadAllLines(path);
                var head = lines[0].Split(' ');
                f.WidthMm = double.Parse(head[0], inv);
                f.HeightMm = double.Parse(head[1], inv);
                f.Lines = new List<Polyline2>();
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    var parts = line.Split(' ');
                    var p = new Polyline2 { Closed = parts[0] == "1" };
                    foreach (var xy in parts.Skip(1))
                    {
                        var c = xy.Split(',');
                        p.X.Add(double.Parse(c[0], inv));
                        p.Y.Add(double.Parse(c[1], inv));
                    }
                    f.Lines.Add(p);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is IndexOutOfRangeException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Рабочая папка для AutoCAD: путь — только латиница и без пробелов (AcadConsole). Первая подходящая: предложенная, временная
        /// папка пользователя (…\Temp\RASv2_sign), общая (C:\ProgramData\RAS_v_2.0\work).
        /// </summary>
        public static string WorkRoot(string preferred)
        {
            var candidates = new[]
            {
                preferred,
                Path.Combine(Path.GetTempPath(), "RASv2_sign"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAS_v_2.0", "work"),
            };
            return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && ScriptSafe(c)) ?? candidates[2];
        }

        /// <summary>Путь можно писать в сценарий AutoCAD: только латиница, без пробелов.</summary>
        public static bool ScriptSafe(string path) => (path ?? "").All(ch => ch > ' ' && ch < 128);

        /// <summary>«1,2 МБ», «340 КБ».</summary>
        public static string Bytes(long b) =>
            b >= 1024 * 1024
                ? (b / 1024.0 / 1024.0).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " МБ"
                : Math.Max(1, (b + 512) / 1024).ToString(CultureInfo.InvariantCulture) + " КБ";
    }
}
