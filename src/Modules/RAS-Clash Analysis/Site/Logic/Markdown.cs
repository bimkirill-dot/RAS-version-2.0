using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Markdown → HTML для документов модуля на сайте («Регламент выгрузки отчётов»): заголовки, абзацы, списки «- », таблицы,
    /// блоки кода ```, **полужирный**, `код`. Только то, что есть в наших документах, — без сторонних библиотек; весь текст
    /// экранируется (документ не может вставить на страницу свою разметку). Проверяется тестами.
    /// </summary>
    public static class Markdown
    {
        private static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(@"^(#{1,4})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex CodeMark = new Regex("\u0001(\\d+)\u0002", RegexOptions.Compiled);
        private static readonly Regex TableRule = new Regex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

        public static string ToHtml(string markdown)
        {
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            var html = new StringBuilder();
            var paragraph = new List<string>();
            var i = 0;

            void EndParagraph()
            {
                if (paragraph.Count == 0) return;
                html.Append("<p>").Append(Inline(string.Join(" ", paragraph))).Append("</p>\n");
                paragraph.Clear();
            }

            while (i < lines.Length)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    EndParagraph();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                    i++;
                    html.Append("<pre>").Append(Escape(string.Join("\n", code).TrimEnd())).Append("</pre>\n");
                    continue;
                }
                if (trimmed.Length == 0)
                {
                    EndParagraph();
                    i++;
                    continue;
                }
                var heading = Heading.Match(trimmed);
                if (heading.Success)
                {
                    EndParagraph();
                    var level = heading.Groups[1].Value.Length;
                    html.Append("<h").Append(level).Append('>').Append(Inline(heading.Groups[2].Value)).Append("</h").Append(level).Append(">\n");
                    i++;
                    continue;
                }
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    EndParagraph();
                    html.Append("<ul>\n");
                    while (i < lines.Length && lines[i].Trim().StartsWith("- ", StringComparison.Ordinal))
                    {
                        var item = lines[i].Trim().Substring(2);
                        i++;
                        // продолжение пункта — строка с отступом
                        while (i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0
                               && !lines[i].Trim().StartsWith("- ", StringComparison.Ordinal))
                            item += " " + lines[i++].Trim();
                        html.Append("<li>").Append(Inline(item)).Append("</li>\n");
                    }
                    html.Append("</ul>\n");
                    continue;
                }
                if (trimmed.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length && TableRule.IsMatch(lines[i + 1].Trim()))
                {
                    EndParagraph();
                    html.Append("<table>\n<thead><tr>");
                    foreach (var cell in Cells(trimmed)) html.Append("<th>").Append(Inline(cell)).Append("</th>");
                    html.Append("</tr></thead>\n<tbody>\n");
                    i += 2;
                    while (i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal))
                    {
                        html.Append("<tr>");
                        foreach (var cell in Cells(lines[i].Trim())) html.Append("<td>").Append(Inline(cell)).Append("</td>");
                        html.Append("</tr>\n");
                        i++;
                    }
                    html.Append("</tbody>\n</table>\n");
                    continue;
                }
                paragraph.Add(trimmed);
                i++;
            }
            EndParagraph();
            return html.ToString();
        }

        /// <summary>Экранировать только то, что может стать разметкой (WebUtility.HtmlEncode превращает и «ёлочки» в коды).</summary>
        public static string Escape(string text) =>
            (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>Ячейки строки таблицы «| a | b |».</summary>
        private static IEnumerable<string> Cells(string row)
        {
            var inner = row.Trim();
            if (inner.StartsWith("|", StringComparison.Ordinal)) inner = inner.Substring(1);
            if (inner.EndsWith("|", StringComparison.Ordinal)) inner = inner.Substring(0, inner.Length - 1);
            foreach (var cell in inner.Split('|')) yield return cell.Trim();
        }

        /// <summary>
        /// Строка: `код` — как есть, остальное — экранировать и **полужирный** (полужирный может включать код: «**папку `x` не
        /// удалять**» — поэтому код сначала откладывается метками, полужирный — по всей строке, потом код возвращается).
        /// </summary>
        public static string Inline(string text)
        {
            var parts = (text ?? "").Split('`');
            var codes = new List<string>();
            var sb = new StringBuilder();
            for (var k = 0; k < parts.Length; k++)
            {
                // нечётные части — внутри `…`; непарная кавычка — обычный знак
                if (k % 2 == 1 && k < parts.Length - 1)
                {
                    sb.Append('\u0001').Append(codes.Count).Append('\u0002');
                    codes.Add("<code>" + Escape(parts[k]) + "</code>");
                }
                else
                {
                    if (k % 2 == 1) sb.Append('`');
                    sb.Append(Escape(parts[k].Replace("\u0001", "").Replace("\u0002", "")));
                }
            }
            var html = Bold.Replace(sb.ToString(), "<strong>$1</strong>");
            return CodeMark.Replace(html, m => codes[int.Parse(m.Groups[1].Value)]);
        }
    }
}
