using System;
using System.Windows;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Окно «Показать изменения в 3D».</summary>
    public partial class ChangesViewWindow : Window
    {
        public ChangesViewWindow(ChangesViewDialogModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * 0.9); // не выше экрана, дальше прокрутка
            vm.RequestClose += ok =>
            {
                try { DialogResult = ok; }
                catch (InvalidOperationException) { Close(); }
            };
        }
    }
}
