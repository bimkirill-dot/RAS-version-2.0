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
        /// <summary>Проверка + пара элементов последнего появления (ClashRules.PairKey); null — пара неполная, по ней не узнаём.</summary>
        public string PairKey { get; set; }
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
        /// <summary>Ключ «личности» после слияния (у узнанной по паре — новый, с новым номером Navisworks).</summary>
        public string IdentityKey { get; set; }
        /// <summary>Узнана по паре элементов: номер Navisworks сменился — ключ «личности» обновить на IdentityKey.</summary>
        public bool Rekeyed { get; set; }
        public ClashState NewState { get; set; }
        /// <summary>Статус меняется (событие в истории коллизии).</summary>
        public bool StateChanged { get; set; }
        /// <summary>Коллизия в отчёте и не исправлена — «есть в последнем отчёте».</summary>
        public bool Present { get; set; }
        /// <summary>Вернулась после исправления: была «Ждёт проверки» или «Исправлена» — и снова в отчёте.</summary>
        public bool Returned { get; set; }
        /// <summary>Исправление подтверждено прогоном: была «Ждёт проверки» — в отчёте её нет.</summary>
        public bool Confirmed { get; set; }
    }

    /// <summary>
    /// Слияние нового отчёта одной проверки с тем, что уже известно (DECISIONS.md модуля: «личность», статусы).
    /// Без базы — проверяется тестами.
    /// <list type="bullet">
    /// <item>Коллизия узнаётся по ключу «личности» (номер Navisworks), а если номер незнакомый — по паре элементов
    /// «файл модели + номер» среди ещё не узнанных коллизий той же проверки: номер Navisworks меняется, если проверку
    /// пересоздали или собрали новый файл Navisworks, и без этого все коллизии стали бы «новыми», а старые — «исправленными».</item>
    /// <item>Новая «личность» — новая коллизия; статус — из Navisworks («Подтверждено» → «Допущена», «Исправлено» → «Исправлена»).</item>
    /// <item>Известная — статус на сайте главнее статуса Navisworks: «Допущена» остаётся «Допущена».</item>
    /// <item>«Активная» или «Ждёт проверки», которой нет в отчёте (или Navisworks пишет «Исправлено»), — «Исправлена»
    /// (у «Ждёт проверки» — исправление подтверждено).</item>
    /// <item>«Исправлена» или «Ждёт проверки», которая снова в отчёте, — опять «Активная», «вернулась после исправления».</item>
    /// <item>Отчёт неполный (в нём меньше коллизий, чем Navisworks насчитал в проверке, — выгружены не все статусы):
    /// пропавшие ничего не меняют — неизвестно, исправлены они или просто не попали в отчёт.</item>
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
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var resultList = results.ToList();

            // сначала — все узнанные по ключу: пара не должна «перехватить» коллизию, которая в этом же отчёте есть со своим номером
            var byResult = new Dictionary<ClashResult, (KnownClash Known, bool ByPair)>();
            foreach (var r in resultList)
                if (byKey.TryGetValue(ClashRules.IdentityKey(testName, r), out var k) && matched.Add(k.Id))
                    byResult[r] = (k, false);

            // затем — по паре элементов, среди ещё не узнанных
            var byPair = knownList.Where(k => k.PairKey != null && !matched.Contains(k.Id))
                .GroupBy(k => k.PairKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<KnownClash>(g), StringComparer.Ordinal);
            foreach (var r in resultList)
            {
                if (byResult.ContainsKey(r) || !ClashRules.HasFullPair(r.Elements)) continue;
                if (byKey.ContainsKey(ClashRules.IdentityKey(testName, r))) continue;
                if (!byPair.TryGetValue(ClashRules.PairKey(testName, r.Elements), out var queue)) continue;
                while (queue.Count > 0)
                {
                    var k = queue.Dequeue();
                    if (!matched.Add(k.Id)) continue;
                    byResult[r] = (k, true);
                    break;
                }
            }

            var steps = new List<MergeStep>();
            foreach (var r in resultList)
            {
                var key = ClashRules.IdentityKey(testName, r);
                // та же «личность» дважды в одном отчёте (без guid, та же пара элементов) — одна коллизия
                if (!seenKeys.Add(key)) continue;
                // «Исправлено» в Navisworks — коллизии в модели уже нет, хоть она и в отчёте
                var present = r.Status != NavisworksStatus.Resolved;

                if (!byResult.TryGetValue(r, out var hit))
                {
                    steps.Add(new MergeStep
                    {
                        Action = MergeAction.New, Result = r, IdentityKey = key,
                        NewState = ClashRules.StateOf(r.Status), StateChanged = false, Present = present,
                    });
                    continue;
                }
                var k = hit.Known;
                var rekeyed = hit.ByPair && key != k.IdentityKey;
                if (!present)
                {
                    var gone = Gone(k, r);
                    gone.IdentityKey = key;
                    gone.Rekeyed = rekeyed;
                    steps.Add(gone);
                    continue;
                }
                var reappeared = k.State == ClashState.Fixed || k.State == ClashState.Pending;
                steps.Add(new MergeStep
                {
                    Action = reappeared ? MergeAction.Reappeared : MergeAction.Again, Result = r, Known = k, IdentityKey = key, Rekeyed = rekeyed,
                    NewState = reappeared ? ClashState.Active : k.State, StateChanged = reappeared, Present = true, Returned = reappeared,
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

        private static MergeStep Gone(KnownClash k, ClashResult r)
        {
            var open = k.State == ClashState.Active || k.State == ClashState.Pending;
            return new MergeStep
            {
                Action = MergeAction.Gone, Result = r, Known = k, IdentityKey = k.IdentityKey,
                NewState = open ? ClashState.Fixed : k.State, StateChanged = open, Present = false,
                Confirmed = k.State == ClashState.Pending,
            };
        }
    }
}
