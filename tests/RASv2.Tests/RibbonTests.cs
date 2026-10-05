using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.Core;

namespace RASv2.Tests
{
    /// <summary>Раскладка кнопок по панелям вкладки (RibbonLayout): файл ribbon.txt и кнопки без раскладки.</summary>
    internal static class RibbonTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly RibbonButton[] Buttons =
        {
            new RibbonButton("ModelHistory.Journal", "RAS-Model History"),
            new RibbonButton("ModelHistory.History", "RAS-Model History"),
            new RibbonButton("ModelHistory.Settings", "RAS-Model History"),
            new RibbonButton("Core.About", "Плагин")
        };

        /// <summary>Лента одной строкой: «Панель: кнопка, кнопка | Панель: …».</summary>
        private static string Text(IEnumerable<RibbonPanelSpec> panels) =>
            string.Join(" | ", panels.Select(p => p.Title + ": " + string.Join(", ", p.Buttons)));

        private static List<RibbonPanelSpec> Arrange(string layout, IEnumerable<RibbonButton> buttons, List<string> warnings = null) =>
            RibbonLayout.Arrange(RibbonLayout.Parse(layout.Split('\n'), w => warnings?.Add(w)), buttons, w => warnings?.Add(w));

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("кнопки стоят на панелях раскладки в её порядке", () =>
            {
                var text = Text(Arrange("# комментарий\n[Общие]\nCore.About\nModelHistory.Journal\n\n[Журнал]\nModelHistory.History\nModelHistory.Settings", Buttons));
                Check(text == "Общие: Core.About, ModelHistory.Journal | Журнал: ModelHistory.History, ModelHistory.Settings", text);
            });
            yield return Case("кнопка без раскладки — на панели модуля, после панелей раскладки", () =>
            {
                var text = Text(Arrange("[Общие]\nModelHistory.Journal", Buttons));
                Check(text == "Общие: ModelHistory.Journal | RAS-Model History: ModelHistory.History, ModelHistory.Settings | Плагин: Core.About", text);
            });
            yield return Case("нет раскладки — у каждого модуля своя панель", () =>
            {
                var text = Text(Arrange("", Buttons));
                Check(text == "RAS-Model History: ModelHistory.Journal, ModelHistory.History, ModelHistory.Settings | Плагин: Core.About", text);
            });
            yield return Case("кнопок модуля нет (не запустился, другая версия Revit) — пустая панель не создаётся", () =>
            {
                var text = Text(Arrange("[RAS-Model History]\nModelHistory.Journal\nModelHistory.History\n[Плагин]\nCore.About",
                    Buttons.Where(b => b.Id == "Core.About")));
                Check(text == "Плагин: Core.About", text);
            });
            yield return Case("панель по умолчанию совпала с панелью раскладки — кнопка добавляется к ней", () =>
            {
                var text = Text(Arrange("[RAS-Model History]\nModelHistory.Settings", Buttons));
                Check(text == "RAS-Model History: ModelHistory.Settings, ModelHistory.Journal, ModelHistory.History | Плагин: Core.About", text);
            });
            yield return Case("кнопка, указанная дважды, стоит на первом месте; панель, указанная дважды, — одна", () =>
            {
                var warnings = new List<string>();
                var text = Text(Arrange("[Общие]\nCore.About\n[Прочее]\nCore.About\nModelHistory.Journal\n[общие]\nModelHistory.History",
                    Buttons, warnings));
                Check(text == "Общие: Core.About, ModelHistory.History | Прочее: ModelHistory.Journal | RAS-Model History: ModelHistory.Settings", text);
                Check(warnings.Count == 1, "предупреждений: " + warnings.Count);
            });
            yield return Case("ошибки в файле не ломают ленту", () =>
            {
                var warnings = new List<string>();
                var text = Text(Arrange("ModelHistory.Journal\n[]\nCore.About\n[Общие]\r\n  ModelHistory.History  \r\n", Buttons, warnings));
                Check(text == "Общие: ModelHistory.History | RAS-Model History: ModelHistory.Journal, ModelHistory.Settings | Плагин: Core.About", text);
                Check(warnings.Count == 3, "предупреждений: " + warnings.Count);
            });
            yield return Case("регистр в кодах и названиях панелей не важен", () =>
            {
                var text = Text(Arrange("[Общие]\nmodelhistory.journal\ncore.ABOUT", Buttons));
                Check(text == "Общие: ModelHistory.Journal, Core.About | RAS-Model History: ModelHistory.History, ModelHistory.Settings", text);
            });
        }
    }
}
