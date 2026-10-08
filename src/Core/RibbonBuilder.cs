using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;

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
        /// <summary>Подпись «без подписи»: пустую строку Revit может не принять, а этот знак не виден.</summary>
        private const string NoLabel = "​";
        public const string SelectorId = "Core.About";

        /// <summary>Кнопки на ленте по кодам (у кнопки в нескольких отделах — несколько).</summary>
        private static readonly Dictionary<string, List<RibbonItem>> Items = new Dictionary<string, List<RibbonItem>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<RibbonPanelSpec, RibbonPanel>> Panels = new List<KeyValuePair<RibbonPanelSpec, RibbonPanel>>();
        private static readonly List<PulldownButton> Selectors = new List<PulldownButton>();
        private static RibbonPlan _plan;

        private static string SavedDepartmentFile => Path.Combine(RasPaths.User, "department.txt");

        /// <summary>Отделы раскладки по порядку (для команд «отдел 1…N»).</summary>
        public static IReadOnlyList<string> Departments => _plan?.Departments ?? new List<string>();

        public static string CurrentDepartment { get; private set; }

        public static IEnumerable<KeyValuePair<string, List<RibbonItem>>> AllItems => Items;

        public static void Build(UIControlledApplication app, string tab, RibbonPlan plan, IReadOnlyDictionary<string, PushButtonData> data)
        {
            _plan = plan;
            var departments = plan.Departments.Take(DepartmentCommands.Count).ToList();
            if (plan.Departments.Count > departments.Count)
                Log.Write(Tag, "Отделов больше " + DepartmentCommands.Count + " — лишние в переключателе не показаны: " +
                               string.Join(", ", plan.Departments.Skip(departments.Count)));

            foreach (var spec in plan.Panels)
            {
                RibbonPanel panel;
                try
                {
                    // имя панели на вкладке уникально: у панели отдела — с названием отдела, на ленте — только своё название
                    panel = app.CreateRibbonPanel(tab, spec.Department == null ? spec.Title : spec.Department + " · " + spec.Title);
                    panel.Title = spec.Title;
                }
                catch (Exception ex)
                {
                    Log.Write(Tag, "Не удалось создать панель «" + spec.Title + "»", ex);
                    continue;
                }
                Panels.Add(new KeyValuePair<RibbonPanelSpec, RibbonPanel>(spec, panel));
                foreach (var slot in spec.Slots) AddSlot(panel, slot, data, departments);
            }

            if (departments.Count > 0) ShowDepartment(RibbonLayout.StartDepartment(plan, ReadSaved()), save: false);
        }

        private static void AddSlot(RibbonPanel panel, RibbonSlot slot, IReadOnlyDictionary<string, PushButtonData> data, List<string> departments)
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
                    else if (entry.Label != null) SetLabel(item, entry.Label);
                }
            }
            catch (Exception ex)
            {
                var ids = string.Join(", ", slot.Entries.Select(e => e.Id));
                Log.Write(OwnerOf(slot.Entries[0].Id), "Не удалось поставить на ленту: " + ids, ex);
            }
        }

        /// <summary>Своя подпись кнопки из раскладки; «» — без подписи.</summary>
        private static void SetLabel(RibbonItem item, string label)
        {
            try { item.ItemText = label.Length == 0 ? NoLabel : label; }
            catch (Exception ex) { Log.Write(Tag, "Не удалось задать подпись «" + label + "» кнопке «" + item.Name + "»", ex); }
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
