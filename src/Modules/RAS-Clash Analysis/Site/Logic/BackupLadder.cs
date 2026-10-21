using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Какие копии базы хранить (DECISIONS.md модуля, «Копии базы — раз в день, лесенкой»): копия — одна на день, и только
    /// в день, когда база менялась. Хранятся: последние daily копий; из последних weekly недель — по последней копии недели;
    /// из последних monthly месяцев — по последней копии месяца. Счёт — по копиям, а не по календарю: сайт простоял неделю —
    /// старые копии не пропадают из-за того, что за неделю новых не было. Без файлов — проверяется тестами.
    /// </summary>
    public static class BackupLadder
    {
        public static ISet<DateTime> Keep(IEnumerable<DateTime> copies, int daily, int weekly, int monthly)
        {
            var days = copies.Select(d => d.Date).Distinct().OrderByDescending(d => d).ToList();
            var keep = new HashSet<DateTime>(days.Take(Math.Max(daily, 1)));
            foreach (var newest in days.GroupBy(WeekOf).Take(Math.Max(weekly, 0)).Select(g => g.First())) keep.Add(newest);
            foreach (var newest in days.GroupBy(d => new DateTime(d.Year, d.Month, 1)).Take(Math.Max(monthly, 0)).Select(g => g.First())) keep.Add(newest);
            return keep;
        }

        /// <summary>Понедельник недели дня.</summary>
        public static DateTime WeekOf(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }
}
