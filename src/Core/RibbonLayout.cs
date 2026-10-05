using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.Core
{
    /// <summary>Кнопка, которую объявил модуль (или ядро): код и панель, куда она попадает, если её нет в раскладке.</summary>
    internal sealed class RibbonButton
    {
        public RibbonButton(string id, string defaultPanel)
        {
            Id = id;
            DefaultPanel = defaultPanel;
        }

        /// <summary>«&lt;модуль&gt;.&lt;кнопка&gt;», например «ModelHistory.Journal».</summary>
        public string Id { get; }

        public string DefaultPanel { get; }
    }

    /// <summary>Панель вкладки и коды кнопок на ней — по порядку.</summary>
    internal sealed class RibbonPanelSpec
    {
        public RibbonPanelSpec(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public List<string> Buttons { get; } = new List<string>();
    }

    /// <summary>
    /// Раскладка кнопок по панелям вкладки (PRINCIPLES.md, раздел 12). Модули объявляют кнопки, а на какой панели
    /// и в каком порядке они стоят, задаёт файл ribbon.txt в сборке (src\Core\ribbon.txt):
    /// <code>
    /// # комментарий
    /// [Общие]
    /// ModelHistory.Journal
    /// Core.About
    /// </code>
    /// Кнопка, которой нет в раскладке, попадает на панель по умолчанию (с названием модуля) — новый модуль виден сразу.
    /// Без Revit API: проверяется тестами.
    /// </summary>
    internal static class RibbonLayout
    {
        public const string FileName = "ribbon.txt";

        /// <summary>Разобрать раскладку. Ошибки в файле не мешают: строка пропускается, причина — в warn.</summary>
        public static List<RibbonPanelSpec> Parse(IEnumerable<string> lines, Action<string> warn)
        {
            var panels = new List<RibbonPanelSpec>();
            RibbonPanelSpec current = null;
            var number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    var title = line.Substring(1, line.Length - 2).Trim();
                    if (title.Length == 0)
                    {
                        warn("раскладка ленты, строка " + number + ": пустое название панели");
                        current = null;
                        continue;
                    }
                    // одна панель, упомянутая дважды, — одна панель на ленте
                    current = Find(panels, title);
                    if (current == null) panels.Add(current = new RibbonPanelSpec(title));
                    continue;
                }
                if (current == null)
                {
                    warn("раскладка ленты, строка " + number + ": кнопка «" + line + "» до первой панели");
                    continue;
                }
                current.Buttons.Add(line);
            }
            return panels;
        }

        /// <summary>
        /// Что создать на ленте: сначала панели раскладки (в её порядке, только кнопки, которые есть в сборке),
        /// затем не попавшие в раскладку кнопки — на свои панели по умолчанию. Пустые панели не создаются.
        /// Кнопки раскладки, которых нет, не ошибка: модуль мог не запуститься или не работать в этой версии Revit.
        /// </summary>
        public static List<RibbonPanelSpec> Arrange(IEnumerable<RibbonPanelSpec> layout, IEnumerable<RibbonButton> buttons, Action<string> warn)
        {
            var available = buttons.ToList();
            var known = new HashSet<string>(available.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<RibbonPanelSpec>();

            foreach (var panel in layout)
            {
                var target = Find(result, panel.Title);
                if (target == null) result.Add(target = new RibbonPanelSpec(panel.Title));
                foreach (var id in panel.Buttons)
                {
                    if (!known.Contains(id)) continue;
                    if (!placed.Add(id))
                    {
                        warn("раскладка ленты: кнопка «" + id + "» указана дважды, стоит на первом месте");
                        continue;
                    }
                    target.Buttons.Add(available.First(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)).Id);
                }
            }

            foreach (var button in available)
            {
                if (!placed.Add(button.Id)) continue;
                var target = Find(result, button.DefaultPanel);
                if (target == null) result.Add(target = new RibbonPanelSpec(button.DefaultPanel));
                target.Buttons.Add(button.Id);
            }

            result.RemoveAll(p => p.Buttons.Count == 0);
            return result;
        }

        private static RibbonPanelSpec Find(List<RibbonPanelSpec> panels, string title) =>
            panels.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
    }
}
