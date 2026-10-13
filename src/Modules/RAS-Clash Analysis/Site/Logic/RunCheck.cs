using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Проверки, которых нет в последнем прогоне своего блока и критичности (DECISIONS.md модуля, «Нет отчёта в прогоне»).
    /// Договорились выгружать все проверки, даже пустые: отчёта нет — ошибка выгрузки, а не «всё исправлено». Сайт
    /// предупреждает; статусы коллизий таких проверок не меняет. Без базы — проверяется тестами.
    /// </summary>
    public static class RunCheck
    {
        /// <summary>Последний прогон (день) каждого блока и критичности — по датам отчётов.</summary>
        public static Dictionary<(string Criticality, string Block), DateTime> LastRuns(IEnumerable<(string Criticality, string Block, DateTime Date)> reports) =>
            reports.GroupBy(r => (r.Criticality ?? "", r.Block ?? ""))
                .ToDictionary(g => g.Key, g => g.Max(r => r.Date).Date);

        /// <summary>Проверки, чей последний отчёт старше последнего прогона их блока и критичности, — с днём этого прогона.</summary>
        public static List<(T Test, DateTime Run)> Missing<T>(IEnumerable<T> tests, Func<T, string> criticality, Func<T, string> block,
            Func<T, DateTime?> lastReport, IEnumerable<(string Criticality, string Block, DateTime Date)> reports)
        {
            var runs = LastRuns(reports);
            var list = new List<(T, DateTime)>();
            foreach (var t in tests)
            {
                if (!runs.TryGetValue((criticality(t) ?? "", block(t) ?? ""), out var run)) continue;
                var last = lastReport(t);
                if (last == null || last.Value.Date < run) list.Add((t, run));
            }
            return list;
        }
    }
}
