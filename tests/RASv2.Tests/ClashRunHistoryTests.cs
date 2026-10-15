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
        }
    }
}
