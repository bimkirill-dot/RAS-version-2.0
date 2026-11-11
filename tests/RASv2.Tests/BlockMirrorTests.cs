using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.BlockMirrorRotation.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Block Mirror Rotation: отражение по точкам, имена осей, кружки, нулевые сегменты размеров.</summary>
    internal static class BlockMirrorTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) < eps;

        private static List<PointPair> Pairs(Func<double, double, (double, double)> f, params (double x, double y)[] points) =>
            points.Select(p =>
            {
                var (tx, ty) = f(p.x, p.y);
                return new PointPair(p.x, p.y, tx, ty);
            }).ToList();

        private static readonly (double, double)[] Block = { (1000, 2000), (1060, 2000), (1060, 2045), (1000, 2045), (1030, 2020) };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("отражение лево ↔ право относительно оси x = 50 находится по точкам", () =>
            {
                var t = PlanTransform.Fit(Pairs((x, y) => (100 - x, y), Block));
                Check(t != null && t.IsMirror() && t.Residual < 1e-6, "не отражение: " + t?.Determinant + ", " + t?.Residual);
                t.Apply(10, 7, out var x2, out var y2);
                Check(Near(x2, 90) && Near(y2, 7), $"10,7 → {x2},{y2}");
            });
            yield return Case("отражение по наклонной оси (северо-восток) — тоже отражение", () =>
            {
                var t = PlanTransform.Fit(Pairs((x, y) => (y + 5, x - 5), Block));
                Check(t != null && t.IsMirror() && t.Residual < 1e-6, "не отражение");
            });
            yield return Case("ничего не поменялось — тождество, не отражение", () =>
            {
                var t = PlanTransform.Fit(Pairs((x, y) => (x, y), Block));
                Check(t != null && t.IsIdentity(1e-3) && !t.IsMirror(), "не тождество");
            });
            yield return Case("поворот на 90° — без искажения, но не отражение", () =>
            {
                var t = PlanTransform.Fit(Pairs((x, y) => (-y, x), Block));
                Check(t != null && t.IsRigid() && !t.IsMirror(), "поворот принят за отражение");
            });
            yield return Case("часть элементов сдвинулась иначе — большое расхождение", () =>
            {
                var pairs = Pairs((x, y) => (100 - x, y), Block);
                pairs[4] = new PointPair(pairs[4].X, pairs[4].Y, pairs[4].ToX + 3, pairs[4].ToY);
                var t = PlanTransform.Fit(pairs);
                Check(t != null && t.Residual > 0.5, "расхождение " + t?.Residual);
            });
            yield return Case("точки на одной прямой — отражение не найти", () =>
            {
                Check(PlanTransform.Fit(Pairs((x, y) => (100 - x, y), (0, 0), (1, 1), (2, 2), (5, 5))) == null, "нашлось по прямой");
                Check(PlanTransform.Fit(Pairs((x, y) => (100 - x, y), (0, 0), (1, 0))) == null, "нашлось по двум точкам");
            });

            yield return Case("оси: виды имён", () =>
            {
                Check(GridNaming.KindOf("12") == GridNameKind.Number && GridNaming.KindOf("Б") == GridNameKind.Letter &&
                      GridNaming.KindOf("АА") == GridNameKind.Letter && GridNaming.KindOf("1/1") == GridNameKind.Other &&
                      GridNaming.KindOf("А'") == GridNameKind.Other && GridNaming.KindOf("2а") == GridNameKind.Other, "виды имён");
            });
            yield return Case("оси после отражения лево ↔ право: цифры снова слева направо, буквы на месте", () =>
            {
                // было 1, 2, 3 слева направо; отразили — 1 справа
                var grids = new[]
                {
                    new GridInfo { Id = 1, Name = "1", X = 30, Vertical = true },
                    new GridInfo { Id = 2, Name = "2", X = 20, Vertical = true },
                    new GridInfo { Id = 3, Name = "3", X = 10, Vertical = true },
                    new GridInfo { Id = 4, Name = "А", Y = 0 },
                    new GridInfo { Id = 5, Name = "Б", Y = 8 },
                };
                var plan = GridNaming.Make(grids);
                Check(plan.Problems.Count == 0, string.Join("; ", plan.Problems));
                Check(plan.Renames.Count == 2 && plan.Renames[1] == "3" && plan.Renames[3] == "1" && !plan.Renames.ContainsKey(2), "цифры: " + Dump(plan));
            });
            yield return Case("оси после отражения верх ↔ низ: буквы снова снизу вверх, по алфавиту (Ж перед И, без З)", () =>
            {
                var grids = new[]
                {
                    new GridInfo { Id = 1, Name = "Е", Y = 30 },
                    new GridInfo { Id = 2, Name = "Ж", Y = 20 },
                    new GridInfo { Id = 3, Name = "И", Y = 10 },
                    new GridInfo { Id = 4, Name = "7", X = 0, Vertical = true },
                    new GridInfo { Id = 5, Name = "10", X = 5, Vertical = true },
                };
                var plan = GridNaming.Make(grids);
                Check(plan.Renames.Count == 2 && plan.Renames[1] == "И" && plan.Renames[3] == "Е", "буквы: " + Dump(plan));
            });
            yield return Case("оси: цифры по значению, не по тексту (2 перед 10)", () =>
            {
                var plan = GridNaming.Make(new[]
                {
                    new GridInfo { Id = 1, Name = "2", X = 10, Vertical = true },
                    new GridInfo { Id = 2, Name = "10", X = 0, Vertical = true },
                });
                Check(plan.Renames[2] == "2" && plan.Renames[1] == "10", Dump(plan));
            });
            yield return Case("оси: дробное имя — не переименовывать ничего, причина", () =>
            {
                var plan = GridNaming.Make(new[]
                {
                    new GridInfo { Id = 1, Name = "1", X = 10, Vertical = true },
                    new GridInfo { Id = 2, Name = "2", X = 0, Vertical = true },
                    new GridInfo { Id = 3, Name = "1/1", X = 5, Vertical = true },
                });
                Check(plan.Renames.Count == 0 && plan.Problems.Count == 1 && plan.Problems[0].Contains("«1/1»"), Dump(plan));
            });
            yield return Case("оси: цифровая горизонтальная — не переименовывать, причина", () =>
            {
                var plan = GridNaming.Make(new[] { new GridInfo { Id = 1, Name = "1", Y = 10 }, new GridInfo { Id = 2, Name = "2", X = 0, Vertical = true } });
                Check(plan.Renames.Count == 0 && plan.Problems.Count == 1, Dump(plan));
            });
            yield return Case("оси: буквы — короче раньше (Я перед АА)", () =>
            {
                Check(GridNaming.CompareLetters("Я", "АА") < 0 && GridNaming.CompareLetters("Б", "А") > 0 && GridNaming.CompareLetters("б", "Б") == 0, "порядок букв");
            });

            yield return Case("кружок снизу: отражение верх ↔ низ переставило концы — кружок снова на нижнем", () =>
            {
                // было: конец 0 внизу (y = 0) с кружком, конец 1 вверху; после — конец 0 вверху
                BubbleSides.Choose(true, false, 0, 30, 30, 0, out var b0, out var b1);
                Check(!b0 && b1, "кружок не на нижнем конце");
            });
            yield return Case("кружок слева: концы на прежних сторонах — кружок там же", () =>
            {
                BubbleSides.Choose(false, true, 10, 0, 10, 0, out var b0, out var b1);
                Check(!b0 && b1, "кружок переехал");
            });
            yield return Case("кружки на обоих концах или ни на одном — так же", () =>
            {
                BubbleSides.Choose(true, true, 0, 30, 30, 0, out var a0, out var a1);
                BubbleSides.Choose(false, false, 0, 30, 30, 0, out var n0, out var n1);
                Check(a0 && a1 && !n0 && !n1, "кружки поменялись");
            });

            yield return Case("размер: нулевые сегменты убираются, остальные ссылки остаются", () =>
            {
                var segments = new[] { 1.5, 0.0, 2.0, 0.0000001, 3.0 };
                Check(ZeroSegments.Any(segments, 1e-6), "нули не найдены");
                var keep = ZeroSegments.Keep(segments, 1e-6);
                Check(keep.SequenceEqual(new[] { 0, 1, 3, 5 }), "оставлены " + string.Join(",", keep));
                Check(!ZeroSegments.Any(new[] { 1.0, 2.0 }, 1e-6), "нули в целом размере");
            });
        }

        private static string Dump(GridNaming.Plan plan) =>
            string.Join(", ", plan.Renames.Select(kv => kv.Key + "→" + kv.Value)) + " | " + string.Join("; ", plan.Problems);
    }
}
