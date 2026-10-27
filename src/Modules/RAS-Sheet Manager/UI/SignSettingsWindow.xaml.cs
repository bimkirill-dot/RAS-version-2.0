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
    /// «Очистка», «Регламент наименования» (только для чтения). Таблица выбора и формула — в окне «Подпись» (из семейства),
    /// цвет подписей — всегда синий (разработчик, 27.10.2026).
    /// </summary>
    public partial class SignSettingsWindow : Window
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public SignSettings Result { get; private set; }

        /// <param name="showRegulation">Открыть сразу на регламенте наименования (ссылка из окна «Подпись»).</param>
        public SignSettingsWindow(SignSettings settings, bool offline, string acadFound, bool showRegulation = false)
        {
            // кисти и образцы ядра — до разметки: на них ссылаются стили окна
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            Nav.SelectedIndex = showRegulation ? 2 : 0;
            var s = settings.Copy();
            Result = s;
            Changed.Text = "Настройки общие для всех, кто добавляет подписи." +
                           (string.IsNullOrEmpty(s.ChangedBy) ? "" : " Изменены: " + s.ChangedBy + ", " + s.ChangedAt + ".") +
                           (offline ? " Сервер не ответил — показаны последние известные; сохранить сейчас не получится." : "");
            Folder.Text = s.Folder;
            ShowSize(s);
            Tolerance.Text = N(s.Tolerance);
            HatchOutline.IsChecked = s.Hatch != HatchModes.Drop;
            HatchDrop.IsChecked = s.Hatch == HatchModes.Drop;
            Acad.Text = s.Acad;
            AcadFound.Text = acadFound != null ? "Пусто — найти самому. Сейчас: " + acadFound : "Пусто — найти самому. AutoCAD не найден: новые подписи не очистить.";
            // регламент — только для чтения (разработчик, 27.10.2026)
            RegulationPattern.Text = SignRegulation.Pattern(s);
            RegulationRules.ItemsSource = SignRegulation.Rules(s);
            RegulationExamples.ItemsSource = SignRegulation.Examples(s);
            FileWordText.Text = s.FileWord;
            FormColumn.Text = s.FormColumn; FormKey.Text = s.FormKey; FormDefault.Text = s.FormDefault;
            GipMarker.Text = s.GipMarker; GipFormColumn.Text = s.GipFormColumn; GipFormKey.Text = s.GipFormKey; GipFormDefault.Text = s.GipFormDefault;
            LeadersText.Text = s.Leaders;
            SaveButton.IsEnabled = !offline;
        }

        private static string N(double v) => v.ToString("0.###", Ru);

        private void ShowSize(SignSettings s)
        {
            ModeHeight.IsChecked = s.SizeMode == SizeModes.Height;
            ModeWidth.IsChecked = s.SizeMode == SizeModes.Width;
            ModeBox.IsChecked = s.SizeMode == SizeModes.Box;
            ModeKeep.IsChecked = s.SizeMode == SizeModes.Keep;
            HeightBox.Text = N(s.Height); MaxWidthBox.Text = N(s.MaxWidth);
            WidthBox.Text = N(s.Width); MaxHeightBox.Text = N(s.MaxHeight);
            BoxWidth.Text = N(s.BoxWidth); BoxHeight.Text = N(s.BoxHeight);
        }

        private Panel[] Sections => new Panel[] { SectionSize, SectionClean, SectionNaming, SectionForm };

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

        /// <summary>Размеры — как изначально (dwg_clean.bat): одной высоты 7 мм, ширина не больше 22 мм и т. д.</summary>
        private void SizeDefaults_Click(object sender, RoutedEventArgs e) => ShowSize(new SignSettings());

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
            s.FormColumn = FormColumn.Text; s.FormKey = FormKey.Text; s.FormDefault = FormDefault.Text;
            s.GipMarker = GipMarker.Text; s.GipFormColumn = GipFormColumn.Text; s.GipFormKey = GipFormKey.Text; s.GipFormDefault = GipFormDefault.Text;
            foreach (var box in new[] { FormDefault, GipFormDefault })
                if (!double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    Fail("«Если фамилии нет» — число, например «1000».", box);
                    return;
                }
            if (!string.IsNullOrWhiteSpace(s.Acad) && !File.Exists(s.Acad.Trim()))
            {
                Fail("Нет файла «" + s.Acad.Trim() + "» — укажите accoreconsole.exe или оставьте поле пустым.", Acad);
                return;
            }
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
