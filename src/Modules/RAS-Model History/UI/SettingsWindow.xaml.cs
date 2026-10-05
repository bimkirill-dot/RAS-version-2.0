using System;
using System.Windows;

namespace RASv2.ModelHistory.UI
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;

            // Открывается по высоте содержимого, но не выше экрана; дальше размер меняет пользователь,
            // а содержимое прокручивается.
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * 0.9);
            Loaded += (s, e) =>
            {
                SizeToContent = SizeToContent.Manual;
                MaxHeight = double.PositiveInfinity;
            };

            vm.RequestClose += ok =>
            {
                try { DialogResult = ok; }
                catch (InvalidOperationException) { Close(); }
            };
        }
    }
}
