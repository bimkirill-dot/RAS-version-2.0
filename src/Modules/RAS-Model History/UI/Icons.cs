using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Иконки ленты рисуются кодом — не нужны файлы ресурсов.</summary>
    public static class Icons
    {
        private static readonly uint Ink = 0xFF37474F;     // тёмно-серый
        private static readonly uint Accent = 0xFF1E88E5;  // синий
        private static readonly uint Paper = 0xFFFFFFFF;
        private static readonly uint Red = 0xFFE53935;
        private static readonly uint Green = 0xFF43A047;

        public static ImageSource Journal(int size) => Draw(size, PaintJournal);
        public static ImageSource History(int size) => Draw(size, PaintHistory);
        public static ImageSource Settings(int size) => Draw(size, PaintSettings);

        private static void PaintJournal(Canvas c)
        {
            c.Fill(Sdf.RoundRect(5, 3, 23, 27, 2.5), Paper);
            c.Fill(Sdf.Stroke(Sdf.RoundRect(5, 3, 23, 27, 2.5), 1.6), Ink);
            c.Fill(Sdf.Segment(9, 9, 19, 9, 1.8), Green);
            c.Fill(Sdf.Segment(9, 14, 19, 14, 1.8), Accent);
            c.Fill(Sdf.Segment(9, 19, 15, 19, 1.8), Red);
            c.Fill(Sdf.Circle(22, 22, 5.2), Paper);
            c.Fill(Sdf.Stroke(Sdf.Circle(22, 22, 4.6), 2.0), Accent);
            c.Fill(Sdf.Segment(25.5, 25.5, 29.5, 29.5, 2.6), Accent);
        }

        private static void PaintHistory(Canvas c)
        {
            c.Fill(Sdf.Circle(16, 16, 12.5), Paper);
            c.Fill(Sdf.Stroke(Sdf.Circle(16, 16, 12), 2.2), Ink);
            c.Fill(Sdf.Segment(16, 16, 16, 8, 2.2), Accent);
            c.Fill(Sdf.Segment(16, 16, 21.5, 19, 2.2), Accent);
            c.Fill(Sdf.Circle(16, 16, 1.8), Ink);
        }

        private static void PaintSettings(Canvas c)
        {
            c.Fill(Sdf.Gear(16, 16, 10.5, 3.2, 8), Ink);
            c.Fill(Sdf.Circle(16, 16, 4.2), Paper);
        }

        private static ImageSource Draw(int size, Action<Canvas> paint)
        {
            var canvas = new Canvas(size);
            paint(canvas);
            return canvas.ToBitmap();
        }

        /// <summary>Пиксели иконки в формате BGRA (для проверки отрисовки без WPF).</summary>
        public static byte[] RenderPixels(string name, int size)
        {
            var canvas = new Canvas(size);
            switch (name)
            {
                case "journal": PaintJournal(canvas); break;
                case "history": PaintHistory(canvas); break;
                default: PaintSettings(canvas); break;
            }
            return canvas.ToPixels();
        }

        private sealed class Canvas
        {
            private readonly int _size;
            private readonly double _scale;
            private readonly double[] _r, _g, _b, _a;

            public Canvas(int size)
            {
                _size = size;
                _scale = size / 32.0;
                _r = new double[size * size];
                _g = new double[size * size];
                _b = new double[size * size];
                _a = new double[size * size];
            }

            /// <summary>Залить фигуру, заданную функцией расстояния (в единицах сетки 32×32), со сглаживанием.</summary>
            public void Fill(Func<double, double, double> sdf, uint argb)
            {
                double ca = ((argb >> 24) & 0xFF) / 255.0;
                double cr = ((argb >> 16) & 0xFF) / 255.0;
                double cg = ((argb >> 8) & 0xFF) / 255.0;
                double cb = (argb & 0xFF) / 255.0;
                for (int y = 0; y < _size; y++)
                {
                    for (int x = 0; x < _size; x++)
                    {
                        var ux = (x + 0.5) / _scale;
                        var uy = (y + 0.5) / _scale;
                        var dPx = sdf(ux, uy) * _scale;
                        var cov = Math.Max(0, Math.Min(1, 0.5 - dPx)) * ca;
                        if (cov <= 0) continue;
                        int i = y * _size + x;
                        var outA = cov + _a[i] * (1 - cov);
                        if (outA <= 0) continue;
                        _r[i] = (cr * cov + _r[i] * _a[i] * (1 - cov)) / outA;
                        _g[i] = (cg * cov + _g[i] * _a[i] * (1 - cov)) / outA;
                        _b[i] = (cb * cov + _b[i] * _a[i] * (1 - cov)) / outA;
                        _a[i] = outA;
                    }
                }
            }

            public ImageSource ToBitmap()
            {
                var pixels = ToPixels();
                var bmp = BitmapSource.Create(_size, _size, 96, 96, PixelFormats.Bgra32, null, pixels, _size * 4);
                bmp.Freeze();
                return bmp;
            }

            public byte[] ToPixels()
            {
                var pixels = new byte[_size * _size * 4];
                for (int i = 0; i < _size * _size; i++)
                {
                    pixels[i * 4 + 0] = ToByte(_b[i]);
                    pixels[i * 4 + 1] = ToByte(_g[i]);
                    pixels[i * 4 + 2] = ToByte(_r[i]);
                    pixels[i * 4 + 3] = ToByte(_a[i]);
                }
                return pixels;
            }

            private static byte ToByte(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        }

        /// <summary>Функции расстояния до простых фигур.</summary>
        private static class Sdf
        {
            public static Func<double, double, double> Circle(double cx, double cy, double r) =>
                (x, y) => Len(x - cx, y - cy) - r;

            public static Func<double, double, double> Stroke(Func<double, double, double> shape, double width) =>
                (x, y) => Math.Abs(shape(x, y)) - width / 2;

            public static Func<double, double, double> RoundRect(double x0, double y0, double x1, double y1, double rad) =>
                (x, y) =>
                {
                    var cx = (x0 + x1) / 2;
                    var cy = (y0 + y1) / 2;
                    var hx = (x1 - x0) / 2 - rad;
                    var hy = (y1 - y0) / 2 - rad;
                    var qx = Math.Abs(x - cx) - hx;
                    var qy = Math.Abs(y - cy) - hy;
                    return Len(Math.Max(qx, 0), Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - rad;
                };

            public static Func<double, double, double> Segment(double ax, double ay, double bx, double by, double width) =>
                (x, y) =>
                {
                    double px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
                    var h = Math.Max(0, Math.Min(1, (px * dx + py * dy) / (dx * dx + dy * dy)));
                    return Len(px - dx * h, py - dy * h) - width / 2;
                };

            public static Func<double, double, double> Gear(double cx, double cy, double r, double tooth, int teeth) =>
                (x, y) =>
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    var angle = Math.Atan2(dy, dx);
                    var wave = Math.Max(-1, Math.Min(1, Math.Cos(teeth * angle) * 3)); // «квадратные» зубья
                    var radius = r + tooth * (wave + 1) / 2 - tooth / 2;
                    return Len(dx, dy) - radius;
                };

            private static double Len(double x, double y) => Math.Sqrt(x * x + y * y);
        }
    }
}
