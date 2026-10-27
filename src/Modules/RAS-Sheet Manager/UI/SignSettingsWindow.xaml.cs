using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.UI
{
    /// <summary>
    /// Общие настройки кнопки «Подпись» — шестерёнка в окне (DECISIONS.md модуля). Разделы — слева: «Папка и размер»,
    /// «Очистка», «Регламент наименования», «Семейство подписей».
    /// </summary>
    public partial class SignSettingsWindow : Window
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public SignSettings Result { get; private set; }

        private bool _ready;

        /// <param name="showRegulation">Открыть сразу на регламенте наименования (ссылка из окна «Подпись»).</param>
        public SignSettingsWindow(SignSettings settings, bool offline, string acadFound, bool showRegulation = false)
        {
            InitializeComponent();
            RASv2.Core.WindowTheme.Apply(this);
            Nav.SelectedIndex = showRegulation ? 2 : 0;
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
            Departments.Text = s.Departments; Leaders.Text = s.Leaders;
            _ready = true;
            ShowRegulation();
            SaveButton.IsEnabled = !offline;
        }

        private static string N(double v) => v.ToString("0.###", Ru);

        private Panel[] Sections => new Panel[] { SectionSize, SectionClean, SectionNaming, SectionFamily };

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            var sections = Sections;
            for (var i = 0; i < sections.Length; i++) sections[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            Scroll.ScrollToTop();
        }

        /// <summary>Раздел, в котором поле, — открыть (ошибка в поле другого раздела).</summary>
        private void ShowSectionOf(DependencyObject field)
        {
            var sections = Sections;
            for (var e = field; e != null; e = LogicalTreeHelper.GetParent(e))
            {
                var i = Array.IndexOf(sections, e as Panel);
                if (i >= 0) { Nav.SelectedIndex = i; return; }
            }
        }

        /// <summary>Регламент — из полей окна (слово, отделы, кто с «!»): меняются поля — меняется и текст.</summary>
        private void ShowRegulation()
        {
            var s = Result.Copy();
            s.FileWord = FileWord.Text; s.Departments = Departments.Text; s.Leaders = Leaders.Text; s.KeyParam = KeyParam.Text;
            s.Normalize();
            RegulationPattern.Text = SignRegulation.Pattern(s);
            RegulationRules.ItemsSource = SignRegulation.Rules(s);
            RegulationExamples.ItemsSource = SignRegulation.Examples(s);
        }

        private void Naming_Changed(object sender, TextChangedEventArgs e)
        {
            if (_ready) ShowRegulation();
        }

        /// <summary>Размеры — как изначально (dwg_clean.bat): одной высоты 7 мм, ширина не больше 22 мм и т. д.</summary>
        private void SizeDefaults_Click(object sender, RoutedEventArgs e)
        {
            var d = new SignSettings();
            ModeHeight.IsChecked = d.SizeMode == SizeModes.Height;
            ModeWidth.IsChecked = d.SizeMode == SizeModes.Width;
            ModeBox.IsChecked = d.SizeMode == SizeModes.Box;
            ModeKeep.IsChecked = d.SizeMode == SizeModes.Keep;
            HeightBox.Text = N(d.Height); MaxWidthBox.Text = N(d.MaxWidth);
            WidthBox.Text = N(d.Width); MaxHeightBox.Text = N(d.MaxHeight);
            BoxWidth.Text = N(d.BoxWidth); BoxHeight.Text = N(d.BoxHeight);
        }

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
            s.Departments = Departments.Text; s.Leaders = Leaders.Text;
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
            ShowSectionOf(field);
            // раздел только что показан — фокус, когда он разложится
            Dispatcher.BeginInvoke(new Action(() => field.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
