using System;
using System.Windows;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Окно «Сортировка и группировка».</summary>
    public partial class SortWindow : Window
    {
        public SortWindow(SortViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.RequestClose += ok =>
            {
                try { DialogResult = ok; }
                catch (InvalidOperationException) { Close(); }
            };
        }
    }
}
