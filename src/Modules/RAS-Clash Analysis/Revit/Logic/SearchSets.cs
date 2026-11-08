using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Xml.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Правило «Скрыть» — поисковый набор Navisworks (разработчик, 03.11.2026: «параметры поиска элементов, которые можно получить
    /// из инструмента Navisworks: импортируем и вставляем сюда; плагин их находит и скрывает»): название, условия по-русски для
    /// окна и сам набор — как в файле экспорта наборов Navisworks (по нему NWF найдёт элементы).
    /// </summary>
    [DataContract]
    public sealed class HideSet
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        /// <summary>Условия текстом: «Элемент › Имя содержит «временн»».</summary>
        [DataMember(Order = 2)] public string Text { get; set; }
        /// <summary>Элемент &lt;selectionset&gt; файла экспорта — без изменений.</summary>
        [DataMember(Order = 3)] public string Xml { get; set; }

        public HideSet Clone() => (HideSet)MemberwiseClone();
    }

    /// <summary>
    /// Файл «Экспорт поисковых наборов» Navisworks (Наборы → Экспорт поисковых наборов…, XML &lt;exchange&gt;&lt;selectionsets&gt;):
    /// поисковые наборы (с &lt;findspec&gt;) — правила; простые наборы выбора (список элементов) не годятся — их нет в другой
    /// модели. Папки наборов просматриваются вглубь.
    /// </summary>
    public static class SearchSets
    {
        /// <summary>
        /// Поисковые наборы файла; skipped — сколько простых наборов выбора пропущено. Не тот файл — InvalidDataException. Годится и
        /// экспорт самого поиска («Найти элементы» → экспорт; разработчик, 08.11.2026: «это файл, который получаешь из Navisworks через
        /// скрыть элементы») — &lt;exchange&gt;&lt;findspec&gt; без набора: правило с названием name (имя файла).
        /// </summary>
        public static List<HideSet> Read(Stream stream, out int skipped, string name = null)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(stream);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is IOException)
            {
                throw new InvalidDataException("Это не файл поисковых наборов Navisworks.", ex);
            }
            var sets = doc.Root?.Element("selectionsets");
            var searches = doc.Root?.Elements("findspec").ToList() ?? new List<XElement>();
            if (doc.Root?.Name.LocalName != "exchange" || sets == null && searches.Count == 0)
                throw new InvalidDataException("Это не файл Navisworks с условиями поиска: нужен экспорт поисковых наборов (Наборы → Экспорт " +
                                               "поисковых наборов) или поиска («Найти элементы» → Экспорт).");
            var result = new List<HideSet>();
            skipped = 0;
            // экспорт самого поиска — набором с названием по файлу (в NWF ищется так же)
            var title = string.IsNullOrWhiteSpace(name) ? "Поиск" : name.Trim();
            for (var i = 0; i < searches.Count; i++)
            {
                var set = new XElement("selectionset", new XAttribute("name", searches.Count == 1 ? title : title + " " + (i + 1)), new XElement(searches[i]));
                result.Add(new HideSet { Name = (string)set.Attribute("name"), Text = Describe(searches[i]), Xml = set.ToString(SaveOptions.DisableFormatting) });
            }
            if (sets == null) return result;
            foreach (var set in sets.Descendants("selectionset"))
            {
                var spec = set.Element("findspec");
                if (spec == null)
                {
                    skipped++;
                    continue;
                }
                result.Add(new HideSet { Name = (string)set.Attribute("name") ?? "Без названия", Text = Describe(spec), Xml = set.ToString(SaveOptions.DisableFormatting) });
            }
            return result;
        }

        /// <summary>Условия набора по-русски: «Элемент › Имя содержит «временн»; Элемент › Тип = «Стена»».</summary>
        public static string Describe(XElement findspec)
        {
            var conditions = findspec.Element("conditions")?.Elements("condition").ToList() ?? new List<XElement>();
            if (conditions.Count == 0) return "все элементы";
            return string.Join("; ", conditions.Select(c =>
            {
                var category = Shown(c.Element("category"));
                var property = Shown(c.Element("property"));
                var test = (string)c.Attribute("test") ?? "";
                var value = (string)c.Element("value")?.Element("data") ?? "";
                var where = category.Length > 0 && property.Length > 0 ? category + " › " + property : category + property;
                return Tests.TryGetValue(test, out var word)
                    ? where + " " + word + (NoValue.Contains(test) ? "" : " «" + value + "»")
                    : where + " " + test + " «" + value + "»";
            }));
        }

        // название, как в окне Navisworks; нет — внутреннее
        private static string Shown(XElement e)
        {
            var name = e?.Element("name");
            return ((string)name ?? (string)name?.Attribute("internal") ?? "").Trim();
        }

        private static readonly Dictionary<string, string> Tests = new Dictionary<string, string>
        {
            ["equals"] = "=", ["not_equals"] = "≠", ["contains"] = "содержит", ["wildcard"] = "по шаблону",
            ["defined"] = "задано", ["undefined"] = "не задано", ["greater_than"] = ">", ["less_than"] = "<",
            ["greater_than_or_equal"] = "≥", ["less_than_or_equal"] = "≤",
        };

        private static readonly HashSet<string> NoValue = new HashSet<string> { "defined", "undefined" };

        /// <summary>
        /// Правило из прежней строки «имя элемента содержит …» (до 03.11.2026): тот же поиск, что набор Navisworks «Элемент › Имя
        /// содержит …».
        /// </summary>
        public static HideSet FromName(string text)
        {
            var v = SecurityElement.Escape(text ?? "");
            var xml = "<selectionset name=\"Имя содержит «" + v + "»\"><findspec mode=\"all\" disjoint=\"0\"><conditions>" +
                      "<condition test=\"contains\" flags=\"10\"><category><name internal=\"LcOaNode\">Элемент</name></category>" +
                      "<property><name internal=\"LcOaSceneBaseUserName\">Имя</name></property><value><data type=\"wstring\">" + v +
                      "</data></value></condition></conditions><locator>/</locator></findspec></selectionset>";
            return new HideSet { Name = "Имя содержит «" + text + "»", Text = "Элемент › Имя содержит «" + text + "»", Xml = xml };
        }
    }
}
