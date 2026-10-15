using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: категории проверок (TestCategories).</summary>
    internal static class ClashCategoryTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static Stream Xml(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("название проверки — без учёта регистра и пробелов, пробелы вокруг «/» не важны", () =>
            {
                var k = TestCategories.Key("АР Стены/МЕР Трубы");
                Check(k == TestCategories.Key("ар стены / мер  трубы"), "пробелы и регистр: " + k);
                Check(k == TestCategories.Key("  АР  Стены/МЕР Трубы "), "по краям");
                Check(k != TestCategories.Key("АР Стены/МЕР Воздуховоды"), "другая проверка");
                Check(TestCategories.Key("КЖ Горизонтальные конструкции") != TestCategories.Key("КЖГоризонтальные конструкции"), "пробел внутри слова — важен");
            });
            yield return Case("названия проверок из XML — отчёта и файла проверок Clash Detective", () =>
            {
                var report = "<exchange><batchtest><clashtests>"
                             + "<clashtest name=\"АР Стены/МЕР Трубы\"><clashresults /></clashtest>"
                             + "<clashtest name=\"КЖ Плиты/МЕР Воздуховоды\" />"
                             + "<clashtest name=\"ар стены / мер трубы\" />"
                             + "</clashtests></batchtest></exchange>";
                var names = TestCategories.NamesFromXml(Xml(report));
                Check(names.Count == 2 && names[0] == "АР Стены/МЕР Трубы" && names[1] == "КЖ Плиты/МЕР Воздуховоды", "без повторов: " + string.Join(" | ", names));
            });
            yield return Case("не XML или без проверок — понятная причина", () =>
            {
                string Error(string text)
                {
                    try { TestCategories.NamesFromXml(Xml(text)); }
                    catch (InvalidDataException ex) { return ex.Message; }
                    return null;
                }
                Check(Error("<exchange></exchange>")?.Contains("нет проверок") == true, "без проверок");
                Check(Error("<exchange><clashtest")?.Contains("не читается как XML") == true, "обрыв");
            });
        }
    }
}
