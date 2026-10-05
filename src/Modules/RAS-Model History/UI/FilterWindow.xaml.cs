using System;
using System.Windows;
using System.Windows.Controls;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Окно «Фильтр».</summary>
    public partial class FilterWindow : Window
    {
        public FilterWindow(FilterViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            // много условий — прокрутка, а не окно выше экрана
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            vm.RequestClose += ok =>
            {
                try { DialogResult = ok; }
                catch (InvalidOperationException) { Close(); }
            };
        }

        /// <summary>Значения поля считаются только при открытии списка: подсчёт идёт по всему журналу.</summary>
        private void ValueDropDownOpened(object sender, EventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FilterRowViewModel row) row.LoadSuggestions();
        }
    }
}
