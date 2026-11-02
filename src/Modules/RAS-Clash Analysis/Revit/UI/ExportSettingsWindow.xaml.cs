using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Общие настройки «Задач выгрузки» (шестерёнка): параметры выгрузки NWC — как в окне экспортёра Navisworks в Revit, те же
    /// подписи и порядок (разработчик, 02.11.2026). «По умолчанию» — значения с его снимка окна.
    /// </summary>
    public partial class ExportSettingsWindow : Window
    {
        private readonly ExportSettings _settings;

        /// <summary>Свои параметры выгрузки узла: без «Удалить из модели перед выгрузкой» и «Меток» — они только общие.</summary>
        public bool ForNode
        {
            set => CommonPart.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }

        public ExportSettingsWindow(ExportSettings settings)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _settings = settings;
            var today = System.DateTime.Today;
            foreach (var f in ExportSettings.DateFormats) DateFormat.Items.Add(new ComboBoxItem { Content = today.ToString(f), Tag = f });
            Fill(settings);
        }

        private void Fill(ExportSettings s)
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
            RemoveRevitLinks.IsChecked = s.RemoveRevitLinks;
            RemoveCad.IsChecked = s.RemoveCad;
            RemovePointClouds.IsChecked = s.RemovePointClouds;
            RemoveIfc.IsChecked = s.RemoveIfc;
            DateFormat.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(ExportSettings.DateFormats, s.DateFormat));
            BlockPart.Text = s.BlockPart.ToString();
            DepartmentPart.Text = s.DepartmentPart.ToString();
            Error.Text = "";
        }

        // имя вида нужно только при «Экспорт: Вид»
        private void Scope_Changed(object sender, SelectionChangedEventArgs e) => ViewName.IsEnabled = Scope.SelectedIndex == 1;

        private void Defaults_Click(object sender, RoutedEventArgs e) => Fill(new ExportSettings());

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ExportSettings.TryFaceting(Faceting.Text, out var faceting))
            {
                Error.Text = "Коэффициент фасетизации — число больше нуля.";
                return;
            }
            // у своих параметров узла меток нет — числа в скрытых полях те же, что у общих
            if (!int.TryParse(BlockPart.Text, out var blockPart) || blockPart < 1 || blockPart > 30 ||
                !int.TryParse(DepartmentPart.Text, out var departmentPart) || departmentPart < 1 || departmentPart > 30)
            {
                Error.Text = "Номер части имени модели у {Блок} и {Отдел} — от 1 до 30.";
                return;
            }
            var scope = Scope.SelectedIndex == 1 ? NwcScope.View : NwcScope.Model;
            if (scope == NwcScope.View && string.IsNullOrWhiteSpace(ViewName.Text))
            {
                Error.Text = "Нужно имя вида для выгрузки.";
                return;
            }

            _settings.Parameters = (NwcParameters)Parameters.SelectedIndex;
            _settings.SharedCoordinates = Coordinates.SelectedIndex == 1;
            _settings.FacetingFactor = faceting;
            _settings.ConvertElementProperties = ElementProperties.IsChecked == true;
            _settings.ConvertUrls = Urls.IsChecked == true;
            _settings.ConvertLights = Lights.IsChecked == true;
            _settings.RoomAsAttribute = RoomAsAttribute.IsChecked == true;
            _settings.ConvertLinkedCad = LinkedCad.IsChecked == true;
            _settings.ConvertLinkedFiles = LinkedFiles.IsChecked == true;
            _settings.ConvertParts = Parts.IsChecked == true;
            _settings.FindMissingMaterials = MissingMaterials.IsChecked == true;
            _settings.DivideIntoLevels = Levels.IsChecked == true;
            _settings.Scope = scope;
            _settings.ViewName = ViewName.Text.Trim();
            _settings.RoomGeometry = RoomGeometry.IsChecked == true;
            _settings.RemoveRevitLinks = RemoveRevitLinks.IsChecked == true;
            _settings.RemoveCad = RemoveCad.IsChecked == true;
            _settings.RemovePointClouds = RemovePointClouds.IsChecked == true;
            _settings.RemoveIfc = RemoveIfc.IsChecked == true;
            _settings.DateFormat = (string)((ComboBoxItem)DateFormat.SelectedItem)?.Tag ?? ExportSettings.DateFormats[0];
            _settings.BlockPart = blockPart;
            _settings.DepartmentPart = departmentPart;
            DialogResult = true;
        }
    }
}
