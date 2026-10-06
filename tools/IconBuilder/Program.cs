using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASv2.IconBuilder
{
    /// <summary>
    /// Иконки кнопок из SVG (справочник стиля — СТИЛЬ.md). Для каждого Icons\&lt;имя&gt;.svg в src рисует рядом
    /// &lt;имя&gt;32.png, &lt;имя&gt;16.png и тёмные &lt;имя&gt;32.dark.png, &lt;имя&gt;16.dark.png. Тёмный вариант — замена цветов
    /// по палитре (palette.txt), акцент — цвет отдела модуля (Icons\department.txt, нет — «Общее»).
    /// Свой рисунок для 16 — &lt;имя&gt;.16.svg (иначе 16 — уменьшенный 32). Цвет не из палитры — ошибка: стиль один.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                var root = FindRoot();
                var palette = Palette.Load(Path.Combine(root, "tools", "IconBuilder", "palette.txt"));
                var built = new List<Built>();
                var errors = 0;
                foreach (var svg in Directory.GetFiles(Path.Combine(root, "src"), "*.svg", SearchOption.AllDirectories)
                             .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Icons" && !f.EndsWith(".16.svg", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try { built.Add(Build(svg, palette, root)); }
                    catch (Exception ex)
                    {
                        errors++;
                        Console.WriteLine("✗ " + Path.GetRelativePath(root, svg) + ": " + ex.Message);
                    }
                }
                foreach (var raster in RasterIcons(root))
                {
                    try { built.Add(raster()); }
                    catch (Exception ex)
                    {
                        errors++;
                        Console.WriteLine("✗ " + ex.Message);
                    }
                }
                var preview = Path.Combine(root, "out", "icons-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(preview));
                Preview.Save(built, palette, preview);
                Console.WriteLine();
                Console.WriteLine("Иконок: " + built.Count + (errors > 0 ? ", с ошибками: " + errors : "") + ". Просмотр — " + Path.GetRelativePath(root, preview));
                return errors == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка: " + ex.Message);
                return 1;
            }
        }

        internal sealed class Built
        {
            public string Name;
            public string Module;
            public string Department;
            public BitmapSource Light32, Light16, Dark32, Dark16;
        }

        private static Built Build(string svgPath, Palette palette, string root)
        {
            var dir = Path.GetDirectoryName(svgPath);
            var name = Path.GetFileNameWithoutExtension(svgPath);
            var departmentFile = Path.Combine(dir, "department.txt");
            var department = File.Exists(departmentFile) ? File.ReadAllText(departmentFile).Trim() : "Общее";
            var accent = palette.Accent(department);
            var small = Path.Combine(dir, name + ".16.svg");
            var svg32 = Svg.Load(svgPath);
            var svg16 = File.Exists(small) ? Svg.Load(small) : svg32;

            var built = new Built
            {
                Name = name,
                Module = Path.GetFileName(Path.GetDirectoryName(dir)),
                Department = department,
                Light32 = svg32.Render(32, c => palette.Light(c, accent.Light)),
                Light16 = svg16.Render(16, c => palette.Light(c, accent.Light)),
                Dark32 = svg32.Render(32, c => palette.Dark(c, accent.Dark)),
                Dark16 = svg16.Render(16, c => palette.Dark(c, accent.Dark))
            };
            Png.Save(built.Light32, Path.Combine(dir, name + "32.png"));
            Png.Save(built.Light16, Path.Combine(dir, name + "16.png"));
            Png.Save(built.Dark32, Path.Combine(dir, name + "32.dark.png"));
            Png.Save(built.Dark16, Path.Combine(dir, name + "16.dark.png"));
            Console.WriteLine("✓ " + Path.GetRelativePath(root, svgPath) + " (" + department + ")");
            return built;
        }

        /// <summary>
        /// Готовые картинки без SVG (логотип RAS у «О плагине»): &lt;имя&gt;32.png и &lt;имя&gt;16.png рисуются в редакторе,
        /// тёмный вариант — обращённая светлота с тем же оттенком (тёмно-синее становится светло-голубым).
        /// </summary>
        private static IEnumerable<Func<Built>> RasterIcons(string root)
        {
            foreach (var big in Directory.GetFiles(Path.Combine(root, "src"), "*32.png", SearchOption.AllDirectories)
                         .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Icons")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(big);
                var name = Path.GetFileName(big);
                name = name.Substring(0, name.Length - "32.png".Length);
                if (File.Exists(Path.Combine(dir, name + ".svg"))) continue; // картинки из SVG — уже сделаны
                var small = Path.Combine(dir, name + "16.png");
                yield return () =>
                {
                    if (!File.Exists(small)) throw new FileNotFoundException("нет " + Path.GetRelativePath(root, small));
                    var built = new Built
                    {
                        Name = name,
                        Module = Path.GetFileName(Path.GetDirectoryName(dir)),
                        Department = "готовая картинка",
                        Light32 = Png.Load(big),
                        Light16 = Png.Load(small)
                    };
                    built.Dark32 = Png.InvertLightness(built.Light32);
                    built.Dark16 = Png.InvertLightness(built.Light16);
                    Png.Save(built.Dark32, Path.Combine(dir, name + "32.dark.png"));
                    Png.Save(built.Dark16, Path.Combine(dir, name + "16.dark.png"));
                    Console.WriteLine("✓ " + Path.GetRelativePath(root, big) + " (готовая картинка: тёмный вариант — обращённая светлота)");
                    return built;
                };
            }
        }

        private static string FindRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PRINCIPLES.md"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Не найдена папка проекта (PRINCIPLES.md).");
        }
    }

    /// <summary>Палитра: цвет SVG → цвет тёмной темы, цвет-заглушка акцента, цвета отделов.</summary>
    internal sealed class Palette
    {
        private readonly Dictionary<string, Color> _dark = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (Color Light, Color Dark)> _departments = new Dictionary<string, (Color, Color)>(StringComparer.OrdinalIgnoreCase);
        private string _accent;

        public IEnumerable<KeyValuePair<string, (Color Light, Color Dark)>> Departments => _departments;

        public static Palette Load(string path)
        {
            var p = new Palette();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var hash = raw.IndexOf('#');
                var line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
                if (line.Length == 0) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) throw new FormatException("palette.txt: не понята строка «" + raw.Trim() + "»");
                var key = line.Substring(0, eq).Trim();
                var values = line.Substring(eq + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (key == "акцент") p._accent = Hex(values[0]);
                else if (IsHex(key)) p._dark[Hex(key)] = Parse(values[0]);
                else p._departments[key] = (Parse(values[0]), Parse(values[1]));
            }
            if (p._accent == null) throw new FormatException("palette.txt: нет строки «акцент = …»");
            return p;
        }

        public (Color Light, Color Dark) Accent(string department) =>
            _departments.TryGetValue(department, out var c) ? c : throw new FormatException("отдел «" + department + "» не найден в palette.txt");

        public Color Light(string svgColor, Color accent)
        {
            var hex = Hex(svgColor);
            if (hex == _accent) return accent;
            if (_dark.ContainsKey(hex)) return Parse(hex);
            throw new FormatException("цвет " + svgColor + " не из палитры (tools\\IconBuilder\\palette.txt)");
        }

        public Color Dark(string svgColor, Color accent)
        {
            var hex = Hex(svgColor);
            if (hex == _accent) return accent;
            if (_dark.TryGetValue(hex, out var c)) return c;
            throw new FormatException("цвет " + svgColor + " не из палитры (tools\\IconBuilder\\palette.txt)");
        }

        private static bool IsHex(string s) => s.TrimStart('#').Length == 6 && s.TrimStart('#').All(Uri.IsHexDigit);
        private static string Hex(string s) => s.Trim().TrimStart('#').ToUpperInvariant();
        private static Color Parse(string s) => (Color)ColorConverter.ConvertFromString("#" + Hex(s));
    }

    internal static class Png
    {
        public static void Save(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
        }

        public static BitmapSource Load(string path)
        {
            using (var file = File.OpenRead(path))
                return BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        /// <summary>Светлота каждой точки — наоборот (L → 1 − L), оттенок и прозрачность те же.</summary>
        public static BitmapSource InvertLightness(BitmapSource source)
        {
            var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = image.PixelWidth * 4;
            var px = new byte[stride * image.PixelHeight];
            image.CopyPixels(px, stride, 0);
            for (var i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] == 0) continue;
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double l = (max + min) / 2, h = 0, s = 0;
                if (max > min)
                {
                    var d = max - min;
                    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                    h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                    h /= 6;
                }
                l = 1 - l;
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                px[i + 2] = Channel(p, q, h + 1.0 / 3);
                px[i + 1] = Channel(p, q, h);
                px[i] = Channel(p, q, h - 1.0 / 3);
            }
            var result = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }

        private static byte Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            var v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            return (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        }
    }

    /// <summary>Лист просмотра: все иконки на светлой и тёмной ленте, 32 и 16, и цвета отделов.</summary>
    internal static class Preview
    {
        private static readonly Color LightRibbon = Color.FromRgb(0xF3, 0xF4, 0xF6);
        private static readonly Color DarkRibbon = Color.FromRgb(0x37, 0x3D, 0x46);

        public static void Save(List<Program.Built> icons, Palette palette, string path)
        {
            const int cell = 200, row = 120;
            var width = Math.Max(icons.Count, 5) * cell + 20;
            var height = row * 2 + 70;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                for (var band = 0; band < 2; band++)
                {
                    var dark = band == 1;
                    var top = band * row;
                    dc.DrawRectangle(new SolidColorBrush(dark ? DarkRibbon : LightRibbon), null, new Rect(0, top, width, row));
                    var ink = new SolidColorBrush(dark ? Color.FromRgb(0xD5, 0xDC, 0xE4) : Color.FromRgb(0x39, 0x42, 0x4E));
                    for (var i = 0; i < icons.Count; i++)
                    {
                        var x = 10 + i * cell;
                        var icon = icons[i];
                        var big = dark ? icon.Dark32 : icon.Light32;
                        var small = dark ? icon.Dark16 : icon.Light16;
                        // как на ленте: 32 и 16
                        dc.DrawImage(big, new Rect(x, top + 10, 32, 32));
                        dc.DrawImage(small, new Rect(x + 40, top + 18, 16, 16));
                        // увеличено «пиксель в пиксель» — видно, чётко ли нарисовано: 32 ×2, 16 ×4
                        var zoom = new DrawingGroup();
                        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
                        using (var z = zoom.Open())
                        {
                            z.DrawImage(big, new Rect(x + 64, top + 10, 64, 64));
                            z.DrawImage(small, new Rect(x + 132, top + 10, 64, 64));
                        }
                        dc.DrawDrawing(zoom);
                        dc.DrawText(Text(icon.Name + " · " + icon.Department, 11, ink), new Point(x, top + 80));
                        dc.DrawText(Text(icon.Module, 10, ink), new Point(x, top + 96));
                    }
                }
                var y = row * 2 + 12;
                var x0 = 10.0;
                var label = new SolidColorBrush(Color.FromRgb(0x39, 0x42, 0x4E));
                dc.DrawRectangle(Brushes.White, null, new Rect(0, row * 2, width, 70));
                dc.DrawText(Text("Отделы:", 11, label), new Point(x0, y + 4));
                x0 += 60;
                foreach (var d in palette.Departments)
                {
                    dc.DrawRectangle(new SolidColorBrush(d.Value.Light), null, new Rect(x0, y, 22, 22));
                    dc.DrawRectangle(new SolidColorBrush(d.Value.Dark), null, new Rect(x0 + 24, y, 22, 22));
                    dc.DrawText(Text(d.Key, 11, label), new Point(x0 + 52, y + 4));
                    x0 += 120;
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Png.Save(bitmap, path);
        }

        private static FormattedText Text(string s, double size, Brush brush) =>
            new FormattedText(s, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0);
    }
}
