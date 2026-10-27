using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.UI
{
    /// <summary>Общие настройки кнопки «Подпись» — шестерёнка в окне (DECISIONS.md модуля).</summary>
    public partial class SignSettingsWindow : Window
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public SignSettings Result { get; private set; }

        public SignSettingsWindow(SignSettings settings, bool offline, string acadFound)
        {
            InitializeComponent();
            RASv2.Core.WindowTheme.Apply(this);
            var s = settings.Copy();
            Result = s;
            Changed.Text = "Настройки общие для всех, кто добавляет подписи." +
                           (string.IsNullOrEmpty(s.ChangedBy) ? "" : " Изменены: " + s.ChangedBy + ", " + s.ChangedAt + ".") +
                           (offline ? " Сервер не ответил — показаны последние известные; сохранить сейчас не получится." : "");
            Folder.Text = s.Folder;
            ModeHeight.IsChecked = s.SizeMode == SizeModes.Height;
            ModeWidth.IsChecked = s.SizeMode == SizeModes.Width;
            ModeBox.IsChecked = s.SizeMode == SizeModes.Box;
            ModeKeep.IsChecked = s.SizeMode == SizeModes.Keep;
            HeightBox.Text = N(s.Height); MaxWidthBox.Text = N(s.MaxWidth);
            WidthBox.Text = N(s.Width); MaxHeightBox.Text = N(s.MaxHeight);
            BoxWidth.Text = N(s.BoxWidth); BoxHeight.Text = N(s.BoxHeight);
            Tolerance.Text = N(s.Tolerance);
            HatchOutline.IsChecked = s.Hatch != HatchModes.Drop;
            HatchDrop.IsChecked = s.Hatch == HatchModes.Drop;
            Acad.Text = s.Acad;
            AcadFound.Text = acadFound != null ? "Пусто — найти самому. Сейчас: " + acadFound : "Пусто — найти самому. AutoCAD не найден: новые подписи не очистить.";
            Table.Text = s.Table; GipTable.Text = s.GipTable; GipMarker.Text = s.GipMarker;
            KeyParam.Text = s.KeyParam; Column.Text = s.Column; DefaultValue.Text = s.DefaultValue;
            FileWord.Text = s.FileWord; ColorBox.Text = s.Color;
            Departments.Text = s.Departments; Leaders.Text = s.Leaders; Regulation.Text = s.Regulation;
            SaveButton.IsEnabled = !offline;
        }

        private static string N(double v) => v.ToString("0.###", Ru);

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка с исходными подписями (DWG)", ShowNewFolderButton = false })
            {
                if (Directory.Exists(Folder.Text)) dialog.SelectedPath = Folder.Text;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Folder.Text = dialog.SelectedPath;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var s = Result.Copy();
            s.Folder = Folder.Text;
            s.SizeMode = ModeWidth.IsChecked == true ? SizeModes.Width : ModeBox.IsChecked == true ? SizeModes.Box : ModeKeep.IsChecked == true ? SizeModes.Keep : SizeModes.Height;
            if (!Num(HeightBox, out var h) || !Num(MaxWidthBox, out var mw) || !Num(WidthBox, out var w) || !Num(MaxHeightBox, out var mh) ||
                !Num(BoxWidth, out var bw) || !Num(BoxHeight, out var bh) || !Num(Tolerance, out var tol)) return;
            s.Height = h; s.MaxWidth = mw; s.Width = w; s.MaxHeight = mh; s.BoxWidth = bw; s.BoxHeight = bh; s.Tolerance = tol;
            s.Hatch = HatchDrop.IsChecked == true ? HatchModes.Drop : HatchModes.Outline;
            s.Acad = Acad.Text;
            if (!string.IsNullOrWhiteSpace(s.Acad) && !File.Exists(s.Acad.Trim()))
            {
                Fail("Нет файла «" + s.Acad.Trim() + "» — укажите accoreconsole.exe или оставьте поле пустым.", Acad);
                return;
            }
            s.Table = Table.Text; s.GipTable = GipTable.Text; s.GipMarker = GipMarker.Text;
            s.KeyParam = KeyParam.Text; s.Column = Column.Text; s.DefaultValue = DefaultValue.Text;
            s.FileWord = FileWord.Text;
            s.Departments = Departments.Text; s.Leaders = Leaders.Text; s.Regulation = Regulation.Text;
            if (SignSettings.Rgb(ColorBox.Text) == null)
            {
                Fail("Цвет — три числа от 0 до 255 через запятую, например «0,0,255».", ColorBox);
                return;
            }
            s.Color = ColorBox.Text.Trim();
            Result = s.Normalize();
            DialogResult = true;
        }

        private bool Num(TextBox box, out double v)
        {
            if (double.TryParse((box.Text ?? "").Trim().Replace('.', ','), NumberStyles.Float, Ru, out v) && v >= 0) return true;
            Fail("Нужно число, например «7» или «0,02».", box);
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
