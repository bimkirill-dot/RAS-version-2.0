using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Светлая и тёмная тема панели и окна настроек.
    /// Все цвета — кисти {DynamicResource Spy.*}. Цвет кисти на месте не меняется: WPF замораживает кисть,
    /// как только её через DynamicResource берёт стиль, применённый к элементу (хватает одного TextBox
    /// со стилем темы). Поэтому при смене темы каждый словарь темы получает новые кисти,
    /// а DynamicResource сам перекрашивает всё, что на них ссылается, без перезагрузки окон.
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>Ключ → (цвет светлой темы, цвет тёмной темы), ARGB.</summary>
        private static readonly Dictionary<string, (uint Light, uint Dark)> Palette = new Dictionary<string, (uint, uint)>
        {
            ["Spy.Bg"] = (0xFFFFFFFF, 0xFF2A2F38),            // фон панели
            ["Spy.Surface"] = (0xFFFFFFFF, 0xFF323843),       // поля ввода, списки, всплывающие окна
            ["Spy.SurfaceAlt"] = (0xFFF6F7F9, 0xFF2F353F),    // строка «Ещё», заголовки таблиц
            ["Spy.Hover"] = (0xFFEEF2F7, 0xFF3A414D),         // наведение
            ["Spy.Pressed"] = (0xFFE1E8F0, 0xFF444C59),       // нажатие
            ["Spy.Border"] = (0xFFC9D0D9, 0xFF4B5361),        // рамки элементов
            ["Spy.Line"] = (0xFFE6E8EB, 0xFF3A404A),          // линии таблиц, разделители
            ["Spy.AltRow"] = (0xFFFAFBFC, 0xFF2D323B),        // чередование строк
            ["Spy.Text"] = (0xFF1F2328, 0xFFE4E7EC),          // основной текст
            ["Spy.Muted"] = (0xFF6B6F76, 0xFF9AA2AE),         // второстепенный текст
            ["Spy.Faint"] = (0xFF9EA3AA, 0xFF757D89),         // подсказки в полях, «не попало в хранилище»
            ["Spy.Accent"] = (0xFF1565C0, 0xFF6AA8F0),        // ссылки, выбранная вкладка, «Изменён»
            ["Spy.AccentBg"] = (0xFFE8F1FB, 0xFF26405F),      // включённые фильтры, блок «Выделено в Revit»
            ["Spy.AccentBorder"] = (0xFFB6D4F2, 0xFF3C6390),
            ["Spy.AccentText"] = (0xFF0C447C, 0xFFB9D6F7),
            ["Spy.Selection"] = (0xFFD9E9FA, 0xFF2E4D74),     // выбранная строка
            ["Spy.Success"] = (0xFF2E7D32, 0xFF73C77A),       // «Создан», «в хранилище»
            ["Spy.Danger"] = (0xFFC62828, 0xFFF07B7B),        // «Удалён», «не попало»
            ["Spy.Warning"] = (0xFF8A5300, 0xFFE9B860),       // предупреждения
            ["Spy.WarningBg"] = (0xFFFFF4E5, 0xFF473A22),
            ["Spy.WarningBorder"] = (0xFFE8B66A, 0xFF7A6134),
            ["Spy.InfoBg"] = (0xFFF3F4F6, 0xFF323843),        // полоса хода актуализации
            ["Spy.GroupBg"] = (0xFFF4F7FB, 0xFF303846),       // строка-операция (группа записей)
        };

        /// <summary>Словари темы (панели, окон, контекстных меню), в которые положены кисти. Закрытое окно не удерживаем.</summary>
        private static readonly List<WeakReference<ResourceDictionary>> Hosts = new List<WeakReference<ResourceDictionary>>();

        /// <summary>Вложенный словарь с кистями: при смене темы он заменяется целиком — одно обновление на окно.</summary>
        private sealed class BrushDictionary : ResourceDictionary
        {
        }

        /// <summary>Сейчас включена тёмная тема.</summary>
        public static bool IsDark { get; private set; }

        /// <summary>Положить кисти темы в словарь ресурсов (словарь темы, окна или контекстного меню).</summary>
        public static void AddBrushes(ResourceDictionary target)
        {
            target.MergedDictionaries.Add(CreateBrushes(IsDark));
            Hosts.RemoveAll(h => !h.TryGetTarget(out _));
            Hosts.Add(new WeakReference<ResourceDictionary>(target));
        }

        /// <summary>Выбрать тему по настройке: «Auto» — как в Revit, «Light», «Dark».</summary>
        public static void Apply(SpySettings settings)
        {
            var mode = settings?.UiTheme ?? "Auto";
            bool dark;
            if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase)) dark = true;
            else if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase)) dark = false;
            else dark = RevitIsDark();
            SetDark(dark);
        }

        public static void SetDark(bool dark)
        {
            if (dark == IsDark) return;
            IsDark = dark;
            foreach (var h in Hosts)
            {
                if (!h.TryGetTarget(out var host)) continue;
                try
                {
                    var fresh = CreateBrushes(dark);
                    var merged = host.MergedDictionaries;
                    var i = 0;
                    while (i < merged.Count && !(merged[i] is BrushDictionary)) i++;
                    if (i < merged.Count) merged[i] = fresh;
                    else merged.Add(fresh);
                }
                catch (Exception ex)
                {
                    Diag.Log("Theme brushes replace failed", ex); // одно окно не должно остаться без темы из-за другого
                }
            }
            Hosts.RemoveAll(h => !h.TryGetTarget(out _));
        }

        /// <summary>Новые кисти темы. Заморожены сразу: их не меняют, а заменяют.</summary>
        private static BrushDictionary CreateBrushes(bool dark)
        {
            var dict = new BrushDictionary();
            foreach (var p in Palette)
            {
                var brush = new SolidColorBrush(ToColor(dark ? p.Value.Dark : p.Value.Light));
                brush.Freeze();
                dict[p.Key] = brush;
            }
            return dict;
        }

        /// <summary>Тема интерфейса Revit (с Revit 2024 есть тёмная). Вызывать из главного потока Revit.</summary>
        private static bool RevitIsDark()
        {
            try
            {
                return Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;
            }
            catch (Exception ex)
            {
                Diag.Log("Revit theme unknown", ex);
                return false;
            }
        }

        private static Color ToColor(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
}
