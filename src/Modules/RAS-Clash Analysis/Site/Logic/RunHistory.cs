using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Состояние коллизий на каждый прогон — для сводки проекта (DECISIONS.md модуля, «Сводка проекта»): тренд, сравнение
    /// прогонов, изменения в плитках. Восстанавливается по отчётам: на день D у проверки берётся её последний отчёт не позже
    /// D; коллизия в нём (не «Исправлено» Navisworks) — есть, её нет, а раньше была — исправлена, ещё не встречалась — не
    /// считается. «Допущена» — по нынешнему статусу. Последний прогон — точно по нынешним статусам. Без базы — тестами.
    /// </summary>
    public sealed class RunHistory
    {
        /// <summary>Отчёт: номер и день прогона.</summary>
        public List<(long Id, DateTime Day)> Reports { get; } = new List<(long, DateTime)>();
        /// <summary>Какие проверки были в отчёте.</summary>
        public List<(long ReportId, long TestId)> ReportTests { get; } = new List<(long, long)>();
        /// <summary>Коллизии: проверка и нынешний статус.</summary>
        public List<(long Id, long TestId, ClashState State)> Clashes { get; } = new List<(long, long, ClashState)>();
        /// <summary>Появления коллизии в отчётах; Present — не «Исправлено» Navisworks.</summary>
        public List<(long ClashId, long ReportId, bool Present)> Occurrences { get; } = new List<(long, long, bool)>();

        /// <summary>Дни прогонов проверок (filter — какие проверки учитывать; null — все), по порядку.</summary>
        public List<DateTime> Days(Func<long, bool> filter = null)
        {
            var day = Reports.ToDictionary(r => r.Id, r => r.Day.Date);
            return ReportTests.Where(x => (filter == null || filter(x.TestId)) && day.ContainsKey(x.ReportId))
                .Select(x => day[x.ReportId]).Distinct().OrderBy(d => d).ToList();
        }

        /// <summary>
        /// Статус каждой коллизии на день прогона (только те, что к этому дню уже встречались); filter — проверки (null — все).
        /// Последний день прогонов — нынешние статусы.
        /// </summary>
        public Dictionary<long, ClashState> StatesAt(DateTime day, Func<long, bool> filter = null)
        {
            day = day.Date;
            var reportDay = Reports.ToDictionary(r => r.Id, r => r.Day.Date);
            var last = Days(filter).DefaultIfEmpty(DateTime.MinValue).Max();
            // у проверки — последний отчёт не позже дня
            var testReport = new Dictionary<long, (DateTime Day, long Id)>();
            foreach (var (reportId, testId) in ReportTests)
            {
                if ((filter != null && !filter(testId)) || !reportDay.TryGetValue(reportId, out var d) || d > day) continue;
                if (!testReport.TryGetValue(testId, out var cur) || d > cur.Day || (d == cur.Day && reportId > cur.Id)) testReport[testId] = (d, reportId);
            }
            var occ = Occurrences.GroupBy(o => o.ClashId).ToDictionary(g => g.Key, g => g.ToList());
            var result = new Dictionary<long, ClashState>();
            foreach (var (id, testId, state) in Clashes)
            {
                if ((filter != null && !filter(testId)) || !testReport.TryGetValue(testId, out var rep)) continue;
                if (!occ.TryGetValue(id, out var list)) continue;
                var seen = list.Any(o => reportDay.TryGetValue(o.ReportId, out var d) && d <= day);
                if (!seen) continue;
                if (day >= last) { result[id] = state; continue; }
                var present = list.Any(o => o.ReportId == rep.Id && o.Present);
                result[id] = present ? (state == ClashState.Accepted ? ClashState.Accepted : ClashState.Active) : ClashState.Fixed;
            }
            return result;
        }

        /// <summary>
        /// Новые в прогоне дня: впервые есть в этот день, а у проверки уже были прогоны раньше (её первый прогон — не «новые»).
        /// </summary>
        public HashSet<long> NewAt(DateTime day, Func<long, bool> filter = null)
        {
            day = day.Date;
            var reportDay = Reports.ToDictionary(r => r.Id, r => r.Day.Date);
            var testFirst = ReportTests.Where(x => reportDay.ContainsKey(x.ReportId)).GroupBy(x => x.TestId)
                .ToDictionary(g => g.Key, g => g.Min(x => reportDay[x.ReportId]));
            var clashTest = Clashes.ToDictionary(c => c.Id, c => c.TestId);
            return new HashSet<long>(Occurrences.GroupBy(o => o.ClashId)
                .Where(g => clashTest.TryGetValue(g.Key, out var t) && (filter == null || filter(t))
                            && testFirst.TryGetValue(t, out var first) && first < day
                            && g.Where(o => reportDay.ContainsKey(o.ReportId)).Select(o => reportDay[o.ReportId]).DefaultIfEmpty(DateTime.MaxValue).Min() == day)
                .Select(g => g.Key));
        }
    }
}
