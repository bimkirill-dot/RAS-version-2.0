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

        /// <summary>
        /// «Мост прогона»: сколько активных было к прошлому прогону, что добавилось и что ушло, сколько стало
        /// (Was + New + NewTests + Returned + Unaccepted − Fixed − Accepted − Gone = Now).
        /// </summary>
        public RunChange Change(DateTime before, DateTime day, Func<long, bool> filter = null)
        {
            var was = StatesAt(before, filter);
            var now = StatesAt(day, filter);
            var fresh = NewAt(day, filter);
            var c = new RunChange { Before = before.Date, Day = day.Date };
            foreach (var id in was.Keys.Union(now.Keys))
            {
                var hadP = was.TryGetValue(id, out var p);
                var hasN = now.TryGetValue(id, out var n);
                var wasActive = hadP && p == ClashState.Active;
                var isActive = hasN && n == ClashState.Active;
                if (wasActive) c.Was++;
                if (isActive) c.Now++;
                if (wasActive && !isActive)
                {
                    if (!hasN) c.Gone++;
                    else if (n == ClashState.Fixed) c.Fixed++;
                    else c.Accepted++;
                }
                else if (!wasActive && isActive)
                {
                    if (!hadP) { if (fresh.Contains(id)) c.New++; else c.NewTests++; }
                    else if (p == ClashState.Fixed) c.Returned++;
                    else c.Unaccepted++;
                }
            }
            return c;
        }

        /// <summary>Изменения от прогона к прогону (со второго прогона).</summary>
        public List<RunChange> Changes(Func<long, bool> filter = null)
        {
            var days = Days(filter);
            var list = new List<RunChange>();
            for (var i = 1; i < days.Count; i++) list.Add(Change(days[i - 1], days[i], filter));
            return list;
        }

        /// <summary>
        /// Исправленные сейчас коллизии: день, когда встретилась впервые, и прогон, которым исправлена (последний переход
        /// «активная → исправлена»; вернулась и снова исправлена — по последнему).
        /// </summary>
        public Dictionary<long, (DateTime First, DateTime Fixed)> FixTimes(Func<long, bool> filter = null)
        {
            var days = Days(filter);
            var first = new Dictionary<long, DateTime>();
            var fixedOn = new Dictionary<long, DateTime>();
            Dictionary<long, ClashState> prev = null, cur = null;
            foreach (var day in days)
            {
                cur = StatesAt(day, filter);
                foreach (var pair in cur)
                {
                    if (!first.ContainsKey(pair.Key)) first[pair.Key] = day;
                    if (pair.Value == ClashState.Fixed && prev != null && prev.TryGetValue(pair.Key, out var p) && p == ClashState.Active) fixedOn[pair.Key] = day;
                }
                prev = cur;
            }
            var result = new Dictionary<long, (DateTime, DateTime)>();
            if (cur == null) return result;
            foreach (var pair in fixedOn)
                if (cur.TryGetValue(pair.Key, out var s) && s == ClashState.Fixed) result[pair.Key] = (first[pair.Key], pair.Value);
            return result;
        }

        /// <summary>
        /// Прогноз по последним прогонам (window): изменение активных за прогон без новых проверок (их появление — разовое),
        /// дней между прогонами и день, когда активных станет 0 при той же скорости; не убывают — Zero = null.
        /// </summary>
        public static (DateTime? Zero, double PerRun, double DaysPerRun) Forecast(IReadOnlyList<RunChange> changes, int activeNow, int window = 3)
        {
            if (changes == null || changes.Count == 0) return (null, 0, 0);
            var last = changes.Skip(Math.Max(0, changes.Count - window)).ToList();
            var perRun = last.Average(c => (double)(c.New + c.Returned + c.Unaccepted - c.Fixed - c.Accepted - c.Gone));
            var daysPerRun = (last[last.Count - 1].Day - last[0].Before).TotalDays / last.Count;
            var lastDay = last[last.Count - 1].Day;
            if (activeNow <= 0) return (lastDay, perRun, daysPerRun);
            if (perRun >= 0 || daysPerRun <= 0) return (null, perRun, daysPerRun);
            var runs = Math.Ceiling(activeNow / -perRun);
            return (lastDay.AddDays(Math.Round(runs * daysPerRun)), perRun, daysPerRun);
        }
    }

    /// <summary>Что изменилось в активных от прогона Before к прогону Day.</summary>
    public sealed class RunChange
    {
        public DateTime Before, Day;
        /// <summary>Активных к прошлому прогону и сейчас.</summary>
        public int Was, Now;
        /// <summary>Пришли: новые (у проверки были прогоны раньше), в новых проверках (первый прогон проверки), вернулись после исправления, сняли «допущена».</summary>
        public int New, NewTests, Returned, Unaccepted;
        /// <summary>Ушли: исправлены, допущены, пропали (нет данных на этот прогон).</summary>
        public int Fixed, Accepted, Gone;
        public int Appeared => New + NewTests + Returned + Unaccepted;
    }
}
