using System;
using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.Tests
{
    /// <summary>Ядро: окно «Что нового» после выпуска (WhatsNew).</summary>
    internal static class WhatsNewTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            const string id = "2026.10.23-120000";
            yield return Case("«что нового»: новая сборка с текстом — показать один раз", () =>
            {
                Check(WhatsNew.ShouldShow(id, "all", "История модели: …", null), "ещё не видел — показать");
                Check(WhatsNew.ShouldShow(id, "test", "История модели: …", "2026.10.23-090930"), "видел прежнюю — показать (и тестовой группе)");
                Check(!WhatsNew.ShouldShow(id, "all", "История модели: …", " " + id + " "), "эту уже видел — нет");
            });
            yield return Case("«что нового»: без текста и у разработчика — не показывать", () =>
            {
                Check(!WhatsNew.ShouldShow(id, "all", "  ", null) && !WhatsNew.ShouldShow(id, "all", null, null), "текста нет");
                Check(!WhatsNew.ShouldShow(id, "dev", "текст", null), "сборка разработчика (revit.cmd build)");
            });
            yield return Case("«что нового»: дата выпуска из номера сборки", () =>
            {
                Check(WhatsNew.DateOf(id) == "23.10.2026" && WhatsNew.DateOf("x") == "" && WhatsNew.DateOf(null) == "", WhatsNew.DateOf(id));
            });
        }
    }
}
