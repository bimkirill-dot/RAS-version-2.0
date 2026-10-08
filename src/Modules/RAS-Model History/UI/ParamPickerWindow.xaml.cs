using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// «Добавить столбец параметра…» (правый щелчок по заголовку таблицы): список всех параметров модели с поиском.
    /// Выбранное — Chosen.
    /// </summary>
    public partial class ParamPickerWindow : Window
    {
        private readonly List<string> _all;

        public ParamPickerWindow(IEnumerable<string> names)
        {
            InitializeComponent();
            _all = names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            Apply("");
            Loaded += (s, e) => Search.Focus();
        }

        /// <summary>Выбранный параметр (null — отмена).</summary>
        public string Chosen { get; private set; }

        private void Apply(string text)
        {
            var t = (text ?? "").Trim();
            var list = t.Length == 0
                ? _all
                : _all.Where(n => CultureInfo.CurrentCulture.CompareInfo.IndexOf(n, t, CompareOptions.IgnoreCase) >= 0).ToList();
            ParamList.ItemsSource = list;
            if (list.Count > 0) ParamList.SelectedIndex = 0;
            CountText.Text = list.Count == _all.Count ? "Параметров: " + list.Count : "Найдено: " + list.Count + " из " + _all.Count;
            Ok.IsEnabled = list.Count > 0;
        }

        private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Apply(Search.Text);

        private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            Ok.IsEnabled = ParamList.SelectedItem != null;

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose();

        private void Ok_Click(object sender, RoutedEventArgs e) => Choose();

        private void Choose()
        {
            if (!(ParamList.SelectedItem is string name)) return;
            Chosen = name;
            DialogResult = true;
        }
    }
}
