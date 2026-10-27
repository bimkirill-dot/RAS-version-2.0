using System;
using System.Globalization;
using System.Windows;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Настройки вида»: личные настройки вида коллизии. Цвета — общие кисти ядра (WindowTheme), по теме Revit.
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

        /// <summary>Светлая или тёмная — как интерфейс Revit: общие кисти ядра.</summary>
        private void ApplyTheme() => RASv2.Core.WindowTheme.Apply(this);
    }
}
