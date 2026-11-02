using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Общие настройки «Задач выгрузки» (шестерёнка) — всё, что одно на компанию: параметры выгрузки NWC (как в окне экспортёра
    /// Navisworks в Revit, те же подписи и порядок), что удалить перед выгрузкой, имя модели и метки, отделы. «По умолчанию» —
    /// только параметры экспортёра, значения со снимка окна разработчика. Правится копия: «Отмена» оставляет как было.
    /// </summary>
    public partial class ExportSettingsWindow : Window
    {
        // пример под «Именем модели» — имя по регламенту
        private const string SampleModel = "0000_01_01_S6_KJ01_01_M3 еще текст.rvt";

        private readonly ExportSettings _settings;
        private readonly ObservableCollection<DepartmentTemplate> _departments;
        private readonly bool _ready;

        public ExportSettingsWindow(ExportSettings settings)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _settings = settings;

            FillNwc(settings.Nwc);
            RemoveRevitLinks.IsChecked = settings.RemoveRevitLinks;
            RemoveCad.IsChecked = settings.RemoveCad;
            RemovePointClouds.IsChecked = settings.RemovePointClouds;
            RemoveIfc.IsChecked = settings.RemoveIfc;
            TrimName.IsChecked = settings.TrimName;
            NameParts.Text = settings.NameParts.ToString();
            BlockPrefix.Text = settings.BlockPrefix;
            var today = DateTime.Today;
            foreach (var f in ExportSettings.DateFormats) DateFormat.Items.Add(new ComboBoxItem { Content = today.ToString(f), Tag = f });
            DateFormat.SelectedIndex = Math.Max(0, Array.IndexOf(ExportSettings.DateFormats, settings.DateFormat));
            _departments = new ObservableCollection<DepartmentTemplate>(settings.Departments.Select(d => d.Clone()));
            DepartmentsList.ItemsSource = _departments;

            _ready = true;
            UpdateExample();
        }

        private void FillNwc(NwcOptions s)
        {
            Parameters.SelectedIndex = (int)s.Parameters;
            Coordinates.SelectedIndex = s.SharedCoordinates ? 1 : 0;
            Faceting.Text = s.FacetingFactor.ToString(CultureInfo.CurrentCulture);
            ElementProperties.IsChecked = s.ConvertElementProperties;
            Urls.IsChecked = s.ConvertUrls;
            Lights.IsChecked = s.ConvertLights;
            RoomAsAttribute.IsChecked = s.RoomAsAttribute;
            LinkedCad.IsChecked = s.ConvertLinkedCad;
            LinkedFiles.IsChecked = s.ConvertLinkedFiles;
            Parts.IsChecked = s.ConvertParts;
            MissingMaterials.IsChecked = s.FindMissingMaterials;
            Levels.IsChecked = s.DivideIntoLevels;
            Scope.SelectedIndex = s.Scope == NwcScope.View ? 1 : 0;
            ViewName.Text = s.ViewName;
            RoomGeometry.IsChecked = s.RoomGeometry;
            Error.Text = "";
        }

        // имя вида нужно только при «Экспорт: Вид»
        private void Scope_Changed(object sender, SelectionChangedEventArgs e) => ViewName.IsEnabled = Scope.SelectedIndex == 1;

        private void Defaults_Click(object sender, RoutedEventArgs e) => FillNwc(new NwcOptions());

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) UpdateExample();
        }

        private string SelectedDateFormat => (string)((ComboBoxItem)DateFormat.SelectedItem)?.Tag ?? ExportSettings.DateFormats[0];

        /// <summary>Пример под «Именем модели»: имя NWC, блок, отдел — по тому, что сейчас в окне.</summary>
        private void UpdateExample()
        {
            int.TryParse(NameParts.Text, out var parts);
            var nwc = ExportNames.NwcName(SampleModel, TrimName.IsChecked == true, parts, out var matches);
            var block = ExportNames.Block(SampleModel, BlockPrefix.Text);
            var department = ExportNames.Department(SampleModel, _departments.Select(d => d.Department));
            NameExample.Text = "Пример: " + SampleModel + " → " + nwc + ".nwc" + (matches || TrimName.IsChecked != true ? "" : " (не по шаблону)") +
                               " · {Блок} " + Or(block) + " · {Отдел} " + Or(department) +
                               " · {Дата} " + DateTime.Today.ToString(SelectedDateFormat);
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "не найден" : s;

        private void AddDepartment_Click(object sender, RoutedEventArgs e) => _departments.Add(new DepartmentTemplate());

        private void DeleteDepartment_Click(object sender, RoutedEventArgs e)
        {
            _departments.Remove((DepartmentTemplate)((FrameworkElement)sender).DataContext);
            UpdateExample();
        }

        private void BrowseEtalon_Click(object sender, RoutedEventArgs e)
        {
            var row = (DepartmentTemplate)((FrameworkElement)sender).DataContext;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Шаблон проекта Revit (*.rte)|*.rte", FileName = row.Etalon ?? "" };
            if (dialog.ShowDialog(this) != true) return;
            row.Etalon = dialog.FileName;
            DepartmentsList.Items.Refresh();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate(out var faceting, out var parts);
            Error.Text = error ?? "";
            if (error != null) return;

            var n = _settings.Nwc;
            n.Parameters = (NwcParameters)Parameters.SelectedIndex;
            n.SharedCoordinates = Coordinates.SelectedIndex == 1;
            n.FacetingFactor = faceting;
            n.ConvertElementProperties = ElementProperties.IsChecked == true;
            n.ConvertUrls = Urls.IsChecked == true;
            n.ConvertLights = Lights.IsChecked == true;
            n.RoomAsAttribute = RoomAsAttribute.IsChecked == true;
            n.ConvertLinkedCad = LinkedCad.IsChecked == true;
            n.ConvertLinkedFiles = LinkedFiles.IsChecked == true;
            n.ConvertParts = Parts.IsChecked == true;
            n.FindMissingMaterials = MissingMaterials.IsChecked == true;
            n.DivideIntoLevels = Levels.IsChecked == true;
            n.Scope = Scope.SelectedIndex == 1 ? NwcScope.View : NwcScope.Model;
            n.ViewName = ViewName.Text.Trim();
            n.RoomGeometry = RoomGeometry.IsChecked == true;

            _settings.RemoveRevitLinks = RemoveRevitLinks.IsChecked == true;
            _settings.RemoveCad = RemoveCad.IsChecked == true;
            _settings.RemovePointClouds = RemovePointClouds.IsChecked == true;
            _settings.RemoveIfc = RemoveIfc.IsChecked == true;
            _settings.TrimName = TrimName.IsChecked == true;
            _settings.NameParts = parts;
            _settings.BlockPrefix = BlockPrefix.Text.Trim();
            _settings.DateFormat = SelectedDateFormat;
            _settings.Departments = _departments.Where(d => !string.IsNullOrWhiteSpace(d.Department))
                .Select(d => new DepartmentTemplate { Department = d.Department.Trim(), Etalon = d.Etalon?.Trim(), ViewTemplate = d.ViewTemplate?.Trim() })
                .ToList();
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate(out double faceting, out int parts)
        {
            parts = 0;
            if (!NwcOptions.TryFaceting(Faceting.Text, out faceting)) return "Коэффициент фасетизации — число больше нуля.";
            if (Scope.SelectedIndex == 1 && string.IsNullOrWhiteSpace(ViewName.Text)) return "Нужно имя вида для выгрузки.";
            if (!int.TryParse(NameParts.Text, out parts) || parts < 1 || parts > 30) return "Частей через «_» — число от 1 до 30.";
            var prefix = BlockPrefix.Text.Trim();
            if (prefix.Length == 0 || !prefix.All(char.IsLetter)) return "{Блок}: часть имени — буквы перед цифрами блока (например, S).";
            return ExportNodes.CheckDepartmentCodes(_departments.Select(d => d.Department));
        }
    }
}
