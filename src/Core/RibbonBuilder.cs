using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
        /// <summary>Сколько раз в простое Revit искать кнопки во внутренней ленте, если при запуске она ещё не была готова.</summary>
        private const int PendingAttempts = 200;
        /// <summary>
        /// Подпись переключателя отделов — не уже этой (точки экрана): с короткой подписью («BIM») панель «Плагин» была заметно
        /// уже соседних (разработчик, 27.10.2026). Ширина кнопки во внутренней ленте (MinWidth) прижимала иконку влево —
        /// выравнивания там нет, поэтому шире делается подпись: неразрывные пробелы по бокам, иконка — по центру над ней.
        /// </summary>
        private const double SelectorTextWidth = 70;

        /// <summary>Кнопки на ленте по кодам (у кнопки в нескольких отделах — несколько).</summary>
        private static readonly Dictionary<string, List<RibbonItem>> Items = new Dictionary<string, List<RibbonItem>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<RibbonPanelSpec, RibbonPanel>> Panels = new List<KeyValuePair<RibbonPanelSpec, RibbonPanel>>();
        private static readonly List<PulldownButton> Selectors = new List<PulldownButton>();
        private static RibbonPlan _plan;
        private static string _tab;
        /// <summary>
        /// Что сделать с кнопкой во внутренней ленте Revit, чего не умеет Revit API (убрать подпись), — пока кнопку там
        /// не нашли: имя панели, код кнопки, действие.
        /// </summary>
        private sealed class Tweak
        {
            public string Panel, Name, What;
            public Action<AW.RibbonItem> Apply;
        }
        private static readonly List<Tweak> Pending = new List<Tweak>();
        private static int _pendingAttempts;

        private static string SavedDepartmentFile => Path.Combine(RasPaths.User, "department.txt");

        /// <summary>Отделы раскладки по порядку (для команд «отдел 1…N»).</summary>
        public static IReadOnlyList<string> Departments => _plan?.Departments ?? new List<string>();

        public static string CurrentDepartment { get; private set; }

        public static IEnumerable<KeyValuePair<string, List<RibbonItem>>> AllItems => Items;

        /// <summary>Кнопки, серые у этого человека по «доступна=» раскладки (для «О плагине»).</summary>
        public static HashSet<string> Disabled { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Пояснение в подсказке серой кнопки: это не поломка.</summary>
        public const string DisabledNote = "Недоступна у вас — обратитесь к BIM-менеджеру.";

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
            ApplyPending();

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
                    if (!entry.Enabled) Disable(item, entry.Id);
                }
            }
            catch (Exception ex)
            {
                var ids = string.Join(", ", slot.Entries.Select(e => e.Id));
                Log.Write(OwnerOf(slot.Entries[0].Id), "Не удалось поставить на ленту: " + ids, ex);
            }
        }

        /// <summary>Кнопка видна, но не нажимается («доступна=» в раскладке): серая, в подсказке — почему.</summary>
        private static void Disable(RibbonItem item, string id)
        {
            try
            {
                item.Enabled = false;
                item.ToolTip = string.IsNullOrEmpty(item.ToolTip) ? DisabledNote : item.ToolTip + "\n\n" + DisabledNote;
                Disabled.Add(id);
            }
            catch (Exception ex) { Log.Write(Tag, "Не удалось сделать серой кнопку «" + id + "»", ex); }
        }

        /// <summary>
        /// Своя подпись кнопки из раскладки. «» — без подписи: пустую подпись (и невидимые знаки) Revit API не принимает,
        /// поэтому подпись остаётся, а показ текста выключается во внутренней ленте Revit (AdWindows: ShowText = false).
        /// </summary>
        private static void SetLabel(string panelName, RibbonItem item, string label)
        {
            if (label.Length == 0)
            {
                Pending.Add(new Tweak { Panel = panelName, Name = item.Name, What = "без подписи", Apply = i => i.ShowText = false });
                return;
            }
            try { item.ItemText = label; }
            catch (Exception ex) { Log.Write(Tag, "Не удалось задать подпись «" + label + "» кнопке «" + item.Name + "»", ex); }
        }

        /// <summary>
        /// Сделать во внутренней ленте Revit то, чего не умеет Revit API (Pending). Кнопка там — по номеру вида
        /// «…%&lt;вкладка&gt;%&lt;панель&gt;%…%&lt;код кнопки&gt;». Если лента ещё не готова (запуск Revit), — повтор в простое
        /// (RetryPending), пока не найдутся.
        /// </summary>
        private static void ApplyPending()
        {
            if (Pending.Count == 0) return;
            try
            {
                var tab = AW.ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == _tab || t.Title == _tab);
                if (tab == null) return;
                var items = tab.Panels.SelectMany(p => Flatten(p.Source?.Items)).Where(i => i?.Id != null).ToList();
                foreach (var pending in Pending.ToList())
                {
                    var found = items.Where(i => i.Id.EndsWith("%" + pending.Name, StringComparison.Ordinal) &&
                                                 i.Id.IndexOf("%" + pending.Panel + "%", StringComparison.Ordinal) >= 0).ToList();
                    if (found.Count == 0) continue;
                    foreach (var i in found) pending.Apply(i);
                    Pending.Remove(pending);
                }
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось настроить кнопки во внутренней ленте Revit", ex);
                Pending.Clear();
            }
        }

        /// <summary>В простое Revit: кнопки, которые при запуске не нашлись во внутренней ленте.</summary>
        public static void RetryPending()
        {
            if (Pending.Count == 0) return;
            ApplyPending();
            if (Pending.Count == 0 || ++_pendingAttempts < PendingAttempts) return;
            Log.Write(Tag, "Не нашлись во внутренней ленте Revit кнопки — не сделано: " +
                           string.Join(", ", Pending.Select(p => p.Name + " (" + p.What + ")")));
            Pending.Clear();
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
                try { selector.ItemText = SelectorLabel(department); }
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

        /// <summary>Название отдела с неразрывными пробелами по бокам — до ширины SelectorTextWidth (длинное — как есть).</summary>
        private static string SelectorLabel(string department)
        {
            try
            {
                double Width(string s) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                var space = Width("\u00A0");
                var pad = space > 0 ? (int)Math.Ceiling((SelectorTextWidth - Width(department)) / space / 2) : 0;
                if (pad <= 0) return department;
                var side = new string('\u00A0', Math.Min(pad, 30));
                return side + department + side;
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось расширить подпись переключателя отделов", ex);
                return department;
            }
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
