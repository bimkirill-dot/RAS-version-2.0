using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: новый отчёт против известного (ReportMerge) и проверки без отчёта в прогоне (RunCheck).</summary>
    internal static class ClashMergeTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Test = "АР Стены/МЕР Трубы";

        /// <summary>Результат Navisworks: пара элементов AR #a и VK #b (номер Navisworks — guid, на узнавание не влияет).</summary>
        private static ClashResult Result(long a, long b, NavisworksStatus status = NavisworksStatus.New, string guid = null, bool swap = false,
            string modelA = "AR.nwc", string modelB = "VK.nwc")
        {
            var r = new ClashResult { Name = "Конфликт" + a, Guid = guid ?? Guid.NewGuid().ToString(), Status = status };
            var ea = new ClashElement { ModelFile = modelA, ElementId = a };
            var eb = new ClashElement { ModelFile = modelB, ElementId = b };
            r.Elements.Add(swap ? eb : ea);
            r.Elements.Add(swap ? ea : eb);
            return r;
        }

        private static string Key(long a, long b) => ClashRules.IdentityKey(Test, Result(a, b));

        private static KnownClash Known(long a, long b, ClashState state) => new KnownClash { Id = a * 1000 + b, IdentityKey = Key(a, b), State = state };

        private static MergeStep Step(List<MergeStep> steps, long a, long b) => steps.Single(s => s.IdentityKey == Key(a, b));

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("первый отчёт: все новые, статус — из Navisworks", () =>
            {
                var steps = ReportMerge.Plan(Test, new KnownClash[0], new[]
                {
                    Result(1, 2), Result(3, 4, NavisworksStatus.Approved), Result(5, 6, NavisworksStatus.Resolved), Result(7, 8, NavisworksStatus.Reviewed),
                });
                Check(steps.All(s => s.Action == MergeAction.New), "все новые");
                Check(Step(steps, 1, 2).NewState == ClashState.Active && Step(steps, 1, 2).Present, "новая — активная, в отчёте");
                Check(Step(steps, 3, 4).NewState == ClashState.Active, "подтверждённая в Navisworks — активная: статусы ставит только сайт");
                Check(Step(steps, 5, 6).NewState == ClashState.Fixed && !Step(steps, 5, 6).Present, "исправленная — исправлена, не «в отчёте»");
                Check(Step(steps, 7, 8).NewState == ClashState.Active, "проверенная — активная");
            });
            yield return Case("одна пара — несколько результатов Navisworks (точки касания) — одна коллизия", () =>
            {
                var steps = ReportMerge.Plan(Test, new KnownClash[0], new[] { Result(1, 2), Result(1, 2), Result(1, 2, swap: true), Result(3, 4) });
                Check(steps.Count == 2, "коллизий: " + steps.Count);
                Check(Step(steps, 1, 2).Points == 3 && Step(steps, 3, 4).Points == 1, "точек: " + Step(steps, 1, 2).Points);
            });
            yield return Case("файл модели переименован — та же коллизия, не «исправлена + новая» (14.10.2026)", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Active), Known(3, 4, ClashState.Accepted) },
                    new[] { Result(1, 2, modelB: "VK_BIM_Alma.nwc"), Result(3, 4, modelA: "AR с отверстиями.nwc") });
                Check(steps.Count == 2 && steps.All(s => s.Action == MergeAction.Again), "узнаны: " + string.Join(", ", steps.Select(s => s.Action)));
                Check(Step(steps, 3, 4).NewState == ClashState.Accepted, "«Допущена» сохранилась");
            });
            yield return Case("одинаковые номера у разных моделей в одном отчёте — разные коллизии", () =>
            {
                var results = new[] { Result(1, 2), Result(1, 2, modelA: "KR.nwc"), Result(1, 2, swap: true), Result(5, 6) };
                var keys = ClashRules.IdentityKeys(Test, results);
                Check(keys[0] != keys[1], "AR и KR с одинаковыми номерами — разные");
                Check(keys[0] == keys[2], "та же пара в другом порядке — одна");
                Check(keys[0].StartsWith("pair:", StringComparison.Ordinal) && keys[3].StartsWith("ids:", StringComparison.Ordinal), "ключи: " + string.Join(" ", keys));
                var steps = ReportMerge.Plan(Test, new KnownClash[0], results);
                Check(steps.Count == 3, "коллизий: " + steps.Count);
            });
            yield return Case("элементы поменялись местами — та же коллизия; номер Navisworks сменился — тоже", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Accepted) }, new[] { Result(1, 2, swap: true, guid: "новый") });
                var s = steps.Single();
                Check(s.Action == MergeAction.Again && s.NewState == ClashState.Accepted && !s.StateChanged, "узнана, «Допущена» сохранилась: " + s.Action);
            });
            yield return Case("снова в отчёте — статус сайта главнее статуса Navisworks", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Active), Known(3, 4, ClashState.Accepted) },
                    new[] { Result(1, 2, NavisworksStatus.Approved), Result(3, 4) });
                Check(Step(steps, 1, 2).Action == MergeAction.Again && Step(steps, 1, 2).NewState == ClashState.Active, "активная осталась активной");
                Check(Step(steps, 3, 4).Action == MergeAction.Again && Step(steps, 3, 4).NewState == ClashState.Accepted, "допущенная осталась допущенной");
                Check(steps.All(s => !s.StateChanged), "статусы не менялись");
            });
            yield return Case("пропала из отчёта: активная — исправлена, допущенная и исправленная — без изменений", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Active), Known(3, 4, ClashState.Accepted), Known(5, 6, ClashState.Fixed) },
                    new ClashResult[0]);
                Check(steps.All(s => s.Action == MergeAction.Gone && !s.Present), "все пропали");
                Check(Step(steps, 1, 2).NewState == ClashState.Fixed && Step(steps, 1, 2).StateChanged, "активная → исправлена");
                Check(Step(steps, 3, 4).NewState == ClashState.Accepted && !Step(steps, 3, 4).StateChanged, "допущенная");
                Check(Step(steps, 5, 6).NewState == ClashState.Fixed && !Step(steps, 5, 6).StateChanged, "исправленная");
            });
            yield return Case("Navisworks пишет «Исправлено» у всех точек — как пропала; хоть одна открыта — пара есть", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Active), Known(3, 4, ClashState.Active) },
                    new[] { Result(1, 2, NavisworksStatus.Resolved), Result(3, 4, NavisworksStatus.Resolved), Result(3, 4) });
                var a = Step(steps, 1, 2);
                Check(a.Action == MergeAction.Gone && a.NewState == ClashState.Fixed && a.StateChanged && a.Result != null, "исправлена, снимок остаётся");
                Check(Step(steps, 3, 4).Action == MergeAction.Again, "одна точка ещё открыта — активная");
            });
            yield return Case("исправленная снова в отчёте — опять активная, «вернулась»; активная — не «вернулась»", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Fixed), Known(3, 4, ClashState.Active) }, new[] { Result(1, 2), Result(3, 4) });
                var f = Step(steps, 1, 2);
                Check(f.Action == MergeAction.Reappeared && f.NewState == ClashState.Active && f.StateChanged && f.Present && f.Returned, "вернулась");
                Check(!Step(steps, 3, 4).Returned, "активная — нет");
            });
            yield return Case("неполная пара (нет номера элемента) — по номеру Navisworks", () =>
            {
                var r = Result(1, 2, guid: "g1");
                r.Elements[1].ElementId = null;
                Check(!ClashRules.HasFullPair(r.Elements) && ClashRules.IdentityKey(Test, r) == "guid:g1", "ключ — номер Navisworks");
                var steps = ReportMerge.Plan(Test, new[] { new KnownClash { Id = 7, IdentityKey = "guid:g1", State = ClashState.Active } }, new[] { r });
                Check(steps.Single().Action == MergeAction.Again, "узнана по номеру");
            });
            yield return Case("неполный отчёт: пропавшие не трогаем, найденные — как обычно", () =>
            {
                var steps = ReportMerge.Plan(Test, new[] { Known(1, 2, ClashState.Active), Known(5, 6, ClashState.Fixed) },
                    new[] { Result(5, 6) }, complete: false);
                Check(steps.Count == 1 && Step(steps, 5, 6).Returned, "только найденная: " + steps.Count);
                var test = new ClashTest { Summary = new ClashSummary { Total = 5 } };
                test.Results.Add(Result(9, 9));
                test.OpenInFile = 3;
                Check(!ReportMerge.IsComplete(test), "в проверке 5, в файле 3 — неполный");
                test.OpenInFile = 5;
                Check(ReportMerge.IsComplete(test), "в файле все 5 (одна без элементов пропущена) — полный");
            });
            yield return Case("полный отчёт — все нерешённые; «Исправлено» Navisworks выгружать не обязательно", () =>
            {
                // в проверке 7: 5 нерешённых, 2 «Исправлено»; выгружены только нерешённые
                var test = new ClashTest { Summary = new ClashSummary { Total = 7, Active = 5, Resolved = 2 } };
                test.OpenInFile = 5;
                Check(ReportMerge.IsComplete(test), "без «Исправлено» — полный");
                // выгружены только «Создать» (3 из 5 нерешённых) и «Исправлено»: 5 в файле, но нерешённых — 3
                test.OpenInFile = 3;
                Check(!ReportMerge.IsComplete(test), "отбор без «Активные» — неполный");
            });
            yield return Case("предохранитель: пропало больше 10 и больше половины активных", () =>
            {
                Check(ReportMerge.TooManyGone(86, 86) && ReportMerge.TooManyGone(30, 16), "86 из 86, 16 из 30 — подозрительно");
                Check(!ReportMerge.TooManyGone(30, 15) && !ReportMerge.TooManyGone(12, 10), "15 из 30, 10 из 12 — обычный прогон");
            });
            yield return Case("проверки без отчёта в последнем прогоне своего блока и критичности", () =>
            {
                var d24 = new DateTime(2026, 9, 24); var d08 = new DateTime(2026, 10, 8, 15, 0, 0);
                var tests = new[]
                {
                    (Name: "КЖ Гор/МЕР Возд", Crit: "Высокая", Block: "S5", Last: (DateTime?)d24),
                    (Name: "АР Стены/МЕР Трубы", Crit: "Высокая", Block: "S5", Last: (DateTime?)d08),
                    (Name: "АР Стены/МЕР Трубы", Crit: "Высокая", Block: "S6", Last: (DateTime?)d24),
                    (Name: "АР Полы", Crit: "Средняя", Block: "S5", Last: (DateTime?)d24),
                };
                var reports = new[] { ("Высокая", "S5", d24), ("Высокая", "S5", d08), ("Высокая", "S6", d24), ("Средняя", "S5", d24) };
                var missing = RunCheck.Missing(tests, t => t.Crit, t => t.Block, t => t.Last, reports);
                Check(missing.Count == 1 && missing[0].Test.Name == "КЖ Гор/МЕР Возд" && missing[0].Run == d08.Date,
                    "только S5 высокой — прогон 08.10: " + string.Join(", ", missing.Select(m => m.Test.Name)));
            });
        }
    }
}
