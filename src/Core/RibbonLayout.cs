using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Кнопка на ленте: код, подпись (null — как задал модуль, «» — без подписи, одна иконка) и кому доступна («доступна="…"»;
    /// null — всем). Enabled — доступна ли этому человеку (ButtonAccess, при построении плана): нет — кнопка серая.
    /// </summary>
    internal sealed class RibbonEntry
    {
        public RibbonEntry(string id, string label, string access = null, bool enabled = true)
        {
            Id = id;
            Label = label;
            Access = access;
            Enabled = enabled;
        }

        public string Id { get; }

        public string Label { get; }

        public string Access { get; }

        public bool Enabled { get; }
    }

    /// <summary>Место на панели: одна кнопка — большая; 2–3 — стопка маленьких в одну колонку.</summary>
    internal sealed class RibbonSlot
    {
        public List<RibbonEntry> Entries { get; } = new List<RibbonEntry>();

        public bool IsStack => Entries.Count > 1;
    }

    /// <summary>Панель: отдел (null — видна во всех отделах), название, места по порядку слева направо.</summary>
    internal sealed class RibbonPanelSpec
    {
        public RibbonPanelSpec(string department, string title)
        {
            Department = department;
            Title = title;
        }

        public string Department { get; }

        public string Title { get; }

        public List<RibbonSlot> Slots { get; } = new List<RibbonSlot>();
    }

    /// <summary>Что строить на ленте: панели и отделы (пусто — переключателя отделов нет).</summary>
    internal sealed class RibbonPlan
    {
        public List<RibbonPanelSpec> Panels { get; } = new List<RibbonPanelSpec>();

        public List<string> Departments { get; } = new List<string>();
    }

    /// <summary>
    /// Раскладка кнопок по отделам и панелям (PRINCIPLES.md, раздел 12; src\Core\DECISIONS.md). Модули объявляют кнопки,
    /// а где они стоят, задаёт файл ribbon.txt в сборке (src\Core\ribbon.txt):
    /// <code>
    /// [Плагин]                                 — панель до первого отдела: видна во всех отделах
    /// Core.About
    /// [[Конструктив]]                          — отдел
    /// [История модели]                         — его панель
    /// ModelHistory.Journal подпись="Журнал\nизменений"
    /// (                                        — стопка: 2–3 маленькие кнопки в одну колонку
    /// ModelHistory.History подпись="История"
    /// ModelHistory.Settings подпись=""         — без подписи, одна иконка
    /// )
    /// </code>
    /// Кнопка, которой нет в раскладке, — на панели с названием модуля, во всех отделах: новый модуль виден сразу.
    /// Ошибки в файле ленту не ломают: строка пропускается, причина — в warn. Без Revit API: проверяется тестами.
    /// </summary>
    internal static class RibbonLayout
    {
        public const string FileName = "ribbon.txt";

        /// <summary>В Revit в стопке не больше трёх кнопок.</summary>
        public const int MaxStack = 3;

        private static readonly Regex Attribute = new Regex(@"(\S+?)\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

        /// <summary>Разобрать раскладку: панели в порядке файла (одинаковые панели одного отдела — одна).</summary>
        public static List<RibbonPanelSpec> Parse(IEnumerable<string> lines, Action<string> warn)
        {
            var panels = new List<RibbonPanelSpec>();
            string department = null;
            RibbonPanelSpec panel = null;
            RibbonSlot stack = null;
            var number = 0;

            void CloseStack(string why)
            {
                if (stack == null) return;
                if (why != null) warn("раскладка ленты, строка " + number + ": стопка не закрыта «)» — " + why);
                stack = null;
            }

            foreach (var raw in lines)
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
                {
                    CloseStack("закрыта перед отделом");
                    var name = line.Substring(2, line.Length - 4).Trim();
                    if (name.Length == 0) warn("раскладка ленты, строка " + number + ": пустое название отдела — его панели видны во всех отделах");
                    department = name.Length == 0 ? null : name;
                    panel = null;
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    CloseStack("закрыта перед панелью");
                    var title = line.Substring(1, line.Length - 2).Trim();
                    if (title.Length == 0)
                    {
                        warn("раскладка ленты, строка " + number + ": пустое название панели");
                        panel = null;
                        continue;
                    }
                    panel = panels.FirstOrDefault(p => Same(p.Department, department) && Same(p.Title, title));
                    if (panel == null) panels.Add(panel = new RibbonPanelSpec(department, title));
                    continue;
                }
                if (line == "(")
                {
                    if (stack != null) warn("раскладка ленты, строка " + number + ": стопка внутри стопки — «(» пропущена");
                    else if (panel == null) warn("раскладка ленты, строка " + number + ": стопка до первой панели — пропущена");
                    else panel.Slots.Add(stack = new RibbonSlot());
                    continue;
                }
                if (line == ")")
                {
                    if (stack == null) warn("раскладка ленты, строка " + number + ": «)» без «(» — пропущена");
                    stack = null;
                    continue;
                }

                var entry = ParseButton(line, number, warn);
                if (entry == null) continue;
                if (panel == null)
                {
                    warn("раскладка ленты, строка " + number + ": кнопка «" + entry.Id + "» до первой панели — пропущена");
                    continue;
                }
                if (stack != null) stack.Entries.Add(entry);
                else
                {
                    var slot = new RibbonSlot();
                    slot.Entries.Add(entry);
                    panel.Slots.Add(slot);
                }
            }
            CloseStack("в конце файла");
            return panels;
        }

        /// <summary>«Код подпись="…"»: подпись — в кавычках, \n — перенос строки, \" — кавычка, \\ — обратная косая.</summary>
        private static RibbonEntry ParseButton(string line, int number, Action<string> warn)
        {
            var space = line.IndexOfAny(new[] { ' ', '\t' });
            var id = space < 0 ? line : line.Substring(0, space);
            var rest = space < 0 ? "" : line.Substring(space + 1).Trim();
            string label = null, access = null;
            var consumed = new StringBuilder(rest);
            foreach (Match m in Attribute.Matches(rest))
            {
                var key = m.Groups[1].Value;
                if (string.Equals(key, "подпись", StringComparison.OrdinalIgnoreCase)) label = Unescape(m.Groups[2].Value);
                else if (string.Equals(key, "доступна", StringComparison.OrdinalIgnoreCase)) access = Unescape(m.Groups[2].Value).Trim();
                else if (string.Equals(key, "размер", StringComparison.OrdinalIgnoreCase))
                    warn("раскладка ленты, строка " + number + ": «размер» не нужен — маленькие кнопки задаются стопкой «( … )»");
                else warn("раскладка ленты, строка " + number + ": непонятное «" + key + "» — пропущено");
                consumed.Replace(m.Value, new string(' ', m.Value.Length));
            }
            if (consumed.ToString().Trim().Length > 0)
                warn("раскладка ленты, строка " + number + ": не понято «" + consumed.ToString().Trim() + "» (подпись — в кавычках: подпись=\"…\")");
            return new RibbonEntry(id, label, access);
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                var next = s[++i];
                sb.Append(next == 'n' ? '\n' : next);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Что строить: в панелях раскладки — только кнопки, которые есть (модуль мог не запуститься или не работать в этой
        /// версии Revit — это не ошибка); стопка без пропавших кнопок, из одной кнопки — большая кнопка, больше трёх —
        /// первые три в стопке, остальные большие; пустые панели и отделы без панелей не строятся. Кнопки без раскладки —
        /// на панелях модулей, во всех отделах. selectorId («Core.About») — где стоит переключатель отделов: если отделы
        /// есть, он обязан быть на панели, видной во всех отделах, иначе из чужого отдела не вернуться.
        /// </summary>
        /// <param name="allowed">Доступна ли кнопка этому человеку по её «доступна=» (null — все доступны); недоступная — серая.</param>
        public static RibbonPlan Arrange(IEnumerable<RibbonPanelSpec> layout, IEnumerable<RibbonButton> buttons, Action<string> warn,
            string selectorId = null, Func<string, bool> allowed = null)
        {
            var available = buttons.ToList();
            var byId = available.GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placedIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // «отдел|код» — кнопка дважды в одном отделе
            var plan = new RibbonPlan();

            foreach (var source in layout)
            {
                var target = new RibbonPanelSpec(source.Department, source.Title);
                foreach (var slot in source.Slots)
                {
                    var entries = new List<RibbonEntry>();
                    foreach (var e in slot.Entries)
                    {
                        if (!byId.TryGetValue(e.Id, out var button)) continue;
                        if (!placedIn.Add((source.Department ?? "") + "|" + button.Id))
                        {
                            warn("раскладка ленты: кнопка «" + e.Id + "» указана дважды" +
                                 (source.Department == null ? "" : " в отделе «" + source.Department + "»") + " — стоит на первом месте");
                            continue;
                        }
                        placed.Add(button.Id);
                        // переключатель отделов («О плагине») серым не бывает: иначе из чужого отдела не вернуться
                        var enabled = Same(button.Id, selectorId) || e.Access == null || allowed == null || allowed(e.Access);
                        if (e.Access != null && Same(button.Id, selectorId))
                            warn("раскладка ленты: «" + selectorId + "» доступна всем всегда — «доступна» у неё не действует");
                        entries.Add(new RibbonEntry(button.Id, e.Label, e.Access, enabled));
                    }
                    if (slot.IsStack && entries.Count > MaxStack)
                        warn("раскладка ленты: в стопке больше " + MaxStack + " кнопок — «" + string.Join("», «", entries.Skip(MaxStack).Select(x => x.Id)) +
                             "» большими кнопками после неё");
                    var stacked = new RibbonSlot();
                    stacked.Entries.AddRange(entries.Take(MaxStack));
                    if (stacked.Entries.Count > 0) target.Slots.Add(stacked); // из одной кнопки — большая (IsStack == false)
                    foreach (var extra in entries.Skip(MaxStack))
                    {
                        var single = new RibbonSlot();
                        single.Entries.Add(extra);
                        target.Slots.Add(single);
                    }
                }
                if (target.Slots.Count > 0) plan.Panels.Add(target);
            }

            foreach (var d in plan.Panels.Where(p => p.Department != null).Select(p => p.Department))
                if (!plan.Departments.Contains(d, StringComparer.OrdinalIgnoreCase)) plan.Departments.Add(d);

            // переключатель отделов — только на панели, видной во всех отделах
            if (selectorId != null && plan.Departments.Count > 0 && byId.ContainsKey(selectorId) &&
                !plan.Panels.Any(p => p.Department == null && p.Slots.Any(s => s.Entries.Any(e => Same(e.Id, selectorId)))))
            {
                warn("раскладка ленты: «" + selectorId + "» (переключатель отделов) должна стоять на панели до первого отдела — " +
                     "поставлена на панель «" + byId[selectorId].DefaultPanel + "», видную во всех отделах");
                placed.Remove(selectorId);
            }

            foreach (var button in available)
            {
                if (placed.Contains(button.Id)) continue;
                placed.Add(button.Id);
                var target = plan.Panels.FirstOrDefault(p => p.Department == null && Same(p.Title, button.DefaultPanel));
                if (target == null) plan.Panels.Add(target = new RibbonPanelSpec(null, button.DefaultPanel));
                var slot = new RibbonSlot();
                slot.Entries.Add(new RibbonEntry(button.Id, null));
                target.Slots.Add(slot);
            }
            return plan;
        }

        /// <summary>Видна ли панель в отделе (null — отделов нет: видны все).</summary>
        public static bool IsVisible(RibbonPanelSpec panel, string department) =>
            panel.Department == null || department == null || Same(panel.Department, department);

        /// <summary>Отдел при запуске: сохранённый выбор человека, если такой отдел ещё есть, иначе — первый.</summary>
        public static string StartDepartment(RibbonPlan plan, string saved) =>
            plan.Departments.FirstOrDefault(d => Same(d, saved)) ?? plan.Departments.FirstOrDefault();

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
