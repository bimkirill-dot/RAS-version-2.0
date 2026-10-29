using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Настройки вида»: личные настройки вида коллизии. Кнопки, поля, кисти — общие образцы ядра (WindowTheme), по теме Revit.
    /// «Границы вида» — только у 3D-вида.
    /// </summary>
    public partial class ViewSettingsWindow : Window
    {
        private static readonly string[] StyleOrder = ViewSettings.Styles;
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public ViewSettings Result { get; private set; }

        // категории открытой модели для «Категорий элементов»; null — модель не открыта
        private readonly Func<List<CategoryItem>> _categories;
        private List<string> _hiddenHost, _hiddenLink;

        public ViewSettingsWindow(ViewSettings settings, Func<List<CategoryItem>> categories)
        {
            _categories = categories;
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            var s = settings.Copy();
            Kind3D.IsChecked = s.Kind == ViewSettings.Kind3D;
            KindPlan.IsChecked = s.Kind == ViewSettings.KindPlan;
            KindSection.IsChecked = s.Kind == ViewSettings.KindSection;
            KindAuto.IsChecked = s.Kind == ViewSettings.KindAuto;
            Isolate.IsChecked = s.Isolate;
            CropView.IsChecked = s.CropToView && s.Kind == ViewSettings.Kind3D;
            CropAuto.IsChecked = CropView.IsChecked != true && s.MarginBySize;
            CropMargin.IsChecked = CropView.IsChecked != true && !s.MarginBySize;
            MarginBox.Text = s.MarginMeters.ToString("0.##", Ru);
            MarginBox.GotFocus += (o, e) => CropMargin.IsChecked = true;
            Paint.IsChecked = s.Paint;
            ClashTransparency.Text = s.ClashTransparency.ToString(Ru);
            OtherTransparency.Text = s.OtherTransparency.ToString(Ru);
            StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(StyleOrder, s.Style));
            WarnUnloaded.IsChecked = s.WarnUnloaded;
            WarnMissingLink.IsChecked = s.WarnMissingLink;
            _hiddenHost = s.HiddenHost;
            _hiddenLink = s.HiddenLink;
            CategoriesSummary.Text = CategoryChoice.Summary(s);
            Result = s;
            Kind_Changed(null, null);
            Isolate_Changed(null, null);
        }

        /// <summary>«Категории элементов» — только без «Изолировать»: при нём категории не действуют.</summary>
        private void Isolate_Changed(object sender, RoutedEventArgs e)
        {
            if (CategoriesRow == null) return; // во время InitializeComponent
            CategoriesRow.IsEnabled = Isolate.IsChecked != true;
            CategoriesRow.Opacity = CategoriesRow.IsEnabled ? 1 : 0.45;
        }

        /// <summary>«Категории элементов»: список — из открытой модели; выбранное сохраняется вместе с остальным («Сохранить»).</summary>
        private void Categories_Click(object sender, RoutedEventArgs e)
        {
            // ошибка не должна уйти из обработчика: сквозь ShowDialog окон она оставляет их сломанными, и Revit падает (29.10.2026)
            try
            {
                var items = _categories?.Invoke();
                if (items == null || items.Count == 0)
                {
                    Fail("Откройте модель — список категорий берётся из неё.", (Control)sender);
                    return;
                }
                Error.Visibility = Visibility.Collapsed;
                var window = new CategoriesWindow(items, _hiddenHost, _hiddenLink) { Owner = this };
                if (window.ShowDialog() != true) return;
                _hiddenHost = window.HiddenHost;
                _hiddenLink = window.HiddenLink;
                CategoriesSummary.Text = CategoryChoice.Summary(new ViewSettings { HiddenHost = _hiddenHost, HiddenLink = _hiddenLink });
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Категории элементов: окно не открылось", ex);
                Fail("Окно «Категории элементов» не открылось — подробности в журнале плагина.", (Control)sender);
            }
        }

        /// <summary>«Границы вида» — только у 3D-вида; выбраны, а тип уже другой — «Авто».</summary>
        private void Kind_Changed(object sender, RoutedEventArgs e)
        {
            if (CropView == null) return; // во время InitializeComponent
            CropView.IsEnabled = Kind3D.IsChecked == true;
            if (!CropView.IsEnabled && CropView.IsChecked == true) CropAuto.IsChecked = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var s = Result.Copy();
            s.Kind = KindPlan.IsChecked == true ? ViewSettings.KindPlan
                   : KindSection.IsChecked == true ? ViewSettings.KindSection
                   : KindAuto.IsChecked == true ? ViewSettings.KindAuto
                   : ViewSettings.Kind3D;
            s.Isolate = Isolate.IsChecked == true;
            s.CropToView = CropView.IsChecked == true;
            if (!s.CropToView) s.MarginBySize = CropAuto.IsChecked == true;
            if (CropMargin.IsChecked == true)
            {
                var text = (MarginBox.Text ?? "").Trim().Replace('.', ',');
                if (!double.TryParse(text, NumberStyles.Float, Ru, out var m) || m < ViewSettings.MinMargin || m > ViewSettings.MaxMargin)
                {
                    Fail($"Отступ — число метров от {ViewSettings.MinMargin.ToString(Ru)} до {ViewSettings.MaxMargin}, например «1» или «1,5».", MarginBox);
                    return;
                }
                s.MarginMeters = m;
            }
            s.Paint = Paint.IsChecked == true;
            if (!Percent(ClashTransparency, out var clash) || !Percent(OtherTransparency, out var other)) return;
            s.ClashTransparency = clash;
            s.OtherTransparency = other;
            s.Style = StyleOrder[Math.Max(0, StyleBox.SelectedIndex)];
            s.WarnUnloaded = WarnUnloaded.IsChecked == true;
            s.WarnMissingLink = WarnMissingLink.IsChecked == true;
            s.HiddenHost = _hiddenHost;
            s.HiddenLink = _hiddenLink;
            Result = s.Normalize();
            DialogResult = true;
        }

        /// <summary>Прозрачность — целое от 0 до 100; пусто — 0.</summary>
        private bool Percent(TextBox box, out int value)
        {
            var text = (box.Text ?? "").Trim().TrimEnd('%').Trim();
            if (text.Length == 0) text = "0";
            if (int.TryParse(text, NumberStyles.Integer, Ru, out value) && value >= 0 && value <= ViewSettings.MaxTransparency) return true;
            Fail($"Прозрачность — целое число от 0 до {ViewSettings.MaxTransparency} %.", box);
            return false;
        }

        private void Fail(string text, Control field)
        {
            Error.Text = text;
            Error.Visibility = Visibility.Visible;
            field.Focus();
        }
    }
}
