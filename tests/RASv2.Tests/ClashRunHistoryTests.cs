using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: состояние коллизий на прогоны — для сводки (RunHistory).</summary>
    internal static class ClashRunHistoryTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly DateTime D1 = new DateTime(2026, 10, 1), D2 = new DateTime(2026, 10, 8), D3 = new DateTime(2026, 10, 14);

        /// <summary>
        /// Проверка 1: отчёты 01, 08, 14. Коллизия 1 — во всех (активная); 2 — только 01 (исправлена к 08); 3 — с 08 (новая);
        /// 4 — 01 и 14, нет в 08 (вернулась); 5 — 01, 08, допущена. Проверка 2 — только 14 (первый прогон — не «новые»), коллизия 6.
        /// </summary>
        private static RunHistory Sample()
        {
            var h = new RunHistory();
            h.Reports.AddRange(new[] { (11L, D1), (12L, D2), (13L, D3), (21L, D3) });
            h.ReportTests.AddRange(new[] { (11L, 1L), (12L, 1L), (13L, 1L), (21L, 2L) });
            h.Clashes.AddRange(new[] { (1L, 1L, ClashState.Active), (2L, 1L, ClashState.Fixed), (3L, 1L, ClashState.Active),
                                       (4L, 1L, ClashState.Active), (5L, 1L, ClashState.Accepted), (6L, 2L, ClashState.Active) });
            h.Occurrences.AddRange(new[]
            {
                (1L, 11L, true), (1L, 12L, true), (1L, 13L, true),
                (2L, 11L, true),
                (3L, 12L, true), (3L, 13L, true),
                (4L, 11L, true), (4L, 13L, true),
                (5L, 11L, true), (5L, 12L, true),
                (6L, 21L, true),
            });
            return h;
        }

        private static int Count(Dictionary<long, ClashState> s, ClashState state) => s.Values.Count(v => v == state);

        /// <summary>
        /// Статусы сайта по журналу (одна проверка, отчёты 01, 08, 14): 1 — есть везде, допущена 09.10; 2 — есть везде, отмечена
        /// исправленной 15.10 (ещё в отчёте 14); 3 — новая 14, допущена 15.10; 4 — есть 01 и 08, отмечена исправленной 03.10,
        /// а в 08 снова есть (вернулась), сейчас активная; 5 — есть 01, нет в 08 (исправлена отчётом).
        /// </summary>
        private static RunHistory Marked()
        {
            var h = new RunHistory();
            h.Reports.AddRange(new[] { (11L, D1), (12L, D2), (13L, D3) });
            h.ReportTests.AddRange(new[] { (11L, 1L), (12L, 1L), (13L, 1L) });
            h.Clashes.AddRange(new[] { (1L, 1L, ClashState.Accepted), (2L, 1L, ClashState.Fixed), (3L, 1L, ClashState.Accepted),
                                       (4L, 1L, ClashState.Active), (5L, 1L, ClashState.Fixed) });
            h.Occurrences.AddRange(new[]
            {
                (1L, 11L, true), (1L, 12L, true), (1L, 13L, true),
                (2L, 11L, true), (2L, 12L, true), (2L, 13L, true),
                (3L, 13L, true),
                (4L, 11L, true), (4L, 12L, true), (4L, 13L, true),
                (5L, 11L, true),
            });
            h.ManualStates.AddRange(new[]
            {
                (1L, new DateTime(2026, 10, 9, 10, 0, 0), ClashState.Accepted),
                (2L, new DateTime(2026, 10, 15, 10, 0, 0), ClashState.Fixed),
                (3L, new DateTime(2026, 10, 15, 11, 0, 0), ClashState.Accepted),
                (4L, new DateTime(2026, 10, 3, 9, 0, 0), ClashState.Fixed),
            });
            return h;
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("дни прогонов — по отчётам проверок", () =>
            {
                Check(string.Join(",", Sample().Days().Select(d => d.Day)) == "1,8,14", "все");
                Check(string.Join(",", Sample().Days(t => t == 2).Select(d => d.Day)) == "14", "только проверка 2");
            });
            yield return Case("состояние на прошлые прогоны — по отчётам", () =>
            {
                var h = Sample();
                var s1 = h.StatesAt(D1);
                Check(s1.Count == 4 && Count(s1, ClashState.Active) == 3 && Count(s1, ClashState.Accepted) == 1, "01: 1,2,4 активны, 5 допущена — " + string.Join(" ", s1));
                var s2 = h.StatesAt(D2);
                Check(s2[2] == ClashState.Fixed && s2[4] == ClashState.Fixed && s2[3] == ClashState.Active && s2[1] == ClashState.Active, "08: 2 и 4 исправлены, 3 появилась");
                Check(!s2.ContainsKey(6), "08: проверки 2 ещё не было");
            });
            yield return Case("последний прогон — нынешние статусы", () =>
            {
                var s3 = Sample().StatesAt(D3);
                Check(s3[4] == ClashState.Active && s3[5] == ClashState.Accepted && s3[2] == ClashState.Fixed && s3[6] == ClashState.Active, "14: " + string.Join(" ", s3));
            });
            yield return Case("новые — не в первом прогоне проверки", () =>
            {
                var h = Sample();
                Check(h.NewAt(D1).Count == 0, "01 — первый прогон");
                Check(h.NewAt(D2).SetEquals(new[] { 3L }), "08: новая 3");
                Check(h.NewAt(D3).Count == 0, "14: 6 — первый прогон своей проверки, не новая");
            });
            yield return Case("мост прогона: было + пришли − ушли = стало", () =>
            {
                var h = Sample();
                var a = h.Change(D1, D2);
                Check(a.Was == 3 && a.New == 1 && a.Fixed == 2 && a.Now == 2 && a.Returned == 0 && a.NewTests == 0,
                      $"01→08: было {a.Was}, новые {a.New}, исправлены {a.Fixed}, стало {a.Now}");
                var b = h.Change(D2, D3);
                Check(b.Was == 2 && b.Returned == 1 && b.NewTests == 1 && b.New == 0 && b.Fixed == 0 && b.Now == 4,
                      $"08→14: было {b.Was}, вернулись {b.Returned}, в новых проверках {b.NewTests}, стало {b.Now}");
                foreach (var c in h.Changes())
                    Check(c.Was + c.Appeared - c.Fixed - c.Accepted - c.Gone == c.Now, "сходится " + c.Day.Day);
            });
            yield return Case("статусы сайта — с того дня, когда поставлены, а не задним числом", () =>
            {
                var h = Marked();
                var s2 = h.StatesAt(D2);
                Check(s2[1] == ClashState.Active, "1: допущена 09.10 — на 08.10 ещё активная");
                Check(h.StatesAt(new DateTime(2026, 10, 5))[4] == ClashState.Fixed, "4: отмечена исправленной 03.10 — на 05.10 исправлена");
                Check(s2[4] == ClashState.Active, "4: в отчёте 08.10 снова есть — вернулась");
                Check(s2[5] == ClashState.Fixed && !h.PresentAt(D2).Contains(5), "5: нет в отчёте 08.10 — исправлена отчётом");
                var s3 = h.StatesAt(D3);
                Check(s3[1] == ClashState.Accepted && s3[2] == ClashState.Fixed && h.PresentAt(D3).Contains(2), "14 (сейчас): 1 допущена, 2 отмечена, ещё в отчёте");
            });
            yield return Case("мост: новая и сразу допущенная — пришла и ушла; отмеченная на сайте — отдельно", () =>
            {
                var c = Marked().Change(D2, D3);
                Check(c.Was == 3 && c.New == 1 && c.Accepted == 2 && c.FixedManual == 1 && c.Fixed == 0 && c.Now == 1,
                      $"08→14: было {c.Was} (1, 2, 4), новые {c.New} (3), допущены {c.Accepted} (1, 3), отмечены {c.FixedManual} (2), стало {c.Now} (4)");
                Check(c.Was + c.Appeared - c.Went == c.Now, "сходится");
                Check(c.Entered[3] == RunChange.NewKind && c.Left[3] == RunChange.AcceptedKind && c.Left[2] == RunChange.FixedManualKind, "виды по коллизиям");
                var a = Marked().Change(D1, D2);
                Check(a.Was == 4 && a.Fixed == 1 && a.Now == 3 && a.Returned == 0, $"01→08: 5 исправлена отчётом, 4 активна в обе даты: {a.Was}→{a.Now}");
            });
            yield return Case("мост через прогон: появившаяся в промежуточном — новая, а не «в новых проверках»", () =>
            {
                var c = Sample().Change(D1, D3);
                Check(c.Was == 3 && c.New == 1 && c.NewTests == 1 && c.Fixed == 1 && c.Returned == 0 && c.Now == 4,
                      $"01→14: было {c.Was}, новые {c.New} (3), в новых проверках {c.NewTests} (6), исправлены {c.Fixed} (2), стало {c.Now}");
            });
        }
    }
}
