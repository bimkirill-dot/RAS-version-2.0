using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.SheetManager.Logic
{
    public struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Unit { get { var l = Length; return l < 1e-15 ? this : this * (1 / l); } }
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    /// <summary>Аффинное преобразование: p' = X·x + Y·y + Z·z + O.</summary>
    public struct Xform
    {
        public readonly Vec3 Ax, Ay, Az, O;
        public Xform(Vec3 ax, Vec3 ay, Vec3 az, Vec3 o) { Ax = ax; Ay = ay; Az = az; O = o; }
        public static readonly Xform Identity = new Xform(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(0, 0, 0));
        public Vec3 Apply(Vec3 p) => Ax * p.X + Ay * p.Y + Az * p.Z + O;
        public Vec3 ApplyVector(Vec3 v) => Ax * v.X + Ay * v.Y + Az * v.Z;
        /// <summary>Сначала inner, потом this.</summary>
        public Xform Then(Xform inner) => new Xform(ApplyVector(inner.Ax), ApplyVector(inner.Ay), ApplyVector(inner.Az), Apply(inner.O));

        /// <summary>
        /// Система координат объекта (OCS) по его нормали — «произвольная ось» AutoCAD: у зеркальных блоков, дуг и полилиний
        /// нормаль (0,0,-1), и без этого они легли бы зеркально.
        /// </summary>
        public static Xform Ocs(Vec3 normal)
        {
            var n = normal.Unit;
            if (Math.Abs(n.X) < 1e-12 && Math.Abs(n.Y) < 1e-12 && n.Z > 0) return Identity;
            var ax = Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
                ? Vec3.Cross(new Vec3(0, 1, 0), n).Unit
                : Vec3.Cross(new Vec3(0, 0, 1), n).Unit;
            var ay = Vec3.Cross(n, ax).Unit;
            return new Xform(ax, ay, n, new Vec3(0, 0, 0));
        }
    }

    /// <summary>Ломаная на плоскости XY (в мм после очистки).</summary>
    public sealed class Polyline2
    {
        public List<double> X = new List<double>(), Y = new List<double>();
        public bool Closed;
        public int Count => X.Count;
        public void Add(double x, double y)
        {
            // подряд одинаковые точки не нужны
            if (X.Count > 0 && Math.Abs(X[X.Count - 1] - x) < 1e-12 && Math.Abs(Y[Y.Count - 1] - y) < 1e-12) return;
            X.Add(x); Y.Add(y);
        }
    }

    /// <summary>
    /// Геометрия подписи из DXF — как в dwg_clean.py (collect, entity_path): объекты модели и раскрытые блоки (с масштабом,
    /// поворотом, зеркалом, массивами MINSERT, вложенные до 10 уровней) в ломаные. Тексты, размеры, точки, выноски и прочее
    /// — выбрасываются; объекты на выключенных и замороженных слоях — тоже (объект слоя 0 внутри блока живёт на слое вставки).
    /// Без Revit API — проверяется тестами.
    /// </summary>
    public sealed class SignGeometry
    {
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TEXT", "MTEXT", "ATTRIB", "ATTDEF", "DIMENSION", "LEADER", "MLEADER", "MULTILEADER", "POINT", "VIEWPORT",
            "IMAGE", "WIPEOUT", "OLE2FRAME", "TOLERANCE", "XLINE", "RAY", "SOLID", "TRACE", "3DFACE", "ACAD_TABLE",
        };

        private readonly DxfDocument _doc;
        private readonly double _tol;

        public List<Polyline2> Curves { get; } = new List<Polyline2>();
        /// <summary>Контуры штриховок — отдельно: их можно оставить контуром или выбросить.</summary>
        public List<Polyline2> HatchLoops { get; } = new List<Polyline2>();
        public int Blocks { get; private set; }
        public int HiddenSkipped { get; private set; }
        public int Hatches { get; private set; }

        /// <param name="tolerance">Точность перевода дуг и сплайнов в ломаные, в единицах чертежа.</param>
        public SignGeometry(DxfDocument doc, double tolerance)
        {
            _doc = doc;
            _tol = Math.Max(tolerance, 1e-9);
            foreach (var e in doc.Model) Collect(e, Xform.Identity, null, 0);
        }

        private void Collect(DxfEntity e, Xform t, string parentLayer, int depth)
        {
            if (Skip.Contains(e.Type) || depth > 10) return;
            if (e.Int(60) == 1) return; // невидимый объект — AutoCAD его не показывает
            var layer = e.Layer;
            if (layer == "0" && parentLayer != null) layer = parentLayer;
            if (_doc.HiddenLayers.Contains(layer)) { HiddenSkipped++; return; }
            switch (e.Type)
            {
                case "INSERT": Insert(e, t, layer, depth); break;
                case "LINE":
                    Add(t, new[] { P(e, 10), P(e, 11) }, false);
                    break;
                case "LWPOLYLINE": LwPolyline(e, t); break;
                case "POLYLINE": Polyline(e, t); break;
                case "ARC":
                case "CIRCLE": Arc(e, t); break;
                case "ELLIPSE": Ellipse(e, t); break;
                case "SPLINE": Spline(e, t); break;
                case "HATCH": Hatch(e, t); break;
            }
        }

        private static Vec3 P(DxfEntity e, int code) => new Vec3(e.Num(code), e.Num(code + 10), e.Num(code + 20));

        private void Add(Xform t, IEnumerable<Vec3> points, bool closed, List<Polyline2> into = null)
        {
            var pl = new Polyline2 { Closed = closed };
            foreach (var p in points)
            {
                var w = t.Apply(p);
                pl.Add(w.X, w.Y);
            }
            if (pl.Count >= 2) (into ?? Curves).Add(pl);
        }

        private void Insert(DxfEntity e, Xform t, string layer, int depth)
        {
            if (!_doc.Blocks.TryGetValue(e.Str(2, ""), out var block)) return;
            Blocks++;
            double sx = e.Num(41, 1), sy = e.Num(42, 1), sz = e.Num(43, 1);
            var rot = e.Num(50) * Math.PI / 180;
            int cols = Math.Max(1, e.Int(70, 1)), rows = Math.Max(1, e.Int(71, 1));
            double dc = e.Num(44), dr = e.Num(45);
            var ocs = Xform.Ocs(e.Extrusion);
            var ins = P(e, 10);
            double c = Math.Cos(rot), s = Math.Sin(rot);
            for (var r = 0; r < rows; r++)
            for (var k = 0; k < cols; k++)
            {
                // массив MINSERT: смещение по столбцам и строкам — в повёрнутой системе вставки
                var off = new Vec3(k * dc * c - r * dr * s, k * dc * s + r * dr * c, 0);
                // точка блока b → (b − база)·масштаб → поворот → + точка вставки (в OCS вставки) → мир
                var local = new Xform(new Vec3(c * sx, s * sx, 0), new Vec3(-s * sy, c * sy, 0), new Vec3(0, 0, sz), ins + off)
                    .Then(new Xform(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), block.Base * -1));
                var full = t.Then(ocs.Then(local));
                foreach (var be in block.Entities) Collect(be, full, layer, depth + 1);
            }
        }

        private void LwPolyline(DxfEntity e, Xform t)
        {
            var elev = e.Num(38);
            var pts = new List<Vec3>();
            var bulges = new List<double>();
            for (var i = 0; i < e.Pairs.Count; i++)
            {
                var p = e.Pairs[i];
                if (p.Key == 10)
                {
                    var x = Dbl(p.Value);
                    var y = i + 1 < e.Pairs.Count && e.Pairs[i + 1].Key == 20 ? Dbl(e.Pairs[i + 1].Value) : 0;
                    pts.Add(new Vec3(x, y, elev));
                    bulges.Add(0);
                }
                else if (p.Key == 42 && bulges.Count > 0) bulges[bulges.Count - 1] = Dbl(p.Value);
            }
            var closed = (e.Int(70) & 1) != 0;
            Add(t.Then(Xform.Ocs(e.Extrusion)), Bulged(pts, bulges, closed), closed);
        }

        private void Polyline(DxfEntity e, Xform t)
        {
            var flags = e.Int(70);
            if ((flags & (16 | 64)) != 0) return; // сеть и многогранная сеть — не подпись
            var is3D = (flags & 8) != 0;
            var closed = (flags & 1) != 0;
            var vertices = e.Vertices.Where(v => (v.Int(70) & 16) == 0).ToList(); // без вершин рамки сплайна
            // сглаженная сплайном (PEDIT → Сплайн): AutoCAD рисует только саму кривую — вершины с флагом 8
            if ((flags & 4) != 0)
            {
                var fit = vertices.Where(v => (v.Int(70) & 8) != 0).ToList();
                if (fit.Count >= 2) vertices = fit;
            }
            var elev = e.Num(30);
            var pts = vertices.Select(v => new Vec3(v.Num(10), v.Num(20), is3D ? v.Num(30) : elev)).ToList();
            var bulges = vertices.Select(v => is3D ? 0 : v.Num(42)).ToList();
            var tr = is3D ? t : t.Then(Xform.Ocs(e.Extrusion));
            Add(tr, Bulged(pts, bulges, closed), closed);
        }

        /// <summary>Отрезки с «выпуклостью» (дуги в полилинии) — в точки.</summary>
        private IEnumerable<Vec3> Bulged(List<Vec3> pts, List<double> bulges, bool closed)
        {
            var n = pts.Count;
            if (n == 0) yield break;
            var segs = closed ? n : n - 1;
            yield return pts[0];
            for (var i = 0; i < segs; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % n];
                var bulge = bulges[i];
                if (Math.Abs(bulge) > 1e-9)
                {
                    // дуга от a к b: выпуклость = tg(θ/4), θ — центральный угол (больше нуля — против часовой).
                    // Центр — от середины хорды по левой нормали на (хорда/2)·(1 − b²)/(2b): знак сам ставит его по нужную сторону.
                    var theta = 4 * Math.Atan(bulge);
                    var chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    if (chord > 1e-12)
                    {
                        var r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
                        var h = chord / 2 * (1 - bulge * bulge) / (2 * bulge);
                        var cx = (a.X + b.X) / 2 - (b.Y - a.Y) / chord * h;
                        var cy = (a.Y + b.Y) / 2 + (b.X - a.X) / chord * h;
                        var a0 = Math.Atan2(a.Y - cy, a.X - cx);
                        foreach (var q in ArcPoints(cx, cy, r, a0, theta, a.Z)) yield return q;
                    }
                }
                if (closed && i == segs - 1) yield break; // замыкание — флагом, а не повтором первой точки
                yield return b;
            }
        }

        /// <summary>Точки дуги (без первой) от угла a0 на sweep радиан, с точностью _tol.</summary>
        private IEnumerable<Vec3> ArcPoints(double cx, double cy, double r, double a0, double sweep, double z)
        {
            var n = Segments(r, Math.Abs(sweep));
            for (var k = 1; k < n; k++)
            {
                var a = a0 + sweep * k / n;
                yield return new Vec3(cx + r * Math.Cos(a), cy + r * Math.Sin(a), z);
            }
        }

        /// <summary>Сколько отрезков на дугу, чтобы стрелка прогиба была не больше точности.</summary>
        private int Segments(double r, double sweep)
        {
            if (r < 1e-12) return 1;
            var step = r <= _tol ? Math.PI / 2 : 2 * Math.Acos(Math.Max(-1, 1 - _tol / r));
            return Math.Max(2, Math.Min(4096, (int)Math.Ceiling(sweep / Math.Max(step, 1e-6))));
        }

        private void Arc(DxfEntity e, Xform t)
        {
            var c = P(e, 10);
            var r = e.Num(40);
            if (r <= 0) return;
            double a0, sweep;
            var full = e.Type == "CIRCLE";
            if (full) { a0 = 0; sweep = 2 * Math.PI; }
            else
            {
                a0 = e.Num(50) * Math.PI / 180;
                var a1 = e.Num(51) * Math.PI / 180;
                sweep = a1 - a0;
                while (sweep <= 0) sweep += 2 * Math.PI;
            }
            var pts = new List<Vec3> { new Vec3(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0), c.Z) };
            pts.AddRange(ArcPoints(c.X, c.Y, r, a0, sweep, c.Z));
            if (!full) pts.Add(new Vec3(c.X + r * Math.Cos(a0 + sweep), c.Y + r * Math.Sin(a0 + sweep), c.Z));
            Add(t.Then(Xform.Ocs(e.Extrusion)), pts, full);
        }

        private void Ellipse(DxfEntity e, Xform t)
        {
            var c = P(e, 10);
            var major = P(e, 11);
            var ratio = e.Num(40, 1);
            double p0 = e.Num(41), p1 = e.Num(42, 2 * Math.PI);
            var sweep = p1 - p0;
            while (sweep <= 0) sweep += 2 * Math.PI;
            var minor = Vec3.Cross(e.Extrusion.Unit, major) * ratio;
            var n = Segments(major.Length, sweep);
            var pts = new List<Vec3>();
            for (var k = 0; k <= n; k++)
            {
                var a = p0 + sweep * k / n;
                pts.Add(c + major * Math.Cos(a) + minor * Math.Sin(a));
            }
            Add(t, pts, Math.Abs(sweep - 2 * Math.PI) < 1e-9);
        }

        private void Spline(DxfEntity e, Xform t)
        {
            var degree = e.Int(71, 3);
            var knots = Values(e, 40);
            var weights = Values(e, 41);
            var ctrl = Points(e, 10);
            var closed = (e.Int(70) & 1) != 0;
            if (ctrl.Count < 2)
            {
                // сплайн только по определяющим точкам — ломаная через них (редкость)
                var fit = Points(e, 11);
                Add(t, fit, closed);
                return;
            }
            if (knots.Count != ctrl.Count + degree + 1) knots = Uniform(ctrl.Count, degree);
            if (weights.Count != ctrl.Count) weights = Enumerable.Repeat(1.0, ctrl.Count).ToList();
            double u0 = knots[degree], u1 = knots[ctrl.Count];
            // густо: по 8 точек на управляющую, затем прореживание оставит нужное
            var n = Math.Max(16, Math.Min(4000, ctrl.Count * 8));
            var pts = new List<Vec3>(n + 1);
            for (var k = 0; k <= n; k++) pts.Add(DeBoor(degree, knots, ctrl, weights, u0 + (u1 - u0) * k / n));
            Add(t, pts, false);
        }

        private static List<double> Values(DxfEntity e, int code) => e.Pairs.Where(p => p.Key == code).Select(p => Dbl(p.Value)).ToList();

        private static List<Vec3> Points(DxfEntity e, int code)
        {
            var list = new List<Vec3>();
            for (var i = 0; i < e.Pairs.Count; i++)
            {
                if (e.Pairs[i].Key != code) continue;
                double x = Dbl(e.Pairs[i].Value), y = 0, z = 0;
                if (i + 1 < e.Pairs.Count && e.Pairs[i + 1].Key == code + 10) y = Dbl(e.Pairs[i + 1].Value);
                if (i + 2 < e.Pairs.Count && e.Pairs[i + 2].Key == code + 20) z = Dbl(e.Pairs[i + 2].Value);
                list.Add(new Vec3(x, y, z));
            }
            return list;
        }

        private static List<double> Uniform(int count, int degree)
        {
            var k = new List<double>();
            for (var i = 0; i < count + degree + 1; i++) k.Add(i < degree + 1 ? 0 : i > count ? count - degree : i - degree);
            return k;
        }

        /// <summary>Точка рационального B-сплайна при параметре u (алгоритм де Бура).</summary>
        private static Vec3 DeBoor(int p, List<double> U, List<Vec3> P, List<double> W, double u)
        {
            var n = P.Count - 1;
            var k = p;
            while (k < n && u >= U[k + 1]) k++;
            var d = new double[p + 1, 4];
            for (var j = 0; j <= p; j++)
            {
                var idx = Math.Min(Math.Max(j + k - p, 0), n);
                var w = W[idx];
                d[j, 0] = P[idx].X * w; d[j, 1] = P[idx].Y * w; d[j, 2] = P[idx].Z * w; d[j, 3] = w;
            }
            for (var r = 1; r <= p; r++)
            for (var j = p; j >= r; j--)
            {
                var i = j + k - p;
                var den = U[Math.Min(i + p - r + 1, U.Count - 1)] - U[Math.Max(i, 0)];
                var a = Math.Abs(den) < 1e-15 ? 0 : (u - U[Math.Max(i, 0)]) / den;
                for (var c = 0; c < 4; c++) d[j, c] = (1 - a) * d[j - 1, c] + a * d[j, c];
            }
            var ww = Math.Abs(d[p, 3]) < 1e-15 ? 1 : d[p, 3];
            return new Vec3(d[p, 0] / ww, d[p, 1] / ww, d[p, 2] / ww);
        }

        /// <summary>
        /// Штриховка — её контуры (в OCS штриховки, на высоте её точки 10): ломаные с выпуклостью и края — отрезки, дуги,
        /// эллипсы. Сплайновые края — ломаной по управляющим точкам (в подписях их не встречалось).
        /// </summary>
        private void Hatch(DxfEntity e, Xform t)
        {
            Hatches++;
            var tr = t.Then(Xform.Ocs(e.Extrusion));
            var elev = e.Num(30);
            var pairs = e.Pairs;
            var i = pairs.FindIndex(p => p.Key == 91);
            if (i < 0) return;
            var paths = (int)Dbl(pairs[i].Value);
            i++;
            for (var path = 0; path < paths && i < pairs.Count; path++)
            {
                while (i < pairs.Count && pairs[i].Key != 92) i++;
                if (i >= pairs.Count) return;
                var type = (int)Dbl(pairs[i].Value);
                i++;
                var loop = new List<Vec3>();
                if ((type & 2) != 0)
                {
                    // ломаная: 72 — есть выпуклости, 73 — замкнута, 93 — вершин, затем 10/20(/42)
                    var hasBulge = Next(pairs, ref i, 72) != 0;
                    Next(pairs, ref i, 73);
                    var count = (int)Next(pairs, ref i, 93);
                    var pts = new List<Vec3>();
                    var bulges = new List<double>();
                    for (var v = 0; v < count; v++)
                    {
                        var x = Next(pairs, ref i, 10);
                        var y = Next(pairs, ref i, 20);
                        pts.Add(new Vec3(x, y, elev));
                        bulges.Add(hasBulge && i < pairs.Count && pairs[i].Key == 42 ? Dbl(pairs[i++].Value) : 0);
                    }
                    loop.AddRange(Bulged(pts, bulges, true));
                }
                else
                {
                    var edges = (int)Next(pairs, ref i, 93);
                    for (var k = 0; k < edges; k++)
                    {
                        var edge = (int)Next(pairs, ref i, 72);
                        if (edge == 1)
                        {
                            loop.Add(new Vec3(Next(pairs, ref i, 10), Next(pairs, ref i, 20), elev));
                            loop.Add(new Vec3(Next(pairs, ref i, 11), Next(pairs, ref i, 21), elev));
                        }
                        else if (edge == 2)
                        {
                            double cx = Next(pairs, ref i, 10), cy = Next(pairs, ref i, 20), r = Next(pairs, ref i, 40);
                            double a0 = Next(pairs, ref i, 50) * Math.PI / 180, a1 = Next(pairs, ref i, 51) * Math.PI / 180;
                            var ccw = Next(pairs, ref i, 73) != 0;
                            var sweep = a1 - a0;
                            while (sweep <= 0) sweep += 2 * Math.PI;
                            // по часовой: углы отсчитаны по часовой — те же точки зеркально по оси X, обход назад
                            if (!ccw) { a0 = -a0; sweep = -sweep; }
                            loop.Add(new Vec3(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0), elev));
                            loop.AddRange(ArcPoints(cx, cy, r, a0, sweep, elev));
                            loop.Add(new Vec3(cx + r * Math.Cos(a0 + sweep), cy + r * Math.Sin(a0 + sweep), elev));
                        }
                        else if (edge == 3)
                        {
                            double cx = Next(pairs, ref i, 10), cy = Next(pairs, ref i, 20);
                            double mx = Next(pairs, ref i, 11), my = Next(pairs, ref i, 21), ratio = Next(pairs, ref i, 40);
                            double a0 = Next(pairs, ref i, 50) * Math.PI / 180, a1 = Next(pairs, ref i, 51) * Math.PI / 180;
                            Next(pairs, ref i, 73);
                            var sweep = a1 - a0;
                            while (sweep <= 0) sweep += 2 * Math.PI;
                            var n = Segments(Math.Sqrt(mx * mx + my * my), sweep);
                            for (var s = 0; s <= n; s++)
                            {
                                var a = a0 + sweep * s / n;
                                loop.Add(new Vec3(cx + mx * Math.Cos(a) - my * ratio * Math.Sin(a), cy + my * Math.Cos(a) + mx * ratio * Math.Sin(a), elev));
                            }
                        }
                        else if (edge == 4)
                        {
                            Next(pairs, ref i, 94); Next(pairs, ref i, 73); Next(pairs, ref i, 74);
                            var nk = (int)Next(pairs, ref i, 95);
                            var nc = (int)Next(pairs, ref i, 96);
                            for (var s = 0; s < nk; s++) Next(pairs, ref i, 40);
                            for (var s = 0; s < nc; s++)
                            {
                                var x = Next(pairs, ref i, 10);
                                var y = Next(pairs, ref i, 20);
                                if (i < pairs.Count && pairs[i].Key == 42) i++;
                                loop.Add(new Vec3(x, y, elev));
                            }
                        }
                    }
                }
                if (loop.Count >= 3) Add(tr, loop, true, HatchLoops);
                // ссылки на граничные объекты: 97 — сколько, затем 330
                var refs = i < pairs.Count && pairs[i].Key == 97 ? (int)Dbl(pairs[i++].Value) : 0;
                for (var s = 0; s < refs && i < pairs.Count && pairs[i].Key == 330; s++) i++;
            }
        }

        /// <summary>Следующее значение с этим кодом начиная с i (пропуская чужие коды); i — после него.</summary>
        private static double Next(List<KeyValuePair<int, string>> pairs, ref int i, int code)
        {
            while (i < pairs.Count && pairs[i].Key != code)
            {
                if (pairs[i].Key == 92 || pairs[i].Key == 75) return 0; // начался следующий контур или узор — значения нет
                i++;
            }
            return i < pairs.Count ? Dbl(pairs[i++].Value) : 0;
        }

        private static double Dbl(string s) =>
            double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
