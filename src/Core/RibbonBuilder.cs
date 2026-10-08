using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using AW = Autodesk.Windows;

namespace RASv2.Core
{
    /// <summary>
    /// Лента в Revit по плану раскладки (RibbonLayout): панели отделов, большие кнопки и стопки маленьких, свои подписи,
    /// переключатель отделов на месте «О плагине». Отделы переключаются скрытием панелей. Панели разных отделов —
    /// разные панели Revit (у панели Revit имя на вкладке уникально), поэтому одна кнопка может стоять в нескольких
    /// отделах — у каждого своя копия. Выбранный отдел запоминается у человека (%AppData%\RAS_v_2.0\department.txt).
    /// Ошибка одной кнопки или панели не мешает остальным.
    /// </summary>
    internal static class RibbonBuilder
    {
        private const string Tag = "Ядро";
        public const string SelectorId = "Core.About";
        /// <summary>Сколько раз в простое Revit искать кнопки без подписи, если при запуске лента ещё не была готова.</summary>
        private const int HideTextAttempts = 200;

        /// <summary>Кнопки на ленте по кодам (у кнопки в нескольких отделах — несколько).</summary>
        private static readonly Dictionary<string, List<RibbonItem>> Items = new Dictionary<string, List<RibbonItem>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<RibbonPanelSpec, RibbonPanel>> Panels = new List<KeyValuePair<RibbonPanelSpec, RibbonPanel>>();
        private static readonly List<PulldownButton> Selectors = new List<PulldownButton>();
        private static RibbonPlan _plan;
        private static string _tab;
        /// <summary>Кнопки без подписи, которых ещё не нашли во внутренней ленте Revit: имя панели → код кнопки.</summary>
        private static readonly List<KeyValuePair<string, string>> PendingNoText = new List<KeyValuePair<string, string>>();
        private static int _hideAttempts;

        private static string SavedDepartmentFile => Path.Combine(RasPaths.User, "department.txt");

        /// <summary>Отделы раскладки по порядку (для команд «отдел 1…N»).</summary>
        public static IReadOnlyList<string> Departments => _plan?.Departments ?? new List<string>();

        public static string CurrentDepartment { get; private set; }

        public static IEnumerable<KeyValuePair<string, List<RibbonItem>>> AllItems => Items;

        public static void Build(UIControlledApplication app, string tab, RibbonPlan plan, IReadOnlyDictionary<string, PushButtonData> data)
        {
            _plan = plan;
            _tab = tab;
            var departments = plan.Departments.Take(DepartmentCommands.Count).ToList();
            if (plan.Departments.Count > departments.Count)
                Log.Write(Tag, "Отделов больше " + DepartmentCommands.Count + " — лишние в переключателе не показаны: " +
                               string.Join(", ", plan.Departments.Skip(departments.Count)));

            foreach (var spec in plan.Panels)
            {
                RibbonPanel panel;
                // имя панели на вкладке уникально: у панели отдела — с названием отдела, на ленте — только своё название
                var panelName = spec.Department == null ? spec.Title : spec.Department + " · " + spec.Title;
                try
                {
                    panel = app.CreateRibbonPanel(tab, panelName);
                    panel.Title = spec.Title;
                }
                catch (Exception ex)
                {
                    Log.Write(Tag, "Не удалось создать панель «" + spec.Title + "»", ex);
                    continue;
                }
                Panels.Add(new KeyValuePair<RibbonPanelSpec, RibbonPanel>(spec, panel));
                foreach (var slot in spec.Slots) AddSlot(panel, panelName, slot, data, departments);
            }
            HidePendingText();

            if (departments.Count > 0) ShowDepartment(RibbonLayout.StartDepartment(plan, ReadSaved()), save: false);
        }

        private static void AddSlot(RibbonPanel panel, string panelName, RibbonSlot slot, IReadOnlyDictionary<string, PushButtonData> data,
            List<string> departments)
        {
            var items = new List<RibbonItemData>();
            foreach (var e in slot.Entries)
            {
                if (departments.Count > 0 && string.Equals(e.Id, SelectorId, StringComparison.OrdinalIgnoreCase))
                    items.Add(SelectorData(data[e.Id]));
                else items.Add(data[e.Id]);
            }
            try
            {
                IList<RibbonItem> created = slot.IsStack
                    ? (items.Count == 2 ? panel.AddStackedItems(items[0], items[1]) : panel.AddStackedItems(items[0], items[1], items[2]))
                    : new List<RibbonItem> { panel.AddItem(items[0]) };
                for (var i = 0; i < created.Count; i++)
                {
                    var entry = slot.Entries[i];
                    var item = created[i];
                    if (!Items.TryGetValue(entry.Id, out var list)) Items[entry.Id] = list = new List<RibbonItem>();
                    list.Add(item);
                    if (item is PulldownButton selector) FillSelector(selector, data[entry.Id], departments);
                    else if (entry.Label != null) SetLabel(panelName, item, entry.Label);
                }
            }
            catch (Exception ex)
            {
                var ids = string.Join(", ", slot.Entries.Select(e => e.Id));
                Log.Write(OwnerOf(slot.Entries[0].Id), "Не удалось поставить на ленту: " + ids, ex);
            }
        }

