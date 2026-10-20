using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.Core;

namespace RASv2.Tests
{
    /// <summary>ribbon.txt — один файл: блоки {Модули} и {Люди}, раскладка, «доступна=» у кнопки (PluginFile, ButtonAccess).</summary>
    internal static class PluginFileTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Sample =
            "# режимы и раскладка\n" +
            "{Модули}\n" +
            "ModelHistory = работает\n" +
            "ClashAnalysis = только тест\n" +
            "{Люди}\n" +
            "руководители = Ivanov_A, RAS\\Petrov_B\n" +
            "[Плагин]\n" +
            "Core.About\n" +
            "[[BIM]]\n" +
            "[Менеджер коллизий]\n" +
            "(\n" +
            "ClashAnalysis.ViewSettings подпись=\"Настройки вида\"\n" +
            "ClashAnalysis.ElementClashes подпись=\"Коллизии элемента\" доступна=\"руководители\"\n" +
            ")\n" +
            "ModelHistory.Settings доступна=\"никому\"\n";

        private static readonly RibbonButton[] Buttons =
        {
            new RibbonButton("Core.About", "Плагин"),
            new RibbonButton("ClashAnalysis.ViewSettings", "RAS-Clash Analysis"),
            new RibbonButton("ClashAnalysis.ElementClashes", "RAS-Clash Analysis"),
            new RibbonButton("ModelHistory.Settings", "RAS-Model History"),
            new RibbonButton("ModelHistory.Journal", "RAS-Model History"),
        };

        private static Dictionary<string, bool> Enabled(string user, List<string> warnings = null)
        {
            var file = PluginFile.Split(Sample.Split('\n'), w => warnings?.Add(w));
            var people = ButtonAccess.People(file.People, w => warnings?.Add(w));
            var layout = RibbonLayout.Parse(file.Layout, w => warnings?.Add(w));
            var plan = RibbonLayout.Arrange(layout, Buttons, w => warnings?.Add(w), "Core.About", a => ButtonAccess.Allowed(a, user, people));
            return plan.Panels.SelectMany(p => p.Slots).SelectMany(s => s.Entries).ToDictionary(e => e.Id, e => e.Enabled);
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("один файл: модули, люди и лента — каждый своё, номера строк настоящие", () =>
            {
                var warnings = new List<string>();
                var file = PluginFile.Split(Sample.Split('\n'), warnings.Add);
                var modules = ModuleSwitches.Parse(file.Modules, warnings.Add);
                Check(ModuleSwitches.ModeOf(modules, "ClashAnalysis") == ModuleMode.TestOnly && ModuleSwitches.ModeOf(modules, "ModelHistory") == ModuleMode.Works, "режимы модулей");
                var people = ButtonAccess.People(file.People, warnings.Add);
                Check(people.Count == 1 && people["руководители"].SequenceEqual(new[] { "Ivanov_A", "Petrov_B" }), "группа: домен «RAS\\» не важен");
                var layout = RibbonLayout.Parse(file.Layout, warnings.Add);
                Check(layout.Count == 2 && layout[1].Title == "Менеджер коллизий", "панели раскладки: " + layout.Count);
                Check(warnings.Count == 0, "предупреждения: " + string.Join("; ", warnings));
                Check(file.Layout.Length == file.Modules.Length && file.Modules[2].Contains("ModelHistory") && file.Layout[2] == "", "строки на своих местах");
            });
            yield return Case("кнопка видна всегда; кому недоступна — серая", () =>
            {
                var boss = Enabled("Ivanov_A");
                Check(boss["ClashAnalysis.ElementClashes"] && boss["ClashAnalysis.ViewSettings"], "руководителю — активна");
                var other = Enabled("Sidorov_C");
                Check(other.ContainsKey("ClashAnalysis.ElementClashes") && !other["ClashAnalysis.ElementClashes"], "остальным — серая, но на ленте");
                Check(!other["ModelHistory.Settings"] && !boss["ModelHistory.Settings"], "«никому» — серая у всех");
                Check(other["ModelHistory.Journal"] && other["Core.About"], "без «доступна» и не вписанная в раскладку — активна у всех");
                Check(Enabled("ras\\ivanov_a")["ClashAnalysis.ElementClashes"], "логин — без учёта регистра и домена");
            });
            yield return Case("«доступна»: логины прямо в кнопке, «всем», «никому», группы", () =>
            {
                var people = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["бим"] = new List<string> { "Poletayev_BIM" } };
                Check(ButtonAccess.Allowed(null, "x", people) && ButtonAccess.Allowed("всем", "x", people), "не указано / всем");
                Check(!ButtonAccess.Allowed("никому", "Poletayev_BIM", people), "никому");
                Check(ButtonAccess.Allowed("Ivanov_A, бим", "Poletayev_BIM", people) && ButtonAccess.Allowed("Ivanov_A, бим", "ivanov_a", people), "логин и группа вместе");
                Check(!ButtonAccess.Allowed("руководтели", "Ivanov_A", people), "опечатка в группе — серая (видно в «О плагине»), а не открыта всем");
            });
            yield return Case("«О плагине» (переключатель отделов) серым не бывает", () =>
            {
                var warnings = new List<string>();
                var layout = RibbonLayout.Parse(new[] { "[Плагин]", "Core.About доступна=\"никому\"" }, warnings.Add);
                var plan = RibbonLayout.Arrange(layout, Buttons, warnings.Add, "Core.About", a => false);
                Check(plan.Panels[0].Slots[0].Entries[0].Enabled && warnings.Any(w => w.Contains("доступна")), "активна и предупреждение");
            });
            yield return Case("ошибки в блоках не ломают остальное", () =>
            {
                var warnings = new List<string>();
                var file = PluginFile.Split(("{Модули}\nClashAnalysis = без кнопок\n{Настройки}\nчто-то\n{Люди}\nбез равно\nвсем = Ivanov_A\n[Плагин]\nCore.About").Split('\n'), warnings.Add);
                var modules = ModuleSwitches.Parse(file.Modules, warnings.Add);
                var people = ButtonAccess.People(file.People, warnings.Add);
                Check(ModuleSwitches.ModeOf(modules, "ClashAnalysis") == ModuleMode.Works, "«без кнопок» больше нет — модуль работает");
                Check(people.Count == 0, "служебное слово и строка без «=» — не группы");
                Check(warnings.Count == 4 && warnings.Any(w => w.Contains("без кнопок")) && warnings.Any(w => w.Contains("{Настройки}")), "предупреждения: " + string.Join("; ", warnings));
                Check(RibbonLayout.Parse(file.Layout, warnings.Add).Count == 1, "лента после блоков — на месте");
            });
        }
    }
}
