using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RASv2.SheetManager.Logic
{
    /// <summary>Как приводить подписи к одному размеру (dwg_clean.py, --mode).</summary>
    public static class SizeModes
    {
        /// <summary>Одной высоты, ширина не больше заданной.</summary>
        public const string Height = "height";
        /// <summary>Одной ширины, высота не больше заданной.</summary>
        public const string Width = "width";
        /// <summary>Вписать в рамку, пропорции сохраняются.</summary>
        public const string Box = "box";
        /// <summary>Не менять размер — как в DWG (по единицам чертежа).</summary>
        public const string Keep = "keep";
        public static readonly string[] All = { Height, Width, Box, Keep };
    }

    /// <summary>Что делать со штриховками: оставить контуром или выбросить.</summary>
    public static class HatchModes
    {
        public const string Outline = "outline";
        public const string Drop = "drop";
    }

    /// <summary>Параметры очистки — числа из настроек модуля (по умолчанию — как в dwg_clean.bat).</summary>
    public sealed class CleanOptions
    {
        public string Mode = SizeModes.Height;
        public double Height = 7, MaxWidth = 22;
        public double Width = 20, MaxHeight = 9;
        public double BoxWidth = 22, BoxHeight = 8;
        /// <summary>Точность прореживания, мм.</summary>
        public double Tolerance = 0.02;
        public string Hatch = HatchModes.Outline;

        /// <summary>Чем очищена подпись — запоминается с картинкой: настройки поменялись — подпись «по прежним настройкам».</summary>
        public string Key => string.Join(";", new object[] { Mode, Height, MaxWidth, Width, MaxHeight, BoxWidth, BoxHeight, Tolerance, Hatch }
            .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
    }

    /// <summary>Чистая подпись: ломаные в мм, центр габарита — в 0,0.</summary>
    public sealed class CleanSign
    {
        public List<Polyline2> Lines = new List<Polyline2>();
        public double WidthMm, HeightMm;
        public int SourceVertices, Vertices;
        public int Blocks, HiddenSkipped, Hatches;

        public string Summary =>
            string.Format(CultureInfo.GetCultureInfo("ru-RU"), "{0:0.0}×{1:0.0} мм, ломаных {2}, точек {3} → {4}", WidthMm, HeightMm, Lines.Count, SourceVertices, Vertices);
    }

    /// <summary>
    /// Очистка подписи — перенос dwg_clean.py (clean_file): геометрия в ломаные, сшивка кусков, стыкующихся концами,
    /// прореживание (Дуглас — Пекер), один размер для всех подписей, центр подписи — в 0,0. Результат пишет DxfWriter.
    /// Без Revit API и AutoCAD — проверяется тестами.
    /// </summary>
    public static class SignCleaner
    {
        /// <summary>null — в подписи нет геометрии (reason — почему).</summary>
        public static CleanSign Clean(DxfDocument doc, CleanOptions o, out string reason)
        {
            reason = null;
            var toMm = doc.ToMillimetres;
            // сначала грубо — чтобы узнать размер и масштаб, потом с нужной точностью в единицах чертежа
            var rough = new SignGeometry(doc, 1e9);
            var all = rough.Curves.Concat(o.Hatch == HatchModes.Drop ? Enumerable.Empty<Polyline2>() : rough.HatchLoops).ToList();
            if (all.Count == 0 || all.All(p => p.Count < 2))
            {
                reason = "в подписи нет линий";
                return null;
            }
            var (minX, minY, maxX, maxY) = Bounds(all);
            var bw = Math.Max(maxX - minX, 1e-9);
            var bh = Math.Max(maxY - minY, 1e-9);
            var k = Scale(o, bw, bh, toMm);
            var geo = new SignGeometry(doc, o.Tolerance / 4 / k);
            var curves = geo.Curves;
            var hatch = o.Hatch == HatchModes.Drop ? new List<Polyline2>() : geo.HatchLoops;
            (minX, minY, maxX, maxY) = Bounds(curves.Concat(hatch).ToList());
            // финальный масштаб — по точному габариту
            bw = Math.Max(maxX - minX, 1e-9);
            bh = Math.Max(maxY - minY, 1e-9);
            k = Scale(o, bw, bh, toMm);
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

            var result = new CleanSign
            {
                Blocks = geo.Blocks,
                HiddenSkipped = geo.HiddenSkipped,
                Hatches = geo.Hatches,
                SourceVertices = curves.Sum(p => p.Count) + hatch.Sum(p => p.Count),
            };
            var gap = Math.Max(o.Tolerance / k, Math.Max(bw, bh) * 1e-6);
            var eps = o.Tolerance / k;
            var lines = Join(curves.Where(p => !p.Closed).ToList(), gap);
            lines.AddRange(curves.Where(p => p.Closed));
            lines.AddRange(hatch);
            foreach (var p in lines)
            {
                var r = Rdp(p, eps);
                var mm = new Polyline2();
                for (var i = 0; i < r.Count; i++) mm.Add((r.X[i] - cx) * k, (r.Y[i] - cy) * k);
                // замкнутая — если концы сошлись в пределах точности
                if (mm.Count > 2 && Math.Abs(mm.X[0] - mm.X[mm.Count - 1]) < o.Tolerance && Math.Abs(mm.Y[0] - mm.Y[mm.Count - 1]) < o.Tolerance)
                {
                    mm.X.RemoveAt(mm.Count - 1);
                    mm.Y.RemoveAt(mm.Y.Count - 1);
                    mm.Closed = true;
                }
                mm.Closed |= p.Closed && mm.Count > 2;
                if (mm.Count >= 2) result.Lines.Add(mm);
            }
            result.Vertices = result.Lines.Sum(p => p.Count);
            result.WidthMm = bw * k;
            result.HeightMm = bh * k;
            return result;
        }

        /// <summary>Во сколько раз увеличить (единицы чертежа → мм нужного размера).</summary>
        public static double Scale(CleanOptions o, double bw, double bh, double toMm)
        {
            switch (o.Mode)
            {
                case SizeModes.Keep: return toMm;
                case SizeModes.Width:
                {
                    var k = o.Width / bw;
                    if (o.MaxHeight > 0 && bh * k > o.MaxHeight) k = o.MaxHeight / bh;
                    return k;
                }
                case SizeModes.Box: return Math.Min(o.BoxWidth / bw, o.BoxHeight / bh);
                default:
                {
                    var k = o.Height / bh;
                    if (o.MaxWidth > 0 && bw * k > o.MaxWidth) k = o.MaxWidth / bw;
                    return k;
                }
            }
        }

        public static (double, double, double, double) Bounds(IEnumerable<Polyline2> lines)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in lines)
            for (var i = 0; i < p.Count; i++)
            {
                minX = Math.Min(minX, p.X[i]); maxX = Math.Max(maxX, p.X[i]);
                minY = Math.Min(minY, p.Y[i]); maxY = Math.Max(maxY, p.Y[i]);
            }
            return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
        }

        /// <summary>Прореживание Дугласа — Пекера: точки, отходящие от прямой меньше eps, убираются.</summary>
        public static Polyline2 Rdp(Polyline2 p, double eps)
        {
            var n = p.Count;
            if (n < 3) return p;
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                if (j <= i + 1) continue;
                double ax = p.X[i], ay = p.Y[i], dx = p.X[j] - ax, dy = p.Y[j] - ay;
                var len = Math.Sqrt(dx * dx + dy * dy);
                double best = -1;
                var bi = -1;
                for (var k = i + 1; k < j; k++)
                {
                    var d = len < 1e-12
                        ? Math.Sqrt((p.X[k] - ax) * (p.X[k] - ax) + (p.Y[k] - ay) * (p.Y[k] - ay))
                        : Math.Abs(dx * (p.Y[k] - ay) - dy * (p.X[k] - ax)) / len;
                    if (d > best) { best = d; bi = k; }
                }
                if (best > eps)
                {
                    keep[bi] = true;
                    stack.Push((i, bi));
                    stack.Push((bi, j));
                }
            }
            var r = new Polyline2 { Closed = p.Closed };
            for (var k = 0; k < n; k++) if (keep[k]) r.Add(p.X[k], p.Y[k]);
            return r;
        }

        /// <summary>Сшить ломаные, у которых концы совпадают (в пределах gap), — как join_chains в dwg_clean.py.</summary>
        public static List<Polyline2> Join(List<Polyline2> polys, double gap)
        {
            var list = polys.Select(p => (X: p.X.ToList(), Y: p.Y.ToList())).ToList();
            var changed = true;
            while (changed)
            {
                changed = false;
                var ends = new Dictionary<(long, long), List<(int Idx, int End)>>();
                (long, long) Key(double x, double y) => ((long)Math.Round(x / gap), (long)Math.Round(y / gap));
                for (var idx = 0; idx < list.Count; idx++)
                {
                    var p = list[idx];
                    foreach (var (end, x, y) in new[] { (0, p.X[0], p.Y[0]), (1, p.X[p.X.Count - 1], p.Y[p.Y.Count - 1]) })
                    {
                        var key = Key(x, y);
                        if (!ends.TryGetValue(key, out var bucket)) ends[key] = bucket = new List<(int, int)>();
                        bucket.Add((idx, end));
                    }
                }
                var used = new bool[list.Count];
                var merged = new List<(List<double> X, List<double> Y)>();
                for (var idx = 0; idx < list.Count; idx++)
                {
                    if (used[idx]) continue;
                    used[idx] = true;
                    var cur = (X: list[idx].X.ToList(), Y: list[idx].Y.ToList());
                    var grown = true;
                    while (grown)
                    {
                        grown = false;
                        foreach (var side in new[] { 1, 0 })
                        {
                            var px = side == 1 ? cur.X[cur.X.Count - 1] : cur.X[0];
                            var py = side == 1 ? cur.Y[cur.Y.Count - 1] : cur.Y[0];
                            if (!ends.TryGetValue(Key(px, py), out var bucket)) continue;
                            foreach (var (j, end) in bucket)
                            {
                                if (used[j]) continue;
                                var q = list[j];
                                var qx = q.X.ToList();
                                var qy = q.Y.ToList();
                                if (side == 1)
                                {
                                    // к концу: q с начала (end 0) или задом наперёд (end 1), без общей точки
                                    if (end == 1) { qx.Reverse(); qy.Reverse(); }
                                    cur.X.AddRange(qx.Skip(1)); cur.Y.AddRange(qy.Skip(1));
                                }
                                else
                                {
                                    // к началу: q так, чтобы его конец пришёлся на наше начало
                                    if (end == 0) { qx.Reverse(); qy.Reverse(); }
                                    qx.RemoveAt(qx.Count - 1); qy.RemoveAt(qy.Count - 1);
                                    qx.AddRange(cur.X); qy.AddRange(cur.Y);
                                    cur = (qx, qy);
                                }
                                used[j] = true;
                                grown = changed = true;
                                break;
                            }
                            if (grown) break;
                        }
                    }
                    merged.Add(cur);
                }
                list = merged;
            }
            return list.Select(p => new Polyline2 { X = p.X, Y = p.Y }).ToList();
        }
    }

    /// <summary>
    /// Чистый DXF подписи: только ломаные на слое 0 (синий, цвет 5), цвет и тип линии — «по слою», единицы — мм. Формат R12 —
    /// самый простой, его открывает любой AutoCAD; в DWG его переводит AutoCAD Core Console (там же — лёгкие полилинии и мм).
    /// </summary>
    public static class DxfWriter
    {
        public static string Write(CleanSign sign)
        {
            var sb = new StringBuilder();
            void Pair(int code, string value) => sb.Append(code).Append("\r\n").Append(value).Append("\r\n");
            string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            Pair(0, "SECTION"); Pair(2, "HEADER");
            Pair(9, "$ACADVER"); Pair(1, "AC1009");
            Pair(9, "$INSUNITS"); Pair(70, "4");
            Pair(9, "$MEASUREMENT"); Pair(70, "1");
            Pair(0, "ENDSEC");
            Pair(0, "SECTION"); Pair(2, "TABLES");
            Pair(0, "TABLE"); Pair(2, "LAYER"); Pair(70, "1");
            Pair(0, "LAYER"); Pair(2, "0"); Pair(70, "0"); Pair(62, "5"); Pair(6, "CONTINUOUS");
            Pair(0, "ENDTAB");
            Pair(0, "ENDSEC");
            Pair(0, "SECTION"); Pair(2, "ENTITIES");
            foreach (var p in sign.Lines)
            {
                Pair(0, "POLYLINE"); Pair(8, "0"); Pair(66, "1");
                Pair(10, "0"); Pair(20, "0"); Pair(30, "0");
                Pair(70, p.Closed ? "1" : "0");
                for (var i = 0; i < p.Count; i++)
                {
                    Pair(0, "VERTEX"); Pair(8, "0");
                    Pair(10, N(p.X[i])); Pair(20, N(p.Y[i])); Pair(30, "0");
                }
                Pair(0, "SEQEND"); Pair(8, "0");
            }
            Pair(0, "ENDSEC");
            Pair(0, "EOF");
            return sb.ToString();
        }
    }
}
