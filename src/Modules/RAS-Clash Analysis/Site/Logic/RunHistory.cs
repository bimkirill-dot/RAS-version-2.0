using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Состояние коллизий на каждую дату отчётов — для сводки проекта (DECISIONS.md модуля, «Сводка проекта»): тренд, сравнение
    /// отчётов, изменения в плитках. Есть ли коллизия — по отчётам: на день D у проверки берётся её последний отчёт не позже D;
    /// коллизия в нём (не «Исправлено» Navisworks) — есть, её нет, а раньше была — исправлена (подтверждено отчётом), ещё не
    /// встречалась — не считается. Статусы сайта — по журналу, с того дня, когда их поставили (не задним числом, 19.10.2026):
    /// «Допущена» держится, пока коллизия есть в отчётах; «Исправлена», отмеченная на сайте, — до следующего отчёта, где коллизия
    /// снова есть (тогда — вернулась). Последний день отчётов — нынешние статусы («сейчас»). Без базы — тестами.
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
        /// <summary>Статусы, поставленные на сайте (журнал): когда и какой.</summary>
        public List<(long ClashId, DateTime Time, ClashState State)> ManualStates { get; } = new List<(long, DateTime, ClashState)>();

        /// <summary>Дни прогонов проверок (filter — какие проверки учитывать; null — все), по порядку.</summary>
        public List<DateTime> Days(Func<long, bool> filter = null)
        {
            var day = Reports.ToDictionary(r => r.Id, r => r.Day.Date);
            return ReportTests.Where(x => (filter == null || filter(x.TestId)) && day.ContainsKey(x.ReportId))
                .Select(x => day[x.ReportId]).Distinct().OrderBy(d => d).ToList();
        }

        /// <summary>
        /// Статус каждой коллизии на день (только те, что к этому дню уже встречались); filter — проверки (null — все).
        /// Последний день прогонов — нынешние статусы.
        /// </summary>
        public Dictionary<long, ClashState> StatesAt(DateTime day, Func<long, bool> filter = null) =>
            Snapshot(day, filter).ToDictionary(x => x.Key, x => x.Value.State);

        /// <summary>
        /// Коллизии, которые на день есть в последнем отчёте своей проверки: «Исправлена» у такой — отмечена на сайте и ещё
        /// не подтверждена отчётом.
        /// </summary>
        public HashSet<long> PresentAt(DateTime day, Func<long, bool> filter = null) =>
            new HashSet<long>(Snapshot(day, filter).Where(x => x.Value.Present).Select(x => x.Key));

        private Dictionary<long, (ClashState State, bool Present)> Snapshot(DateTime day, Func<long, bool> filter)
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
            var manual = ManualStates.GroupBy(m => m.ClashId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Time).ToList());
            var result = new Dictionary<long, (ClashState, bool)>();
            foreach (var (id, testId, state) in Clashes)
            {
                if ((filter != null && !filter(testId)) || !testReport.TryGetValue(testId, out var rep)) continue;
                if (!occ.TryGetValue(id, out var list)) continue;
                var seen = list.Any(o => reportDay.TryGetValue(o.ReportId, out var d) && d <= day);
                if (!seen) continue;
                var present = list.Any(o => o.ReportId == rep.Id && o.Present);
                if (day >= last) { result[id] = (state, present); continue; }
                if (!present) { result[id] = (ClashState.Fixed, false); continue; }
                // есть в отчёте: статус сайта, поставленный не позже дня
                var marks = manual.TryGetValue(id, out var m) ? m : null;
                (long ClashId, DateTime Time, ClashState State)? mark = null;
                if (marks != null)
                    foreach (var x in marks)
                        if (x.Time.Date <= day) mark = x;
                ClashState at;
                if (mark == null || mark.Value.State == ClashState.Active)
                    // статусов на сайте не ставили, а сейчас «Допущена» — так пришло из отчёта (до 15.10.2026 «Подтверждено» Navisworks)
                    at = mark == null && marks == null && state == ClashState.Accepted ? ClashState.Accepted : ClashState.Active;
                else if (mark.Value.State == ClashState.Accepted) at = ClashState.Accepted;
                // «Исправлена» на сайте — пока не пришёл отчёт, где коллизия снова есть
                else at = mark.Value.Time.Date >= rep.Day ? ClashState.Fixed : ClashState.Active;
                result[id] = (at, true);
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
        /// «Мост»: сколько активных было на дату before, что добавилось и что ушло к дате day, сколько стало
        /// (Was + New + NewTests + Returned + Unaccepted − Fixed − FixedManual − Accepted − Gone = Now). Даты — любые две.
        /// Появившаяся между ними считается пришедшей всегда — даже если потом её допустили или исправили (тогда она ещё и ушла):
        /// «новая», если у её проверки был прогон не позже before, иначе — «в новых проверках». Исправленная, которая ещё есть в
        /// отчёте, — «отмечена на сайте» (FixedManual), подтвердится следующим отчётом.
        /// </summary>
        public RunChange Change(DateTime before, DateTime day, Func<long, bool> filter = null)
        {
            var was = Snapshot(before, filter);
            var now = Snapshot(day, filter);
            var reportDay = Reports.ToDictionary(r => r.Id, r => r.Day.Date);
            var testFirst = ReportTests.Where(x => reportDay.ContainsKey(x.ReportId)).GroupBy(x => x.TestId)
                .ToDictionary(g => g.Key, g => g.Min(x => reportDay[x.ReportId]));
            var clashTest = Clashes.ToDictionary(x => x.Id, x => x.TestId);
            bool OldTest(long id) => clashTest.TryGetValue(id, out var t) && testFirst.TryGetValue(t, out var first) && first <= before.Date;
            var c = new RunChange { Before = before.Date, Day = day.Date };
            foreach (var id in was.Keys.Union(now.Keys))
            {
                var hadP = was.TryGetValue(id, out var p);
                var hasN = now.TryGetValue(id, out var n);
                bool startActive;
                if (!hadP)
                {
                    var kind = OldTest(id) ? RunChange.NewKind : RunChange.NewTestsKind;
                    if (kind == RunChange.NewKind) c.New++; else c.NewTests++;
                    c.Entered[id] = kind;
                    startActive = true;
                }
                else
                {
                    startActive = p.State == ClashState.Active;
                    if (startActive) c.Was++;
                }
                var isActive = hasN && n.State == ClashState.Active;
                if (isActive) c.Now++;
                if (startActive && !isActive)
                {
                    string kind;
                    if (!hasN) { c.Gone++; kind = RunChange.GoneKind; }
                    else if (n.State == ClashState.Fixed)
                    {
                        if (n.Present) { c.FixedManual++; kind = RunChange.FixedManualKind; }
                        else { c.Fixed++; kind = RunChange.FixedKind; }
                    }
                    else { c.Accepted++; kind = RunChange.AcceptedKind; }
                    c.Left[id] = kind;
                }
                else if (hadP && !startActive && isActive)
                {
                    if (p.State == ClashState.Fixed) { c.Returned++; c.Entered[id] = RunChange.ReturnedKind; }
                    else { c.Unaccepted++; c.Entered[id] = RunChange.UnacceptedKind; }
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
    }

    /// <summary>Что изменилось в активных от даты Before к дате Day.</summary>
    public sealed class RunChange
    {
        public const string NewKind = "new", NewTestsKind = "tests", ReturnedKind = "returned", UnacceptedKind = "unaccepted";
        public const string FixedKind = "fixed", FixedManualKind = "marked", AcceptedKind = "accepted", GoneKind = "gone";

        public DateTime Before, Day;
        /// <summary>Активных на дату Before и на дату Day.</summary>
        public int Was, Now;
        /// <summary>Пришли: новые (у проверки были прогоны раньше), в новых проверках (первый прогон проверки), вернулись после исправления, сняли «допущена».</summary>
        public int New, NewTests, Returned, Unaccepted;
        /// <summary>Ушли: исправлены (подтверждено отчётом), отмечены исправленными на сайте (ещё в отчёте), допущены, пропали (нет данных).</summary>
        public int Fixed, FixedManual, Accepted, Gone;
        /// <summary>Как коллизия пришла и как ушла (виды выше) — для разбивки по моделям и списка изменений.</summary>
        public Dictionary<long, string> Entered { get; } = new Dictionary<long, string>();
        public Dictionary<long, string> Left { get; } = new Dictionary<long, string>();
        public int Appeared => New + NewTests + Returned + Unaccepted;
        public int Went => Fixed + FixedManual + Accepted + Gone;
    }
}
