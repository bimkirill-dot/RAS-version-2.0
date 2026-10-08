using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.Core;

namespace RASv2.Tests
{
    /// <summary>Раскладка ленты (RibbonLayout): отделы, панели, стопки, подписи, кнопки без раскладки.</summary>
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

        /// <summary>
        /// Лента одной строкой: «{Отдел}Панель: большая, [стопка, из, кнопок] | …»; своя подпись — «код=«подпись»».
        /// Панель без отдела — без фигурных скобок.
        /// </summary>
        private static string Text(RibbonPlan plan) =>
            string.Join(" | ", plan.Panels.Select(p =>
                (p.Department == null ? "" : "{" + p.Department + "}") + p.Title + ": " +
                string.Join(", ", p.Slots.Select(s => s.IsStack ? "[" + string.Join(", ", s.Entries.Select(Entry)) + "]" : Entry(s.Entries[0]))))) +
            (plan.Departments.Count > 0 ? " / отделы: " + string.Join(", ", plan.Departments) : "");

        private static string Entry(RibbonEntry e) => e.Label == null ? e.Id : e.Id + "=«" + e.Label + "»";

        private static RibbonPlan Plan(string layout, IEnumerable<RibbonButton> buttons = null, List<string> warnings = null) =>
            RibbonLayout.Arrange(RibbonLayout.Parse(layout.Split('\n'), w => warnings?.Add(w)), buttons ?? Buttons,
                w => warnings?.Add(w), "Core.About");

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("кнопки стоят на панелях раскладки в её порядке", () =>
            {
                var text = Text(Plan("# комментарий\n[Общие]\nCore.About\nModelHistory.Journal\n\n[Журнал]\nModelHistory.History\nModelHistory.Settings"));
                Check(text == "Общие: Core.About, ModelHistory.Journal | Журнал: ModelHistory.History, ModelHistory.Settings", text);
            });
            yield return Case("нет раскладки — у каждого модуля своя панель, переключателя отделов нет", () =>
            {
                var text = Text(Plan(""));
                Check(text == "RAS-Model History: ModelHistory.Journal, ModelHistory.History, ModelHistory.Settings | Плагин: Core.About", text);
            });
            yield return Case("кнопка без раскладки — на панели модуля после панелей раскладки, во всех отделах", () =>
            {
                var text = Text(Plan("[Плагин]\nCore.About\n[[BIM]]\n[Журнал]\nModelHistory.Journal"));
                Check(text == "Плагин: Core.About | {BIM}Журнал: ModelHistory.Journal | RAS-Model History: ModelHistory.History, ModelHistory.Settings / отделы: BIM", text);
            });
            yield return Case("своя подпись, подпись с переносом и без подписи", () =>
            {
                var text = Text(Plan("[П]\nModelHistory.Journal подпись=\"Журнал\\nизменений\"\nModelHistory.History   подпись=\"\"\nModelHistory.Settings подпись=\"Сказал \\\"да\\\"\"\nCore.About"));
                Check(text == "П: ModelHistory.Journal=«Журнал\nизменений», ModelHistory.History=«», ModelHistory.Settings=«Сказал \"да\"», Core.About", text);
            });
            yield return Case("стопки: несколько подряд, из одной кнопки — большая", () =>
            {
                var text = Text(Plan("[П]\nModelHistory.Journal\n(\nModelHistory.History подпись=\"История\"\nModelHistory.Settings подпись=\"\"\n)\n(\nCore.About\n)"));
                Check(text == "П: ModelHistory.Journal, [ModelHistory.History=«История», ModelHistory.Settings=«»], Core.About", text);
            });
            yield return Case("стопка больше трёх — первые три в стопке, остальные большие, причина в журнале", () =>
            {
                var warnings = new List<string>();
                var text = Text(Plan("[П]\n(\nModelHistory.Journal\nModelHistory.History\nModelHistory.Settings\nCore.About\n)", null, warnings));
                Check(text == "П: [ModelHistory.Journal, ModelHistory.History, ModelHistory.Settings], Core.About", text);
                Check(warnings.Count == 1, "предупреждений: " + warnings.Count);
            });
            yield return Case("кнопки из стопки нет (модуль выключен) — стопка сжимается, одна оставшаяся — большая", () =>
            {
                var layout = "[П]\n(\nModelHistory.Journal\nModelHistory.History\nModelHistory.Settings\n)\n(\nModelHistory.Journal\nCore.About\n)";
                var text = Text(Plan(layout, Buttons.Where(b => b.Id != "ModelHistory.History" && b.Id != "ModelHistory.Journal")));
                Check(text == "П: ModelHistory.Settings, Core.About", text);
            });
            yield return Case("отделы: панели до первого отдела видны всегда, одна кнопка может быть в нескольких отделах", () =>
            {
                var warnings = new List<string>();
                var plan = Plan("[Плагин]\nCore.About\n[[Конструктив]]\n[История модели]\nModelHistory.Journal\nModelHistory.History\n" +
                                "[[BIM]]\n[История модели]\nModelHistory.Journal подпись=\"Журнал\"\nModelHistory.Settings", null, warnings);
                var text = Text(plan);
                Check(text == "Плагин: Core.About | {Конструктив}История модели: ModelHistory.Journal, ModelHistory.History | " +
                              "{BIM}История модели: ModelHistory.Journal=«Журнал», ModelHistory.Settings / отделы: Конструктив, BIM", text);
                Check(warnings.Count == 0, "предупреждений: " + warnings.Count);
                Check(RibbonLayout.IsVisible(plan.Panels[0], "BIM") && !RibbonLayout.IsVisible(plan.Panels[1], "BIM") && RibbonLayout.IsVisible(plan.Panels[2], "bim"),
                    "видимость панелей в отделе");
            });
            yield return Case("кнопка дважды в одном отделе — на первом месте; одна панель, указанная дважды, — одна", () =>
            {
                var warnings = new List<string>();
                var text = Text(Plan("[Плагин]\nCore.About\n[[BIM]]\n[А]\nModelHistory.Journal\n[Б]\nModelHistory.Journal\nModelHistory.History\n[а]\nModelHistory.Settings", null, warnings));
                Check(text == "Плагин: Core.About | {BIM}А: ModelHistory.Journal, ModelHistory.Settings | {BIM}Б: ModelHistory.History / отделы: BIM", text);
                Check(warnings.Count == 1, "предупреждений: " + warnings.Count);
            });
            yield return Case("переключатель отделов («О плагине») только в отделе — ставится на панель, видную всегда", () =>
            {
                var warnings = new List<string>();
                var text = Text(Plan("[[BIM]]\n[Журнал]\nModelHistory.Journal\nCore.About\nModelHistory.History\nModelHistory.Settings", null, warnings));
                Check(text == "{BIM}Журнал: ModelHistory.Journal, Core.About, ModelHistory.History, ModelHistory.Settings | Плагин: Core.About / отделы: BIM", text);
                Check(warnings.Count == 1, "предупреждений: " + warnings.Count);
            });
            yield return Case("отдел при запуске — сохранённый выбор, а если его больше нет — первый", () =>
            {
                var plan = Plan("[Плагин]\nCore.About\n[[Конструктив]]\n[А]\nModelHistory.Journal\n[[BIM]]\n[Б]\nModelHistory.History");
                Check(RibbonLayout.StartDepartment(plan, "bim") == "BIM", "сохранённый");
                Check(RibbonLayout.StartDepartment(plan, null) == "Конструктив", "ничего не выбирал");
                Check(RibbonLayout.StartDepartment(plan, "Удалённый") == "Конструктив", "удалённый отдел");
            });
            yield return Case("отдел, у которого не осталось кнопок, в переключатель не попадает", () =>
            {
                var text = Text(Plan("[Плагин]\nCore.About\n[[КЖ]]\n[А]\nKj.Walls\n[[BIM]]\n[Б]\nModelHistory.Journal"));
                Check(text.EndsWith("/ отделы: BIM"), text);
            });
            yield return Case("ошибки в файле не ломают ленту", () =>
            {
                var warnings = new List<string>();
                var text = Text(Plan("ModelHistory.Journal\n[]\n(\n)\n[П]\n)\n(\n(\nModelHistory.History размер=\"маленькая\" цвет=\"красный\"\n" +
                                     "Core.About подпись=Без кавычек\n[[  ]]\n[Р]\nModelHistory.Settings", null, warnings));
                Check(text == "П: [ModelHistory.History, Core.About] | Р: ModelHistory.Settings | RAS-Model History: ModelHistory.Journal", text);
                // кнопка до панели, пустая панель, стопка до панели и её «)», «)» без «(», стопка в стопке, «размер», «цвет»,
                // подпись без кавычек, стопка не закрыта перед отделом, пустой отдел
                Check(warnings.Count == 11, "предупреждений: " + warnings.Count + ": " + string.Join(" / ", warnings));
            });
            yield return Case("регистр в кодах, панелях и отделах не важен", () =>
            {
                var text = Text(Plan("[Плагин]\ncore.ABOUT\n[[bim]]\n[Общие]\nmodelhistory.journal\n[[BIM]]\n[общие]\nModelHistory.History"));
                Check(text == "Плагин: Core.About | {bim}Общие: ModelHistory.Journal, ModelHistory.History | RAS-Model History: ModelHistory.Settings / отделы: bim", text);
            });
        }
    }
}
