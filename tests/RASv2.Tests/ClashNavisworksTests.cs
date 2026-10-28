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
                Check(c.Count == 21, "кандидатов 21: " + c.Count);
                Check(c.All(v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z) - 1) < 1e-9), "единичные");
                Check(c.Count(v => v.Z > 0) == 4 && c.Where(v => v.Z > 0).All(v => v.Preference < 0.6),
                    "4 взгляда чуть снизу (плита над стеной) — и они наименее привычные");
                Check(c.Where(v => v.Preference == 1.0).All(v => v.Elevation == 35), "самый привычный — 35° сверху");
            });
            yield return Case("виды: один элемент закрыл другой — такой вид проигрывает (числа проверки разработчика 23.10.2026)", () =>
            {
                // «Конфликт2»: сверху плита во весь кадр, колонны видно 3 точки из ~60; снизу — оба открыты целиком
                var top = ViewPick.Score(5600, 5600, 3, 60, 19200, 0.6);
                var below = ViewPick.Score(990, 990, 213, 213, 19200, 0.5);
                Check(below > top, $"снизу, оба открыты ({below:0.000}), лучше, чем сверху, колонна закрыта ({top:0.000})");
                Check(ViewPick.Score(900, 900, 0, 50, 19200, 1.0) < 0, "второго не видно вовсе — вид не годится");
            });
            yield return Case("виды: крошечный в кадре элемент не перевешивает заметный, хоть и чуть закрытый", () =>
            {
                var sliver = ViewPick.Score(800, 800, 3, 3, 19200, 1.0);     // открыт целиком, но 3 точки
                var visible = ViewPick.Score(800, 800, 400, 500, 19200, 1.0); // открыт на 80 %, 400 точек
                Check(visible > sliver, $"заметный ({visible:0.000}) лучше крошечного ({sliver:0.000})");
            });
            yield return Case("виды: «годится без перебора» — оба открыты на 90 % и не крошечные", () =>
            {
                Check(ViewPick.Good(990, 990, 213, 213, 19200), "оба открыты целиком");
                Check(ViewPick.Good(900, 1000, 450, 500, 19200), "оба открыты ровно на 90 %");
                Check(!ViewPick.Good(890, 1000, 500, 500, 19200), "первый открыт на 89 % — перебирать");
                Check(!ViewPick.Good(5600, 5600, 3, 60, 19200), "второй почти закрыт плитой — перебирать");
                Check(!ViewPick.Good(800, 800, 50, 50, 19200), "второй открыт, но 50 точек — крошечный, перебирать");
                Check(!ViewPick.Good(0, 0, 0, 0, 19200) && !ViewPick.Good(10, 10, 10, 10, 0), "элементов нет в кадре или нет картинки");
                Check(ViewPick.FirstRound == 8 && ViewPick.Candidates().Take(ViewPick.FirstRound).All(c => Math.Abs(c.Elevation - 35) < 1e-9),
                    "первый круг — 8 привычных ракурсов, 35° сверху");
            });
            yield return Case("виды: при равной видимости побеждает привычный ракурс", () =>
            {
                Check(ViewPick.Score(300, 300, 300, 300, 19200, 1.0) > ViewPick.Score(300, 300, 300, 300, 19200, 0.6), "35° сверху лучше, чем сверху");
            });
            yield return Case("виды: видимость — по четырём картинкам, цвета не важны", () =>
            {
                // строка из 6 пикселей: фон (с осью), плита (красная подсветка), колонна (зелёная), колонна за плитой
                const int bg = 0x6496E6, axis = 0xE61E1E, slab = 0xC83232, column = 0x32B432;
                var none = new[] { bg, axis, bg, bg, bg, bg };
                var onlyFirst = new[] { bg, axis, slab, slab, slab, bg };       // плита — пиксели 2–4
                var onlySecond = new[] { bg, axis, bg, column, column, column }; // колонна — 3–5
                var both = new[] { bg, axis, slab, slab, column, column };      // в 3 плита впереди, в 4 — колонна
                ViewPick.Visible(both, onlyFirst, onlySecond, none, out var first, out var second, out var firstAll, out var secondAll);
                Check(first == 2 && second == 2 && firstAll == 3 && secondAll == 3,
                    $"видно плиты 2 из 3, колонны 2 из 3: {first}/{firstAll}, {second}/{secondAll}");
                // колонна целиком за плитой — её не видно, ось (красная, как подсветка) элементом не считается
                ViewPick.Visible(onlyFirst, onlyFirst, new[] { bg, axis, bg, column, column, bg }, none, out first, out second, out firstAll, out secondAll);
                Check(first == 3 && second == 0 && secondAll == 2, $"колонна за плитой не видна: {first} / {second} из {secondAll}");
                Check(ViewPick.Same(0xC83232, 0xCC3030) && !ViewPick.Same(0xC83232, 0x32B432), "допуск на сглаживание — 12");
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
