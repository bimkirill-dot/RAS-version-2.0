using System;
using System.Collections.Generic;

namespace RASv2.BlockMirrorRotation.Logic
{
    /// <summary>
    /// Преобразование в плане: x' = A·x + B·y + C, y' = D·x + E·y + F; высота не меняется. «Отразить проект» Revit делает
    /// именно такое — какое, плагин узнаёт после команды по парам точек «до → после» (Fit). Без Revit API — с тестами.
    /// </summary>
    public sealed class PlanTransform
    {
        public double A { get; private set; } = 1;
        public double B { get; private set; }
        public double C { get; private set; }
        public double D { get; private set; }
        public double E { get; private set; } = 1;
        public double F { get; private set; }

        /// <summary>Наибольшее расхождение пар с найденным преобразованием (в единицах точек).</summary>
        public double Residual { get; private set; }

        public static PlanTransform Identity => new PlanTransform();

        public PlanTransform() { }

        public PlanTransform(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public void Apply(double x, double y, out double rx, out double ry)
        {
            rx = A * x + B * y + C;
            ry = D * x + E * y + F;
        }

        /// <summary>Направление (без сдвига).</summary>
        public void ApplyVector(double x, double y, out double rx, out double ry)
        {
            rx = A * x + B * y;
            ry = D * x + E * y;
        }

        public double Determinant => A * E - B * D;

        /// <summary>Без искажения: столбцы единичные и перпендикулярные (поворот или отражение).</summary>
        public bool IsRigid(double eps = 1e-6) =>
            Math.Abs(A * A + D * D - 1) < eps && Math.Abs(B * B + E * E - 1) < eps && Math.Abs(A * B + D * E) < eps;

        /// <summary>Отражение: без искажения и с переворотом (определитель −1).</summary>
        public bool IsMirror(double eps = 1e-6) => IsRigid(eps) && Determinant < 0;

        /// <summary>Ничего не поменялось (сдвиг меньше tolerance).</summary>
        public bool IsIdentity(double tolerance) =>
            Math.Abs(A - 1) < 1e-9 && Math.Abs(B) < 1e-9 && Math.Abs(D) < 1e-9 && Math.Abs(E - 1) < 1e-9 &&
            Math.Abs(C) < tolerance && Math.Abs(F) < tolerance;

        /// <summary>
        /// Преобразование по парам точек методом наименьших квадратов. Нужно не меньше трёх точек не на одной прямой — иначе
        /// null. Слишком малые поправки (1e-12) прижимаются к нулю, чтобы «ничего не поменялось» узнавалось точно.
        /// </summary>
        public static PlanTransform Fit(IList<PointPair> pairs)
        {
            if (pairs == null || pairs.Count < 3) return null;
            // точки сдвигаются к их среднему — так система не теряет точность на больших координатах
            double mx = 0, my = 0;
            foreach (var p in pairs) { mx += p.X; my += p.Y; }
            mx /= pairs.Count; my /= pairs.Count;
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0;
            double tx = 0, txx = 0, txy = 0, ty = 0, tyx = 0, tyy = 0;
            foreach (var p in pairs)
            {
                var x = p.X - mx;
                var y = p.Y - my;
                sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
                tx += p.ToX; txx += p.ToX * x; txy += p.ToX * y;
                ty += p.ToY; tyx += p.ToY * x; tyy += p.ToY * y;
            }
            var n = (double)pairs.Count;
            // матрица нормальных уравнений: [sxx sxy sx; sxy syy sy; sx sy n]
            var m = new[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
            var row1 = Solve(m, new[] { txx, txy, tx });
            var row2 = Solve(m, new[] { tyx, tyy, ty });
            if (row1 == null || row2 == null) return null;
            // обратно из сдвинутых координат: x' = a(x − mx) + b(y − my) + c
            var t = new PlanTransform(Clean(row1[0]), Clean(row1[1]), row1[2] - row1[0] * mx - row1[1] * my,
                                      Clean(row2[0]), Clean(row2[1]), row2[2] - row2[0] * mx - row2[1] * my);
            double worst = 0;
            foreach (var p in pairs)
            {
                t.Apply(p.X, p.Y, out var rx, out var ry);
                worst = Math.Max(worst, Math.Sqrt((rx - p.ToX) * (rx - p.ToX) + (ry - p.ToY) * (ry - p.ToY)));
            }
            t.Residual = worst;
            return t;
        }

        private static double Clean(double v)
        {
            if (Math.Abs(v) < 1e-12) return 0;
            if (Math.Abs(v - 1) < 1e-12) return 1;
            if (Math.Abs(v + 1) < 1e-12) return -1;
            return v;
        }

        /// <summary>Система 3×3 методом Гаусса; вырожденная (точки на одной прямой) — null.</summary>
        private static double[] Solve(double[,] matrix, double[] rhs)
        {
            var a = new double[3, 4];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++) a[i, j] = matrix[i, j];
                a[i, 3] = rhs[i];
            }
            var scale = 0.0;
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    scale = Math.Max(scale, Math.Abs(a[i, j]));
            if (scale == 0) return null;
            for (var col = 0; col < 3; col++)
            {
                var pivot = col;
                for (var r = col + 1; r < 3; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
                if (Math.Abs(a[pivot, col]) < scale * 1e-12) return null;
                if (pivot != col)
                    for (var j = 0; j < 4; j++) { var tmp = a[col, j]; a[col, j] = a[pivot, j]; a[pivot, j] = tmp; }
                for (var r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    var k = a[r, col] / a[col, col];
                    for (var j = col; j < 4; j++) a[r, j] -= k * a[col, j];
                }
            }
            return new[] { a[0, 3] / a[0, 0], a[1, 3] / a[1, 1], a[2, 3] / a[2, 2] };
        }
    }

    /// <summary>Точка элемента модели до и после команды Revit.</summary>
    public struct PointPair
    {
        public double X, Y, ToX, ToY;

        public PointPair(double x, double y, double toX, double toY)
        {
            X = x; Y = y; ToX = toX; ToY = toY;
        }
    }
}
