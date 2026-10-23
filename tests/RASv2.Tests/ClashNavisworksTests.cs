using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Navisworks.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis в Navisworks: подбор вида коллизии (ViewPick).</summary>
    internal static class ClashNavisworksTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("виды: направления — единичные, смотрят вниз или вбок, привычный ракурс — наискось сверху", () =>
            {
                var c = ViewPick.Candidates();
                Check(c.Count == 17, "кандидатов 17: " + c.Count);
                Check(c.All(v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z) - 1) < 1e-9), "единичные");
                Check(c.All(v => v.Z < 0), "камера всегда смотрит сверху вниз (снизу модель не показываем)");
                Check(c.Where(v => v.Preference == 1.0).All(v => v.Elevation == 35), "самый привычный — 35° сверху");
            });
            yield return Case("виды: один элемент закрыл другой — такой вид проигрывает", () =>
            {
                // 2000 пикселей картинки: в первом виде второго элемента почти нет, во втором — оба видны поровну
                var hidden = ViewPick.Score(900, 10, 2000, 1.0);
                var both = ViewPick.Score(400, 350, 2000, 0.6);
                Check(both > hidden, $"оба видны ({both:0.000}) лучше, чем один закрыл другой ({hidden:0.000})");
                Check(ViewPick.Score(900, 0, 2000, 1.0) < 0, "второго не видно вовсе — вид не годится");
            });
            yield return Case("виды: при равной видимости побеждает привычный ракурс", () =>
            {
                Check(ViewPick.Score(300, 300, 2000, 1.0) > ViewPick.Score(300, 300, 2000, 0.6), "35° сверху лучше, чем сверху");
            });
            yield return Case("виды: цвета элементов — с учётом освещения и тени", () =>
            {
                Check(ViewPick.IsFirst(200, 30, 30) && ViewPick.IsFirst(110, 40, 35), "красный — на свету и в тени");
                Check(ViewPick.IsSecond(30, 190, 40) && ViewPick.IsSecond(25, 95, 30), "зелёный — на свету и в тени");
                Check(!ViewPick.IsFirst(180, 180, 180) && !ViewPick.IsSecond(180, 180, 180) && !ViewPick.IsFirst(40, 10, 10),
                    "серый фон и почти чёрное — не элементы");
            });
            yield return Case("виды: что показать — место коллизии с запасом, а не стена целиком", () =>
            {
                // стена 10 × 0,3 × 3 м, труба через неё длиной 4 м
                var wall = new Box3(0, 0, 0, 10, 0.3, 3);
                var pipe = new Box3(4.9, -2, 1.4, 5.1, 2.3, 1.6);
                var z = ViewPick.Zone(wall, pipe);
                Check(z.MinX < 4.9 && z.MaxX > 5.1 && z.MinY < 0 && z.MaxY > 0.3, "место пересечения внутри");
                Check(z.MaxX - z.MinX < 10, "не вся стена: " + (z.MaxX - z.MinX).ToString("0.00"));
                // элементы не касаются: зазор 0,5 м — показываем промежуток с запасом
                var gap = ViewPick.Zone(new Box3(0, 0, 0, 1, 1, 1), new Box3(1.5, 0, 0, 2.5, 1, 1));
                Check(gap.MinX < 1 && gap.MaxX > 1.5, "промежуток между элементами внутри");
            });
        }
    }
}
