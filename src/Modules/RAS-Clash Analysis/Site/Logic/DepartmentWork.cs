using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Работа отделов для «Динамики» (30.10.2026, разработчик: «посматривать работу отделов»): сколько коллизий отдел отметил на
    /// сайте «Исправлена» или «Допущена» — нарастающим итогом к каждой дате. Коллизия у отдела — одна, сколько бы раз он её ни
    /// отмечал; засчитана, пока последняя отметка этого отдела — «Исправлена» или «Допущена» (вернул в «Активную» сам — снята;
    /// отметки других отделов и отчёты не влияют: подтвердилась ли — не важно). Без базы — тестами.
    /// </summary>
    public sealed class DepartmentWork
    {
        /// <summary>Отдел автора отметки — так он называется в «Без отдела» (автор без отдела, не найден или не записан).</summary>
        public const string NoDepartment = "";

        /// <summary>Отметки статусов на сайте: коллизия, её проверка, когда, какой статус, отдел автора (NoDepartment — без отдела).</summary>
        public List<(long ClashId, long TestId, DateTime Time, ClashState State, string Department)> Marks { get; } =
            new List<(long, long, DateTime, ClashState, string)>();

        /// <summary>Точка линии отдела: засчитано к концу дня; отмечено в этот день «Исправлена» и «Допущена» (коллизий).</summary>
        public sealed class Point
        {
            public int Total, Fixed, Accepted;
        }

        /// <summary>
        /// Линии отделов на дни days (по порядку): у каждого отдела, отмечавшего коллизии проверок filter (null — все), — точка на
        /// каждый день.
        /// </summary>
        public Dictionary<string, List<Point>> Lines(IReadOnlyList<DateTime> days, Func<long, bool> filter = null)
        {
            var result = new Dictionary<string, List<Point>>();
            var marks = Marks.Where(m => filter == null || filter(m.TestId)).GroupBy(m => m.Department ?? NoDepartment);
            foreach (var dept in marks)
            {
                var byClash = dept.GroupBy(m => m.ClashId).Select(g => g.OrderBy(m => m.Time).ToList()).ToList();
                var line = new List<Point>();
                foreach (var day in days.Select(d => d.Date))
                {
                    var p = new Point();
                    foreach (var list in byClash)
                    {
                        var last = list.LastOrDefault(m => m.Time.Date <= day);
                        if (last.Time == default(DateTime)) continue;
                        if (last.State != ClashState.Active) p.Total++;
                        // в этот день: последняя за день отметка отдела
                        if (last.Time.Date != day) continue;
                        if (last.State == ClashState.Fixed) p.Fixed++;
                        else if (last.State == ClashState.Accepted) p.Accepted++;
                    }
                    line.Add(p);
                }
                result[dept.Key] = line;
            }
            return result;
        }
    }
}