        /// <summary>
        /// Своя подпись кнопки из раскладки. «» — без подписи: пустую подпись (и невидимые знаки) Revit API не принимает,
        /// поэтому подпись остаётся, а показ текста выключается во внутренней ленте Revit (AdWindows: ShowText = false).
        /// </summary>
        private static void SetLabel(string panelName, RibbonItem item, string label)
        {
            if (label.Length == 0)
            {
                PendingNoText.Add(new KeyValuePair<string, string>(panelName, item.Name));
                return;
            }
            try { item.ItemText = label; }
            catch (Exception ex) { Log.Write(Tag, "Не удалось задать подпись «" + label + "» кнопке «" + item.Name + "»", ex); }
        }

        /// <summary>
        /// Выключить текст у кнопок без подписи. Кнопка во внутренней ленте Revit — по номеру вида
        /// «…%&lt;вкладка&gt;%&lt;панель&gt;%…%&lt;код кнопки&gt;». Если лента ещё не готова (запуск Revit), — повтор в простое
        /// (RetryHiddenText), пока не найдутся.
        /// </summary>
        private static void HidePendingText()
        {
            if (PendingNoText.Count == 0) return;
            try
            {
                var tab = AW.ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == _tab || t.Title == _tab);
                if (tab == null) return;
                var items = tab.Panels.SelectMany(p => Flatten(p.Source?.Items)).Where(i => i?.Id != null).ToList();
                foreach (var pending in PendingNoText.ToList())
                {
                    var found = items.Where(i => i.Id.EndsWith("%" + pending.Value, StringComparison.Ordinal) &&
                                                 i.Id.IndexOf("%" + pending.Key + "%", StringComparison.Ordinal) >= 0).ToList();
                    if (found.Count == 0) continue;
                    foreach (var i in found) i.ShowText = false;
                    PendingNoText.Remove(pending);
                }
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось убрать подпись кнопок", ex);
                PendingNoText.Clear();
            }
        }

        /// <summary>В простое Revit: кнопки без подписи, которые при запуске не нашлись во внутренней ленте.</summary>
        public static void RetryHiddenText()
        {
            if (PendingNoText.Count == 0) return;
            HidePendingText();
            if (PendingNoText.Count == 0 || ++_hideAttempts < HideTextAttempts) return;
            Log.Write(Tag, "Не нашлись во внутренней ленте Revit кнопки без подписи — подпись осталась: " +
                           string.Join(", ", PendingNoText.Select(p => p.Value)));
            PendingNoText.Clear();
        }

        /// <summary>Кнопки панели, в том числе в стопках (строки стопки — RibbonRowPanel).</summary>
        private static IEnumerable<AW.RibbonItem> Flatten(IEnumerable<AW.RibbonItem> items)
        {
            if (items == null) yield break;
            foreach (var item in items)
            {
                yield return item;
                if (item is AW.RibbonRowPanel row)
                    foreach (var inner in Flatten(row.Items)) yield return inner;
            }
        }

        /// <summary>Переключатель отделов — выпадающая кнопка вместо «О плагине», с той же иконкой.</summary>
        private static PulldownButtonData SelectorData(PushButtonData about) => new PulldownButtonData(about.Name, about.Text)
        {
            ToolTip = "Отдел на ленте и «О плагине»",
            LargeImage = about.LargeImage,
            Image = about.Image
        };

        private static void FillSelector(PulldownButton selector, PushButtonData about, List<string> departments)
        {
            Selectors.Add(selector);
            selector.AddPushButton(new PushButtonData(about.Name + ".Open", about.Text, about.AssemblyName, about.ClassName)
            {
                ToolTip = about.ToolTip,
                AvailabilityClassName = about.AvailabilityClassName
            });
            selector.AddSeparator();
            for (var i = 0; i < departments.Count; i++)
            {
                var command = DepartmentCommands.Type(i);
                selector.AddPushButton(new PushButtonData(about.Name + ".Department" + (i + 1), departments[i], command.Assembly.Location, command.FullName)
                {
                    ToolTip = "Показать на ленте панели отдела «" + departments[i] + "»",
                    AvailabilityClassName = typeof(AlwaysAvailable).FullName
                });
            }
        }

        /// <summary>Показать панели отдела (и панели, видные во всех отделах); на переключателе — название отдела.</summary>
        public static void ShowDepartment(string department, bool save = true)
        {
            if (department == null) return;
            CurrentDepartment = department;
            foreach (var p in Panels)
            {
                try { p.Value.Visible = RibbonLayout.IsVisible(p.Key, department); }
                catch (Exception ex) { Log.Write(Tag, "Не удалось показать / скрыть панель «" + p.Key.Title + "»", ex); }
            }
            foreach (var selector in Selectors)
            {
                try { selector.ItemText = department; }
                catch (Exception ex) { Log.Write(Tag, "Не удалось подписать переключатель отделов", ex); }
            }
            if (!save) return;
            try
            {
                Directory.CreateDirectory(RasPaths.User);
                File.WriteAllText(SavedDepartmentFile, department);
            }
            catch (Exception ex) { Log.Write(Tag, "Не удалось запомнить отдел", ex); }
        }

        private static string ReadSaved()
        {
            try { return File.Exists(SavedDepartmentFile) ? File.ReadAllText(SavedDepartmentFile).Trim() : null; }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось прочитать выбранный отдел", ex);
                return null;
            }
        }

        /// <summary>Метка журнала для кнопки: модуль — часть кода до точки.</summary>
        public static string OwnerOf(string id)
        {
            var dot = id.IndexOf('.');
            return dot > 0 ? id.Substring(0, dot) : Tag;
        }
    }
}
