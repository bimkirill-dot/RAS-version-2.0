using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis, сайт: какие копии базы хранить (BackupLadder).</summary>
    internal static class SiteBackupTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static DateTime D(int month, int day) => new DateTime(2026, month, day);

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("копии: последние ежедневные — все", () =>
            {
                var copies = Enumerable.Range(0, 5).Select(i => D(10, 21).AddDays(-i)).ToList();
                Check(BackupLadder.Keep(copies, 14, 8, 12).Count == 5, "5 копий — все 5");
            });
            yield return Case("копии: старше ежедневных — последняя копия недели и последняя копия месяца", () =>
            {
                // каждый день с 1 июля по 21 октября
                var copies = new List<DateTime>();
                for (var d = D(7, 1); d <= D(10, 21); d = d.AddDays(1)) copies.Add(d);
                var keep = BackupLadder.Keep(copies, 3, 2, 3);
                Check(keep.Contains(D(10, 21)) && keep.Contains(D(10, 20)) && keep.Contains(D(10, 19)), "три последних дня");
                // неделя 19–25.10 — уже есть (21.10); прошлая неделя 12–18.10 — её воскресенье
                Check(keep.Contains(D(10, 18)), "последняя копия прошлой недели");
                Check(!keep.Contains(D(10, 11)), "позапрошлая неделя — нет (недель 2)");
                // месяцы: октябрь (21.10), сентябрь (30.09), август (31.08); июля — нет
                Check(keep.Contains(D(9, 30)) && keep.Contains(D(8, 31)) && !keep.Contains(D(7, 31)), "последняя копия месяца, месяцев 3");
                Check(keep.Count == 6, "всего 6: " + string.Join(", ", keep.OrderBy(d => d).Select(d => d.ToString("dd.MM"))));
            });
            yield return Case("копии: дни без изменений — копий нет, счёт по копиям, а не по календарю", () =>
            {
                // сайт простоял: копии 1, 2 и 3 сентября, потом — только 21 октября
                var keep = BackupLadder.Keep(new[] { D(9, 1), D(9, 2), D(9, 3), D(10, 21) }, 3, 0, 0);
                Check(keep.SetEquals(new[] { D(9, 2), D(9, 3), D(10, 21) }), "три последних копии, хотя между ними полтора месяца");
            });
            yield return Case("копии: неделя — с понедельника", () =>
            {
                Check(BackupLadder.WeekOf(D(10, 21)) == D(10, 19) && BackupLadder.WeekOf(D(10, 25)) == D(10, 19) && BackupLadder.WeekOf(D(10, 26)) == D(10, 26),
                    "среда и воскресенье — неделя с 19.10; понедельник — своя");
            });
        }
    }
}
