using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Категории проверок (DECISIONS.md модуля, «Категории проверок»): деление проверок, например по критичности, — по
    /// названию проверки, на весь сайт. Проверка — в одной категории. Без базы — проверяется тестами.
    /// </summary>
    public static class TestCategories
    {
        /// <summary>
        /// Цвета категорий и подкатегорий — готовая палитра (ключ → название); на сайте ключ — переменная CSS «--cat-ключ»
        /// со своим оттенком для светлой и тёмной темы. Строка коллизии помечается тонкой полосой этого цвета.
        /// </summary>
        public static readonly (string Key, string Title)[] Colors =
        {
            // по кругу оттенков; прежние восемь ключей — без изменений (уже сохранены в базе)
            ("red", "красный"), ("crimson", "малиновый"), ("pink", "розовый"), ("orange", "оранжевый"), ("amber", "янтарный"),
            ("yellow", "жёлтый"), ("lime", "лаймовый"), ("olive", "оливковый"), ("green", "зелёный"), ("teal", "бирюзовый"),
            ("cyan", "голубой"), ("blue", "синий"), ("navy", "тёмно-синий"), ("indigo", "индиго"), ("violet", "фиолетовый"),
            ("brown", "коричневый"), ("graphite", "графит"), ("gray", "серый"),
        };

        public static bool IsColor(string key) => key != null && Array.Exists(Colors, c => c.Key == key);

        public static string ColorTitle(string key) => Array.Find(Colors, c => c.Key == key).Title ?? key;

        /// <summary>
        /// Ключ названия проверки для сравнения: без учёта регистра и лишних пробелов, пробелы вокруг «/» не важны —
        /// «АР Стены/МЕР Трубы» = «ар стены / мер  трубы».
        /// </summary>
        public static string Key(string testName)
        {
            if (string.IsNullOrWhiteSpace(testName)) return "";
            var sb = new StringBuilder(testName.Length);
            var space = false;
            foreach (var c in testName.Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (c == '/')
                {
                    space = false;
                    sb.Append('/');
                    continue;
                }
                if (space && sb.Length > 0 && sb[sb.Length - 1] != '/') sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Названия проверок из XML Navisworks — отчёта или файла проверок, выгруженного из Clash Detective: каждый
        /// <c>clashtest name="…"</c>, без повторов, в порядке файла. Не XML — InvalidDataException с причиной по-русски.
        /// </summary>
        public static List<string> NamesFromXml(Stream xml)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            try
            {
                using (var r = XmlReader.Create(xml, settings))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "clashtest")
                        {
                            var name = (r.GetAttribute("name") ?? "").Trim();
                            if (name.Length > 0 && seen.Add(Key(name))) names.Add(name);
                        }
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("Файл не читается как XML: " + ex.Message, ex);
            }
            if (names.Count == 0) throw new InvalidDataException("В файле нет проверок Navisworks (clashtest).");
            return names;
        }
    }
}
