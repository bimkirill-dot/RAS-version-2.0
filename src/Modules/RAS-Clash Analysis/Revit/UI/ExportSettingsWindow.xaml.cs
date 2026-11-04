using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Общие настройки «Задач выгрузки» (шестерёнка) — всё, что одно на компанию: параметры выгрузки NWC (как в окне экспортёра
    /// Navisworks в Revit, те же подписи и порядок; последним — обрезка имени NWC), что удалить перед выгрузкой, метки, отделы. «По умолчанию» —
    /// только параметры экспортёра, значения со снимка окна разработчика. Правится копия: «Отмена» оставляет как было.
    /// </summary>
    public partial class ExportSettingsWindow : Window
    {
        // пример под «Обрезать имя NWC» и «Метками» — имя разработчика (03.11.2026)
        // с хвостом после «М3» — чтобы в примере была видна обрезка (разработчик, 04.11.2026)
        private const string SampleModel = "0000_01_01_S01_KJ01_01_М3_BIM_Alma";

        private readonly ExportSettings _settings;
        private readonly ObservableCollection<DepartmentTemplate> _departments = new ObservableCollection<DepartmentTemplate>();
        private readonly bool _ready;

        public ExportSettingsWindow(ExportSettings settings)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _settings = settings;
            var today = DateTime.Today;
            foreach (var f in ExportSettings.DateFormats) DateFormat.Items.Add(new ComboBoxItem { Content = today.ToString(f), Tag = f });
            DepartmentsList.ItemsSource = _departments;
            FillAll(settings);
            _ready = true;
            UpdateExample();
            _opened = State();
            Closing += (s, e) => e.Cancel = !ExportTaskPanel.MayClose(this, DialogResult == true || State() == _opened);
        }

        private readonly string _opened;

        /// <summary>
        /// Параметры выгрузки задачи (карандаш задачи → «Настройки» → «Изменить»; разработчик, 03.11.2026: «то же, что шестерёнка»):
        /// экспортёр NWC и «Удалить из модели перед выгрузкой»; вид — в самой задаче, обрезка имени, метки и отделы — только общие.
        /// </summary>
        public ExportSettingsWindow(ExportSettings settings, string taskName) : this(settings)
        {
            Title = "Параметры выгрузки — " + taskName;
            FileButtons.Visibility = Visibility.Collapsed;
            OptionRowOf(Scope).Visibility = Visibility.Collapsed;
            OptionRowOf(TrimName).Visibility = Visibility.Collapsed;
            NameExample.Visibility = Visibility.Collapsed;
            // всё после «Удалить из модели перед выгрузкой» (метки, отделы) — общее для компании
            var row = OptionRowOf(RemoveIfc);
            var panel = (Panel)row.Parent;
            for (var i = panel.Children.IndexOf(row) + 1; i < panel.Children.Count; i++) panel.Children[i].Visibility = Visibility.Collapsed;
        }

        private static FrameworkElement OptionRowOf(FrameworkElement e)
        {
            while (!(e is Border) || e.Parent is Grid) e = (FrameworkElement)e.Parent;
            return e;
        }

        /// <summary>Всё, что правится в окне, — строкой: «Отмена» с правками спрашивает.</summary>
        private string State() => string.Join("|", Parameters.SelectedIndex, Coordinates.SelectedIndex, Faceting.Text,
            ElementProperties.IsChecked, Urls.IsChecked, Lights.IsChecked, RoomAsAttribute.IsChecked, LinkedCad.IsChecked,
            LinkedFiles.IsChecked, Parts.IsChecked, MissingMaterials.IsChecked, Levels.IsChecked, Scope.SelectedIndex, ViewName.Text,
            RoomGeometry.IsChecked, RemoveRevitLinks.IsChecked, RemoveCad.IsChecked, RemovePointClouds.IsChecked, RemoveIfc.IsChecked,
            TrimName.IsChecked, BlockPrefix.Text, DateFormat.SelectedIndex,
            string.Join(";", _departments.Select(d => d.Department)));

        /// <summary>Окно — из настроек (открытие и «Импорт…»).</summary>
        private void FillAll(ExportSettings s)
        {
            FillNwc(s.Nwc);
            RemoveRevitLinks.IsChecked = s.RemoveRevitLinks;
            RemoveCad.IsChecked = s.RemoveCad;
            RemovePointClouds.IsChecked = s.RemovePointClouds;
            RemoveIfc.IsChecked = s.RemoveIfc;
            TrimName.IsChecked = s.TrimName;
            BlockPrefix.Text = s.BlockPrefix;
            DateFormat.SelectedIndex = Math.Max(0, Array.IndexOf(ExportSettings.DateFormats, s.DateFormat));
            _departments.Clear();
            foreach (var d in s.Departments) _departments.Add(d.Clone());
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

        /// <summary>Примеры на одном имени модели: под «Обрезать имя NWC» — имя NWC, под «Метками» — {Блок}, {Отдел}, {Дата} (по тому, что сейчас в окне).</summary>
        private void UpdateExample()
        {
            var nwc = ExportNames.NwcName(SampleModel, TrimName.IsChecked == true, out var matches);
            NameExample.Text = "Пример: " + SampleModel + " → " + nwc + (matches || TrimName.IsChecked != true ? "" : " (не по шаблону)");
            var block = ExportNames.Block(SampleModel, BlockPrefix.Text);
            var department = ExportNames.Department(SampleModel, _departments.Select(d => d.Department));
            LabelsExample.Text = "Пример: " + SampleModel + " → {Блок} " + Or(block) + " · {Отдел} " + Or(department) +
                                 " · {Дата} " + DateTime.Today.ToString(SelectedDateFormat);
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "не найден" : s;

        private void AddDepartment_Click(object sender, RoutedEventArgs e) => _departments.Add(new DepartmentTemplate());

        private void DeleteDepartment_Click(object sender, RoutedEventArgs e)
        {
            _departments.Remove((DepartmentTemplate)((FrameworkElement)sender).DataContext);
            UpdateExample();
        }


        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var collected = Collect();
            if (collected == null) return;
            _settings.CopyFrom(collected);
            DialogResult = true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var collected = Collect();
            if (collected == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = ExportFiles.Filter, FileName = "Общие настройки выгрузки.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                ExportFiles.Save(dialog.FileName, ExportFiles.FromCommon(collected));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт общих настроек не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                if (file.Kind != ExportFile.Common)
                {
                    Error.Text = "В файле — " + file.Kind.ToLowerInvariant() + ", а не общие настройки.";
                    return;
                }
                FillAll(file.Settings);
                UpdateExample();
                Error.Text = "";
                Info.Text = "Взято из «" + Path.GetFileName(dialog.FileName) + "» — сохранится по «Сохранить».";
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт общих настроек не прочитан", ex);
                Error.Text = ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message;
            }
        }

        /// <summary>Настройки, как сейчас в окне; что-то не так — ошибка внизу и null.</summary>
        private ExportSettings Collect()
        {
            var error = Validate(out var faceting);
            Error.Text = error ?? "";
            if (error != null) return null;

            var result = new ExportSettings();
            var n = result.Nwc;
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

            result.RemoveRevitLinks = RemoveRevitLinks.IsChecked == true;
            result.RemoveCad = RemoveCad.IsChecked == true;
            result.RemovePointClouds = RemovePointClouds.IsChecked == true;
            result.RemoveIfc = RemoveIfc.IsChecked == true;
            result.TrimName = TrimName.IsChecked == true;
            result.BlockPrefix = BlockPrefix.Text.Trim();
            result.DateFormat = SelectedDateFormat;
            result.Departments = _departments.Where(d => !string.IsNullOrWhiteSpace(d.Department))
                .Select(d => new DepartmentTemplate { Department = d.Department.Trim() })
                .ToList();
            return result;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate(out double faceting)
        {
            if (!NwcOptions.TryFaceting(Faceting.Text, out faceting)) return "Коэффициент фасетизации — число больше нуля.";
            if (Scope.SelectedIndex == 1 && string.IsNullOrWhiteSpace(ViewName.Text)) return "Нужно имя вида для выгрузки.";
            var prefix = BlockPrefix.Text.Trim();
            if (prefix.Length == 0 || !prefix.All(char.IsLetter)) return "{Блок}: часть имени — буквы перед цифрами блока (например, S).";
            return ExportNodes.CheckDepartmentCodes(_departments.Select(d => d.Department));
        }
    }
}
