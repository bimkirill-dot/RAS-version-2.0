using System.Windows;

namespace RASv2.BlockMirrorRotation.UI
{
    /// <summary>«Отразить блок»: сколько видов, «Переименовать оси», «Выполнить» / «Отмена». Кнопки, кисти — общие образцы ядра.</summary>
    public partial class MirrorWindow : Window
    {
        public MirrorWindow(int views)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            if (views == 0)
            {
                Info.Text = "В файле нет видов на листах — восстанавливать нечего. Откройте файл блока с оформленными листами.";
                Go.IsEnabled = false;
                return;
            }
            Info.Text = "Revit отразит модель целиком. Оформление на видах на листах (" + views +
                        ") плагин запомнит до отражения и вернёт после: оси, уровни, марки, высотные отметки, размеры, текст. " +
                        "Что не получится — покажет списком.";
        }

        public bool RenameGrids => Rename.IsChecked == true;

        private void Go_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
