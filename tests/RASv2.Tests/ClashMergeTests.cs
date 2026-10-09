using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: новый отчёт против известного (ReportMerge).</summary>
    internal static class ClashMergeTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Test = "АР Стены/МЕР Трубы";

        private static ClashResult Result(string guid, NavisworksStatus status = NavisworksStatus.New, long a = 1, long b = 2)
        {
            var r = new ClashResult { Name = "Конфликт", Guid = guid, Status = status };
            r.Elements.Add(new ClashElement { ModelFile = "AR.nwc", ElementId = a });
            r.Elements.Add(new ClashElement { ModelFile = "VK.nwc", ElementId = b });
            return r;
        }

        private static KnownClash Known(string guid, ClashState state) => new KnownClash { Id = guid.GetHashCode(), IdentityKey = "guid:" + guid, State = state };

        private static MergeStep Step(List<MergeStep> steps, string guid) => steps.Single(s => s.IdentityKey == "guid:" + guid);

        /// <summary>Известная коллизия с парой элементов последнего появления (как её даёт база).</summary>
        private static KnownClash KnownWithPair(string guid, ClashState state, long a = 1, long b = 2)
        {
            var k = Known(guid, state);
            k.PairKey = ClashRules.PairKey(Test, Result(guid, a: a, b: b).Elements);
            return k;
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("первый отчёт: все новые, статус — из Navisworks", () =>
            {
                var steps = ReportMerge.Plan(Test, new KnownClash[0], new[]
                {
                    Result("a"), Result("b", NavisworksStatus.Approved), Result("c", NavisworksStatus.Resolved), Result("d", NavisworksStatus.Reviewed),
                });
                Check(steps.All(s => s.Action == MergeAction.New), "все новые");
                Check(Step(steps, "a").NewState == ClashState.Active && Step(steps, "a").Present, "новая — активная, в отчёте");
                Check(Step(steps, "b").NewState == ClashState.Accepted, "подтверждённая — допущена");
                Check(Step(steps, "c").NewState == ClashState.Fixed && !Step(steps, "c").Present, "исправленная — исправлена, не «в отчёте»");
                Check(Step(steps, "d").NewState == ClashState.Active, "проверенная — активная");
            });
            yield return Case("снова в отчёте — статус сайта главнее статуса Navisworks", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known("a", ClashState.Active), Known("b", ClashState.Accepted) },
                    new[] { Result("a", NavisworksStatus.Approved), Result("b", NavisworksStatus.New) });
                Check(Step(steps, "a").Action == MergeAction.Again && Step(steps, "a").NewState == ClashState.Active, "активная осталась активной");
                Check(Step(steps, "b").Action == MergeAction.Again && Step(steps, "b").NewState == ClashState.Accepted, "допущенная осталась допущенной");
                Check(steps.All(s => !s.StateChanged), "статусы не менялись");
            });
            yield return Case("пропала из отчёта: активная — исправлена, допущенная — без изменений", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known("a", ClashState.Active), Known("b", ClashState.Accepted), Known("c", ClashState.Fixed) },
                    new ClashResult[0]);
                Check(steps.All(s => s.Action == MergeAction.Gone && !s.Present), "все пропали");
                Check(Step(steps, "a").NewState == ClashState.Fixed && Step(steps, "a").StateChanged, "активная → исправлена");
                Check(Step(steps, "b").NewState == ClashState.Accepted && !Step(steps, "b").StateChanged, "допущенная");
                Check(Step(steps, "c").NewState == ClashState.Fixed && !Step(steps, "c").StateChanged, "исправленная");
            });
            yield return Case("Navisworks пишет «Исправлено» — как пропала", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known("a", ClashState.Active) }, new[] { Result("a", NavisworksStatus.Resolved) });
                var a = Step(steps, "a");
                Check(a.Action == MergeAction.Gone && a.NewState == ClashState.Fixed && a.StateChanged && a.Result != null, "исправлена, снимок остаётся");
            });
            yield return Case("исправленная снова в отчёте — опять активная", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known("a", ClashState.Fixed) }, new[] { Result("a") });
                var a = Step(steps, "a");
                Check(a.Action == MergeAction.Reappeared && a.NewState == ClashState.Active && a.StateChanged && a.Present, "появилась снова");
            });
            yield return Case("без guid — по паре элементов; дубль в отчёте — одна коллизия", () =>
            {
                var steps = ReportMerge.Plan(Test, new KnownClash[0], new[] { Result(null, a: 5, b: 6), Result(null, a: 5, b: 6) });
                Check(steps.Count(s => s.Action == MergeAction.New) == 1, "одна новая, шагов: " + steps.Count);
                Check(steps[0].IdentityKey.StartsWith("pair:", StringComparison.Ordinal), "ключ по паре");
            });
            yield return Case("номер Navisworks сменился — та же коллизия по паре элементов, ключ обновляется", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { KnownWithPair("old", ClashState.Accepted) }, new[] { Result("new") });
                var s = steps.Single();
                Check(s.Action == MergeAction.Again && s.Known?.IdentityKey == "guid:old", "узнана: " + s.Action);
                Check(s.Rekeyed && s.IdentityKey == "guid:new", "новый ключ: " + s.IdentityKey);
                Check(s.NewState == ClashState.Accepted && !s.StateChanged, "«Допущена» сохранилась");
            });
            yield return Case("сменился номер у исправленной — снова активная; у «Исправлено» в Navisworks — исправлена", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { KnownWithPair("f", ClashState.Fixed, 1, 2), KnownWithPair("a", ClashState.Active, 3, 4) },
                    new[] { Result("f2", a: 1, b: 2), Result("a2", NavisworksStatus.Resolved, 3, 4) });
                var f = Step(steps, "f2");
                Check(f.Action == MergeAction.Reappeared && f.Rekeyed && f.NewState == ClashState.Active, "исправленная вернулась");
                var g = Step(steps, "a2");
                Check(g.Action == MergeAction.Gone && g.Rekeyed && g.NewState == ClashState.Fixed && g.Result != null, "исправлена в Navisworks");
                Check(steps.Count == 2, "лишних шагов нет: " + steps.Count);
            });
            yield return Case("пара не перехватывает коллизию, которая есть в отчёте со своим номером", () =>
            {
                // две известные с одной парой (одни и те же элементы пересекаются дважды): «a» — со своим номером, «x» — новый номер
                var steps = ReportMerge.Plan(Test, new[] { KnownWithPair("a", ClashState.Active), KnownWithPair("b", ClashState.Accepted) },
                    new[] { Result("x"), Result("a") });
                Check(Step(steps, "a").Action == MergeAction.Again && !Step(steps, "a").Rekeyed, "«a» — по своему номеру");
                Check(Step(steps, "x").Known?.IdentityKey == "guid:b" && Step(steps, "x").Rekeyed, "«x» — это «b»");
                Check(steps.Count == 2, "шагов: " + steps.Count);
            });
            yield return Case("неполная пара (нет номера элемента) — по паре не узнаём", () =>
            {
                var k = Known("old", ClashState.Active);
                k.PairKey = null;
                var r = Result("new");
                r.Elements[1].ElementId = null;
                var steps = ReportMerge.Plan(Test, new[] { k }, new[] { r });
                Check(steps.Any(s => s.Action == MergeAction.New) && steps.Any(s => s.Action == MergeAction.Gone), "новая + пропавшая");
                Check(!ClashRules.HasFullPair(r.Elements), "пара неполная");
            });
        }
    }
}
