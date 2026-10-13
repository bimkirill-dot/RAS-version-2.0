using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>Коллизия, уже известная базе, — то, что нужно для слияния с новым отчётом.</summary>
    public sealed class KnownClash
    {
        public long Id { get; set; }
        public string IdentityKey { get; set; }
        public ClashState State { get; set; }
    }

    public enum MergeAction
    {
        /// <summary>Новая коллизия.</summary>
        New,
        /// <summary>Снова в отчёте, статус не меняется.</summary>
        Again,
        /// <summary>Была исправлена — и снова в отчёте: опять «Активная».</summary>
        Reappeared,
        /// <summary>Нет в отчёте (или Navisworks пишет «Исправлено»): «Активная» становится «Исправлена».</summary>
        Gone,
    }

    public sealed class MergeStep
    {
        public MergeAction Action { get; set; }
        /// <summary>Коллизия из отчёта (первый из её результатов); у Gone — та, что с пометкой «Исправлено», или null, если её нет в отчёте.</summary>
        public ClashResult Result { get; set; }
        /// <summary>Известная коллизия; у New — null.</summary>
        public KnownClash Known { get; set; }
        public string IdentityKey { get; set; }
        public ClashState NewState { get; set; }
        /// <summary>Статус меняется (событие в истории коллизии).</summary>
        public bool StateChanged { get; set; }
        /// <summary>Коллизия в отчёте и не исправлена — «есть в последнем отчёте».</summary>
        public bool Present { get; set; }
        /// <summary>Вернулась после исправления: была «Исправлена» — и снова в отчёте.</summary>
        public bool Returned { get; set; }
        /// <summary>Сколько результатов Navisworks на эту пару элементов в отчёте (точки пересечения); у пропавшей — 0.</summary>
        public int Points { get; set; }
    }

    /// <summary>
    /// Слияние нового отчёта одной проверки с тем, что уже известно (DECISIONS.md модуля, «Коллизии: пара элементов, три статуса»).
    /// Без базы — проверяется тестами.
    /// <list type="bullet">
    /// <item>Коллизия — пара элементов в проверке (ClashRules.IdentityKey; порядок элементов не важен), как у автора статьи.
    /// Несколько результатов Navisworks на одну пару (две-три точки касания одних и тех же элементов) — одна коллизия.</item>
    /// <item>Новая — статус из Navisworks («Подтверждено» → «Допущена», «Исправлено» → «Исправлена»).</item>
    /// <item>Известная — статус на сайте главнее статуса Navisworks: «Допущена» остаётся «Допущена».</item>
    /// <item>«Активная», которой нет в отчёте своей проверки (или Navisworks пишет «Исправлено»), — «Исправлена».</item>
    /// <item>«Исправлена», которая снова в отчёте, — опять «Активная», «вернулась после исправления».</item>
    /// <item>Отчёт неполный (в нём меньше коллизий, чем Navisworks насчитал в проверке, — выгружены не все статусы):
    /// пропавшие ничего не меняют.</item>
    /// </list>
    /// </summary>
    public static class ReportMerge
    {
        public static List<MergeStep> Plan(string testName, IEnumerable<KnownClash> known, IEnumerable<ClashResult> results, bool complete = true)
        {
            var knownList = known.ToList();
            var byKey = new Dictionary<string, KnownClash>(StringComparer.Ordinal);
            foreach (var k in knownList) byKey[k.IdentityKey] = k;
            var matched = new HashSet<long>();
            var steps = new List<MergeStep>();

            // результаты одной пары — одна коллизия; порядок — как в отчёте
            foreach (var group in results.GroupBy(r => ClashRules.IdentityKey(testName, r), StringComparer.Ordinal))
            {
                var all = group.ToList();
                // «Исправлено» в Navisworks — этой точки в модели уже нет, хоть она и в отчёте; пара есть, пока есть хоть одна точка
                var open = all.Where(r => r.Status != NavisworksStatus.Resolved).ToList();
                var present = open.Count > 0;
                var r0 = present ? open[0] : all[0];

                if (!byKey.TryGetValue(group.Key, out var k) || !matched.Add(k.Id))
                {
                    steps.Add(new MergeStep
                    {
                        Action = MergeAction.New, Result = r0, IdentityKey = group.Key, Points = all.Count,
                        NewState = present ? ClashRules.StateOf(r0.Status) : ClashState.Fixed, Present = present,
                    });
                    continue;
                }
                if (!present)
                {
                    var gone = Gone(k, r0);
                    gone.Points = all.Count;
                    steps.Add(gone);
                    continue;
                }
                var reappeared = k.State == ClashState.Fixed;
                steps.Add(new MergeStep
                {
                    Action = reappeared ? MergeAction.Reappeared : MergeAction.Again, Result = r0, Known = k, IdentityKey = k.IdentityKey,
                    NewState = reappeared ? ClashState.Active : k.State, StateChanged = reappeared, Present = true, Returned = reappeared,
                    Points = all.Count,
                });
            }
            // неполный отчёт: про пропавшие ничего не известно — не трогаем
            if (complete)
                foreach (var k in knownList.Where(k => !matched.Contains(k.Id)))
                    steps.Add(Gone(k, null));
            return steps;
        }

        /// <summary>Отчёт полный — в нём все коллизии проверки (итог Navisworks «всего» не больше, чем коллизий в отчёте; итога нет — полный).</summary>
        public static bool IsComplete(ClashTest test) => test.Summary == null || test.Summary.Total <= Math.Max(test.ResultsInFile, test.Results.Count);

        /// <summary>
        /// Предохранитель: из отчёта пропало подозрительно много активных — больше 10 и больше половины. Так бывает, если
        /// в Navisworks не подгрузилась модель; но и если проверку честно исправили целиком. Поэтому — предупреждение у
        /// отчёта и «Вернуть активными», а не запрет (DECISIONS.md, «Предохранитель»).
        /// </summary>
        public static bool TooManyGone(int activeBefore, int gone) => gone > 10 && gone * 2 > activeBefore;

        private static MergeStep Gone(KnownClash k, ClashResult r)
        {
            var open = k.State == ClashState.Active;
            return new MergeStep
            {
                Action = MergeAction.Gone, Result = r, Known = k, IdentityKey = k.IdentityKey,
                NewState = open ? ClashState.Fixed : k.State, StateChanged = open, Present = false,
            };
        }
    }
}
