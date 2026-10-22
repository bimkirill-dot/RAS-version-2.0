using System;
using System.Collections.Generic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis, сайт: документы модуля на странице (Markdown → HTML).</summary>
    internal static class SiteMarkdownTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("документ: заголовки, абзац из нескольких строк, полужирный и код", () =>
            {
                var html = Markdown.ToHtml("# Регламент\n\nПервая строка\nвторая **важно** и `S1\\14.10.2026`.\n\n## Папки");
                Check(html.Contains("<h1>Регламент</h1>") && html.Contains("<h2>Папки</h2>"), "заголовки: " + html);
                Check(html.Contains("<p>Первая строка вторая <strong>важно</strong> и <code>S1\\14.10.2026</code>.</p>"), "абзац: " + html);
            });
            yield return Case("документ: список и таблица", () =>
            {
                var html = Markdown.ToHtml("- один\n- **два**\n\n| Что видно | Что сделать |\n|---|---|\n| «Нет отчёта» | Выгрузить |\n| а | б |\n");
                Check(html.Contains("<ul>\n<li>один</li>\n<li><strong>два</strong></li>\n</ul>"), "список: " + html);
                Check(html.Contains("<th>Что видно</th><th>Что сделать</th>") && html.Contains("<td>«Нет отчёта»</td><td>Выгрузить</td>") && html.Contains("<td>а</td><td>б</td>"), "таблица: " + html);
            });
            yield return Case("документ: блок кода — как есть, без разметки внутри", () =>
            {
                var html = Markdown.ToHtml("```\n<папка проекта>\\S1\n   **не полужирный**\n```\nпосле");
                Check(html.Contains("<pre>&lt;папка проекта&gt;\\S1\n   **не полужирный**</pre>") && html.Contains("<p>после</p>"), "код: " + html);
            });
            yield return Case("документ: полужирный с кодом внутри", () =>
            {
                var html = Markdown.Inline("- **Папку `<отчёт>_files` не удалять** — иначе");
                Check(html == "- <strong>Папку <code>&lt;отчёт&gt;_files</code> не удалять</strong> — иначе", "строка: " + html);
            });
            yield return Case("документ: разметка из текста не проходит на страницу", () =>
            {
                var html = Markdown.ToHtml("<script>alert(1)</script> и `<b>`");
                Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("<code>&lt;b&gt;</code>"), "экранировано: " + html);
            });
        }
    }
}
