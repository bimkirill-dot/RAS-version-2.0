using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Настройки вида»: личные настройки вида коллизии. Тема — как в Revit: общей темы окон в ядре пока нет (PRINCIPLES.md,
    /// раздел 12 — появится, когда понадобится второму модулю), поэтому цвета — здесь, по светлой или тёмной теме Revit.
    /// </summary>
    public partial class ViewSettingsWindow : Window
    {
        private static readonly string[] StyleOrder = ViewSettings.Styles;

        public ViewSettings Result { get; private set; }

        public ViewSettingsWindow(ViewSettings settings)
        {
            InitializeComponent();
            ApplyTheme();
            var s = settings.Copy();
            Kind3D.IsChecked = s.Kind == ViewSettings.Kind3D;
            KindPlan.IsChecked = s.Kind == ViewSettings.KindPlan;
            KindSection.IsChecked = s.Kind == ViewSettings.KindSection;
            KindAuto.IsChecked = s.Kind == ViewSettings.KindAuto;
            ShowIsolate.IsChecked = s.Isolate;
            ShowCrop.IsChecked = !s.Isolate;
            MarginSize.IsChecked = s.MarginBySize;
            MarginFixed.IsChecked = !s.MarginBySize;
            MarginBox.Text = s.MarginMeters.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
            MarginBox.GotFocus += (o, e) => MarginFixed.IsChecked = true;
            StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(StyleOrder, s.Style));
            Paint.IsChecked = s.Paint;
            WarnUnloaded.IsChecked = s.WarnUnloaded;
            WarnMissingLink.IsChecked = s.WarnMissingLink;
            Result = s;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var s = Result.Copy();
            s.Kind = KindPlan.IsChecked == true ? ViewSettings.KindPlan
                   : KindSection.IsChecked == true ? ViewSettings.KindSection
                   : KindAuto.IsChecked == true ? ViewSettings.KindAuto
                   : ViewSettings.Kind3D;
            s.Isolate = ShowIsolate.IsChecked == true;
            s.MarginBySize = MarginSize.IsChecked == true;
            if (!s.MarginBySize)
            {
                var text = (MarginBox.Text ?? "").Trim().Replace('.', ',');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("ru-RU"), out var m) || m < ViewSettings.MinMargin || m > ViewSettings.MaxMargin)
                {
                    Error.Text = $"Запас — число метров от {ViewSettings.MinMargin.ToString(CultureInfo.GetCultureInfo("ru-RU"))} до {ViewSettings.MaxMargin}, например «1» или «1,5».";
                    Error.Visibility = Visibility.Visible;
                    MarginBox.Focus();
                    return;
                }
                s.MarginMeters = m;
            }
            s.Style = StyleOrder[Math.Max(0, StyleBox.SelectedIndex)];
            s.Paint = Paint.IsChecked == true;
            s.WarnUnloaded = WarnUnloaded.IsChecked == true;
            s.WarnMissingLink = WarnMissingLink.IsChecked == true;
            Result = s.Normalize();
            DialogResult = true;
        }

        /// <summary>Светлая или тёмная — как интерфейс Revit.</summary>
        private void ApplyTheme()
        {
            var dark = false;
            try { dark = UIThemeManager.CurrentTheme == UITheme.Dark; }
            catch (Exception) { /* тему узнать не удалось — светлая */ }
            Resources["Bg"] = Brush(dark ? "#2B2F36" : "#FFFFFF");
            Resources["Fg"] = Brush(dark ? "#E6E9EE" : "#1F2328");
            Resources["Muted"] = Brush(dark ? "#9AA3AF" : "#6B7280");
            Resources["Field"] = Brush(dark ? "#1F2329" : "#FFFFFF");
            Resources["Line"] = Brush(dark ? "#4A525E" : "#C9CED6");
        }

        private static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
