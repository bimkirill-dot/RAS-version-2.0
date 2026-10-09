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
        /// <summary>Коллизия из отчёта; у Gone — та, что была с пометкой «Исправлено», или null, если её нет в отчёте.</summary>
        public ClashResult Result { get; set; }
        /// <summary>Известная коллизия; у New — null.</summary>
        public KnownClash Known { get; set; }
        public string IdentityKey { get; set; }
        public ClashState NewState { get; set; }
        /// <summary>Статус меняется (событие в истории коллизии).</summary>
        public bool StateChanged { get; set; }
        /// <summary>Коллизия в отчёте и не исправлена — «есть в последнем отчёте».</summary>
        public bool Present { get; set; }
    }

    /// <summary>
    /// Слияние нового отчёта одной проверки с тем, что уже известно (DECISIONS.md модуля: «личность», статусы).
    /// Без базы — проверяется тестами.
    /// <list type="bullet">
    /// <item>Новая «личность» — новая коллизия; статус — из Navisworks («Подтверждено» → «Допущена», «Исправлено» → «Исправлена»).</item>
    /// <item>Известная — статус на сайте главнее статуса Navisworks: «Допущена» остаётся «Допущена».</item>
    /// <item>«Активная», которой нет в отчёте (или Navisworks пишет «Исправлено»), — «Исправлена».</item>
    /// <item>«Исправлена», которая снова в отчёте, — опять «Активная».</item>
    /// </list>
    /// </summary>
    public static class ReportMerge
    {
        public static List<MergeStep> Plan(string testName, IEnumerable<KnownClash> known, IEnumerable<ClashResult> results)
        {
            var byKey = new Dictionary<string, KnownClash>(StringComparer.Ordinal);
            foreach (var k in known) byKey[k.IdentityKey] = k;

            var steps = new List<MergeStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                var key = ClashRules.IdentityKey(testName, r);
                // та же «личность» дважды в одном отчёте (без guid, та же пара элементов) — одна коллизия
                if (!seen.Add(key)) continue;
                // «Исправлено» в Navisworks — коллизии в модели уже нет, хоть она и в отчёте
                var present = r.Status != NavisworksStatus.Resolved;

                if (!byKey.TryGetValue(key, out var k))
                {
                    steps.Add(new MergeStep
                    {
                        Action = MergeAction.New, Result = r, IdentityKey = key,
                        NewState = ClashRules.StateOf(r.Status), StateChanged = false, Present = present,
                    });
                    continue;
                }
                if (!present)
                {
                    steps.Add(Gone(k, r));
                    continue;
                }
                var reappeared = k.State == ClashState.Fixed;
                steps.Add(new MergeStep
                {
                    Action = reappeared ? MergeAction.Reappeared : MergeAction.Again, Result = r, Known = k, IdentityKey = key,
                    NewState = reappeared ? ClashState.Active : k.State, StateChanged = reappeared, Present = true,
                });
            }
            foreach (var k in byKey.Values.Where(k => !seen.Contains(k.IdentityKey)))
                steps.Add(Gone(k, null));
            return steps;
        }

        private static MergeStep Gone(KnownClash k, ClashResult r) => new MergeStep
        {
            Action = MergeAction.Gone, Result = r, Known = k, IdentityKey = k.IdentityKey,
            NewState = k.State == ClashState.Active ? ClashState.Fixed : k.State,
            StateChanged = k.State == ClashState.Active, Present = false,
        };
    }
}
