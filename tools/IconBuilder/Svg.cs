using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace RASv2.IconBuilder
{
    /// <summary>
    /// Простое подмножество SVG — то, из чего состоят иконки (СТИЛЬ.md): svg (viewBox), g, rect, circle, ellipse, line,
    /// polyline, polygon, path; fill, stroke, stroke-width, stroke-linecap, stroke-linejoin, opacity — атрибутами или
    /// в style; transform: translate, scale, rotate, matrix. Остальное (градиенты, текст, фильтры) — ошибка:
    /// в иконках этого быть не должно.
    /// </summary>
    internal sealed class Svg
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly XElement _root;
        private readonly Rect _viewBox;

        private Svg(XElement root, Rect viewBox)
        {
            _root = root;
            _viewBox = viewBox;
        }

        public static Svg Load(string path)
        {
            var root = XDocument.Load(path).Root ?? throw new FormatException("пустой файл");
            if (root.Name.LocalName != "svg") throw new FormatException("это не SVG");
            var vb = Numbers((string)root.Attribute("viewBox") ?? "");
            if (vb.Length != 4) throw new FormatException("нет viewBox (например, viewBox=\"0 0 32 32\")");
            return new Svg(root, new Rect(vb[0], vb[1], vb[2], vb[3]));
        }

        /// <summary>Нарисовать size×size; цвет каждого элемента — через map (палитра темы).</summary>
        public BitmapSource Render(int size, Func<string, Color> map)
        {
            var group = new DrawingGroup();
            var style = new Style { Fill = "#000000", StrokeWidth = 1, Cap = PenLineCap.Flat, Join = PenLineJoin.Miter, Opacity = 1 };
            foreach (var child in _root.Elements()) Draw(child, style, group, map);
            group.Transform = new MatrixTransform(new Matrix(size / _viewBox.Width, 0, 0, size / _viewBox.Height,
                -_viewBox.X * size / _viewBox.Width, -_viewBox.Y * size / _viewBox.Height));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // рамка во весь размер: без неё картинка обрезалась бы по рисунку
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
                dc.DrawDrawing(group);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private sealed class Style
        {
            public string Fill, Stroke;
            public double StrokeWidth, Opacity;
            public PenLineCap Cap;
            public PenLineJoin Join;
            public Style Copy() => (Style)MemberwiseClone();
        }

        private static void Draw(XElement e, Style inherited, DrawingGroup into, Func<string, Color> map)
        {
            var name = e.Name.LocalName;
            if (name == "title" || name == "desc" || name == "metadata" || name == "defs" && !e.HasElements) return;
            var s = Apply(e, inherited.Copy());
            var transform = Transform((string)e.Attribute("transform"));

            if (name == "g")
            {
                var group = new DrawingGroup { Transform = transform, Opacity = s.Opacity };
                foreach (var child in e.Elements()) Draw(child, s, group, map);
                into.Children.Add(group);
                return;
            }

            var geometry = Shape(e, name);
            if (transform != null) geometry.Transform = transform;
            // у линии нет площади: заливку по умолчанию (чёрную) не спрашиваем у палитры
            var fill = s.Fill == null || s.Fill == "none" || name == "line" ? null : new SolidColorBrush(map(s.Fill));
            var pen = s.Stroke == null || s.Stroke == "none" || s.StrokeWidth <= 0 ? null
                : new Pen(new SolidColorBrush(map(s.Stroke)), s.StrokeWidth) { StartLineCap = s.Cap, EndLineCap = s.Cap, LineJoin = s.Join };
            var drawing = new GeometryDrawing(fill, pen, geometry);
            if (Math.Abs(s.Opacity - 1) < 1e-6) into.Children.Add(drawing);
            else into.Children.Add(new DrawingGroup { Opacity = s.Opacity, Children = { drawing } });
        }

        private static Geometry Shape(XElement e, string name)
        {
            double A(string attr) => double.Parse((string)e.Attribute(attr) ?? "0", Inv);
            switch (name)
            {
                case "rect":
                    var rx = A("rx");
                    var ry = e.Attribute("ry") != null ? A("ry") : rx;
                    if (e.Attribute("rx") == null) rx = ry;
                    return new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), rx, ry);
                case "circle":
                    return new EllipseGeometry(new Point(A("cx"), A("cy")), A("r"), A("r"));
                case "ellipse":
                    return new EllipseGeometry(new Point(A("cx"), A("cy")), A("rx"), A("ry"));
                case "line":
                    return new LineGeometry(new Point(A("x1"), A("y1")), new Point(A("x2"), A("y2")));
                case "polyline":
                case "polygon":
                    var n = Numbers((string)e.Attribute("points") ?? "");
                    var points = Enumerable.Range(0, n.Length / 2).Select(i => new Point(n[2 * i], n[2 * i + 1])).ToList();
                    var figure = new PathFigure(points[0], points.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), name == "polygon");
                    return new PathGeometry(new[] { figure });
                case "path":
                    return Geometry.Parse((string)e.Attribute("d") ?? throw new FormatException("path без d"));
                default:
                    throw new FormatException("элемент <" + name + "> в иконках не поддерживается (СТИЛЬ.md)");
            }
        }

        /// <summary>Атрибуты и style="…" элемента поверх унаследованных.</summary>
        private static Style Apply(XElement e, Style s)
        {
            var props = new Dictionary<string, string>();
            foreach (var a in e.Attributes()) props[a.Name.LocalName] = a.Value;
            var css = (string)e.Attribute("style");
            if (css != null)
                foreach (var part in css.Split(';'))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2) props[kv[0].Trim()] = kv[1].Trim();
                }
            if (props.TryGetValue("fill", out var fill)) s.Fill = fill;
            if (props.TryGetValue("stroke", out var stroke)) s.Stroke = stroke;
            if (props.TryGetValue("stroke-width", out var w)) s.StrokeWidth = double.Parse(w.Replace("px", ""), Inv);
            if (props.TryGetValue("opacity", out var o)) s.Opacity *= double.Parse(o, Inv);
            if (props.TryGetValue("stroke-linecap", out var cap))
                s.Cap = cap == "round" ? PenLineCap.Round : cap == "square" ? PenLineCap.Square : PenLineCap.Flat;
            if (props.TryGetValue("stroke-linejoin", out var join))
                s.Join = join == "round" ? PenLineJoin.Round : join == "bevel" ? PenLineJoin.Bevel : PenLineJoin.Miter;
            foreach (var bad in new[] { "fill-opacity", "stroke-opacity", "stroke-dasharray", "filter", "mask", "clip-path" })
                if (props.ContainsKey(bad)) throw new FormatException("«" + bad + "» в иконках не поддерживается (СТИЛЬ.md)");
            if (s.Fill != null && s.Fill.StartsWith("url(")) throw new FormatException("градиенты в иконках не поддерживаются (СТИЛЬ.md)");
            return s;
        }

        private static Transform Transform(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Matrix.Identity;
            foreach (Match op in Regex.Matches(text, @"(\w+)\s*\(([^)]*)\)"))
            {
                var v = Numbers(op.Groups[2].Value);
                var t = Matrix.Identity;
                switch (op.Groups[1].Value)
                {
                    case "translate": t.Translate(v[0], v.Length > 1 ? v[1] : 0); break;
                    case "scale": t.Scale(v[0], v.Length > 1 ? v[1] : v[0]); break;
                    case "rotate":
                        if (v.Length == 3) t.RotateAt(v[0], v[1], v[2]);
                        else t.Rotate(v[0]);
                        break;
                    case "matrix": t = new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]); break;
                    default: throw new FormatException("transform «" + op.Groups[1].Value + "» не поддерживается");
                }
                // SVG: преобразования применяются справа налево
                m = t * m;
            }
            return new MatrixTransform(m);
        }

        private static double[] Numbers(string s) =>
            Regex.Matches(s, @"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(x => double.Parse(x.Value, Inv)).ToArray();
    }
}
