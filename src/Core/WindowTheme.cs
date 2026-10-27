using System.Windows;
using System.Windows.Media;

namespace RASv2.Core
{
    /// <summary>
    /// Общие кисти и образцы окон модулей (PRINCIPLES.md, раздел 12: «цвета — только через общие кисти», «окна строятся по
    /// общим образцам ядра»): светлая или тёмная тема — как интерфейс Revit (в Revit 2023 — всегда светлая). Окно берёт кисти
    /// через {DynamicResource Bg} и т. п.: Bg — фон, Fg — текст, Muted — подсказки, Field — поля ввода, Line — рамки, Row — фон
    /// строки и выбранного, Hover / Pressed — кнопка под мышью и нажатая, Accent — главная кнопка и выделение, AccentFg — текст
    /// на акценте, Error — ошибка. Образцы кнопок и полей — WindowStyles (обычная, PrimaryButton, IconButton).
    /// </summary>
    public static class WindowTheme
    {
        public static void Apply(FrameworkElement element)
        {
            var dark = RevitCompat.IsDarkTheme;
            var r = element.Resources;
            r["Bg"] = Brush(dark ? "#2B2F36" : "#FFFFFF");
            r["Fg"] = Brush(dark ? "#E6E9EE" : "#1F2328");
            r["Muted"] = Brush(dark ? "#9AA3AF" : "#6B7280");
            r["Field"] = Brush(dark ? "#1F2329" : "#FFFFFF");
            r["Line"] = Brush(dark ? "#4A525E" : "#C9CED6");
            r["Row"] = Brush(dark ? "#353A42" : "#F3F5F8");
            r["Hover"] = Brush(dark ? "#3A4049" : "#EEF1F5");
            r["Pressed"] = Brush(dark ? "#454C57" : "#E2E6EC");
            r["Accent"] = Brush(dark ? "#5B8DEF" : "#2F6FED");
            r["AccentFg"] = Brush("#FFFFFF");
            r["Error"] = Brush(dark ? "#FF6B6F" : "#E5484D");
            r.MergedDictionaries.Add(new WindowStyles());
        }

        public static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
