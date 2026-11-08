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
    /// Общие настройки «Задач выгрузки» (шестерёнка) — всё, что одно на компанию, по разделам меню слева (разработчик, 07.11.2026):
    /// «Общее» — метки, отделы, копирование моделей; «Экспорт NWC» — параметры NWC Exporter (как в окне экспортёра Navisworks в
    /// Revit, те же подписи и порядок), имя NWC («обрезать всё после …»), очистка модели перед экспортом; «Настройки отчётов»
    /// (08.11.2026) — как вкладка «Отчёт» Clash Detective. «По умолчанию» — у параметров экспортёра и у отчётов. Правится копия:
    /// «Отмена» оставляет как было.
    /// </summary>
    public partial class ExportSettingsWindow : Window
    {
        // пример под «Имя NWC» и «Метками» — имя разработчика (03.11.2026)
        // с хвостом после «М3» — чтобы в примере была видна обрезка (разработчик, 04.11.2026)
        private const string SampleModel = "0000_01_01_S01_KJ01_01_М3_BIM_Alma";

        private readonly ExportSettings _settings;
        private readonly ObservableCollection<DepartmentTemplate> _departments = new ObservableCollection<DepartmentTemplate>();

        /// <summary>Галочка списка: поле или статус отчёта.</summary>
        public sealed class Pick
        {
            public string Name { get; set; }
            public bool On { get; set; }
        }

        private readonly ObservableCollection<Pick> _fields = new ObservableCollection<Pick>();
        private readonly ObservableCollection<Pick> _statuses = new ObservableCollection<Pick>();
        private readonly bool _ready;

        public ExportSettingsWindow(ExportSettings settings)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _settings = settings;
            var today = DateTime.Today;
            foreach (var f in ExportSettings.DateFormats) DateFormat.Items.Add(new ComboBoxItem { Content = today.ToString(f), Tag = f });
            DepartmentsList.ItemsSource = _departments;
            ReportFields.ItemsSource = _fields;
            ReportStatuses.ItemsSource = _statuses;
            foreach (var g in ReportOptions.GroupModes) GroupMode.Items.Add(g);
            FillAll(settings);
            Nav.SelectedIndex = 0;
            _ready = true;
            UpdateExample();
            _opened = State();
            Closing += (s, e) => e.Cancel = !ExportTaskPanel.MayClose(this, DialogResult == true || State() == _opened);
        }

        private readonly string _opened;

        /// <summary>
        /// Параметры выгрузки задачи (панель строки → «Настройки NWC Exporter» → «Изменить»; разработчик, 03.11.2026: «то же, что
        /// шестерёнка»): только раздел «Экспорт NWC» — параметры NWC Exporter и очистка модели; вид — в самой задаче; имя NWC, метки,
        /// отделы, копирование — только общие. Меню разделов не нужно — один раздел.
        /// </summary>
        public ExportSettingsWindow(ExportSettings settings, string taskName) : this(settings)
        {
            Title = "Параметры выгрузки — " + taskName;
            FileButtons.Visibility = Visibility.Collapsed;
            NavBox.Visibility = Visibility.Collapsed;
            Nav.SelectedIndex = 1;
            OptionRowOf(Scope).Visibility = Visibility.Collapsed;
            NameBlock.Visibility = Visibility.Collapsed;
            Width = 780;
        }

        /// <summary>Раздел меню — его содержимое справа, прокрутка — к началу.</summary>
        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            var tag = (string)((ListBoxItem)Nav.SelectedItem)?.Tag;
            General.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            NwcSection.Visibility = tag == "Nwc" ? Visibility.Visible : Visibility.Collapsed;
            ReportSection.Visibility = tag == "Report" ? Visibility.Visible : Visibility.Collapsed;
            RvtSection.Visibility = tag == "Rvt" ? Visibility.Visible : Visibility.Collapsed;
            Scroll.ScrollToTop();
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
            TrimAfter.Text, BlockPrefix.Text, DateFormat.SelectedIndex, ParallelCopies.Text,
            string.Join(";", _departments.Select(d => d.Department)), Picked(_fields), Picked(_statuses), GroupMode.SelectedIndex,
            FilteredOnly.IsChecked, RvtAudit.IsChecked, RvtWorksets.SelectedIndex, RvtOverwrite.IsChecked, RvtTrimAfter.Text);

        private static string Picked(ObservableCollection<Pick> picks) => string.Join(";", picks.Where(p => p.On).Select(p => p.Name));

        /// <summary>Окно — из настроек (открытие и «Импорт…»).</summary>
        private void FillAll(ExportSettings s)
        {
            FillNwc(s.Nwc);
            RemoveRevitLinks.IsChecked = s.RemoveRevitLinks;
            RemoveCad.IsChecked = s.RemoveCad;
            RemovePointClouds.IsChecked = s.RemovePointClouds;
            RemoveIfc.IsChecked = s.RemoveIfc;
            TrimAfter.Text = s.TrimAfter ?? "";
            BlockPrefix.Text = s.BlockPrefix;
            DateFormat.SelectedIndex = Math.Max(0, Array.IndexOf(ExportSettings.DateFormats, s.DateFormat));
            ParallelCopies.Text = s.ParallelCopies.ToString(CultureInfo.InvariantCulture);
            _departments.Clear();
            foreach (var d in s.Departments) _departments.Add(d.Clone());
            FillReport(s.Report ?? ReportOptions.Defaults());
            var r = s.Rvt ?? new RvtOptions();
            RvtAudit.IsChecked = r.Audit;
            RvtWorksets.SelectedIndex = r.KeepWorksets ? 0 : 1;
            RvtOverwrite.IsChecked = r.Overwrite;
            RvtTrimAfter.Text = r.TrimAfter ?? "";
        }

        /// <summary>Настройки отчётов — все поля и статусы Navisworks по порядку, отмечены — из настроек.</summary>
        private void FillReport(ReportOptions r)
        {
            _fields.Clear();
            foreach (var f in ReportOptions.AllFields) _fields.Add(new Pick { Name = f, On = r.Fields.Contains(f) });
            _statuses.Clear();
            foreach (var s in ReportOptions.AllStatuses) _statuses.Add(new Pick { Name = s, On = r.Statuses.Contains(s) });
            GroupMode.SelectedIndex = Math.Max(0, Array.IndexOf(ReportOptions.GroupModes, r.GroupMode));
            FilteredOnly.IsChecked = r.FilteredOnly;
        }

        private void ReportDefaults_Click(object sender, RoutedEventArgs e) => FillReport(ReportOptions.Defaults());

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
            ShowError("");
        }

        // имя вида нужно только при «Экспорт: Вид»
        private void Scope_Changed(object sender, SelectionChangedEventArgs e) => ViewName.IsEnabled = Scope.SelectedIndex == 1;

        private void Defaults_Click(object sender, RoutedEventArgs e) => FillNwc(new NwcOptions());

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) UpdateExample();
        }

        private string SelectedDateFormat => (string)((ComboBoxItem)DateFormat.SelectedItem)?.Tag ?? ExportSettings.DateFormats[0];

        /// <summary>Примеры на одном имени модели: под «Имя NWC» — имя NWC, под «Метками» — {Блок}, {Отдел}, {Дата} (по тому, что сейчас в окне).</summary>
        private void UpdateExample()
        {
            var rvt = ExportNames.NwcName(SampleModel, RvtTrimAfter.Text, out var rvtMatches);
            RvtNameExample.Text = "Пример: " + SampleModel + " → " + rvt + ".rvt" +
                                  (RvtTrimAfter.Text.Trim().Length == 0 ? " (имя модели)" : rvtMatches ? "" : " (в имени нет «" + RvtTrimAfter.Text.Trim() + "» — как есть)");
            var nwc = ExportNames.NwcName(SampleModel, TrimAfter.Text, out var matches);
            NameExample.Text = "Пример: " + SampleModel + " → " + nwc +
                               (TrimAfter.Text.Trim().Length == 0 ? " (не обрезается)" : matches ? "" : " (в имени нет «" + TrimAfter.Text.Trim() + "» — как есть)");
            var block = ExportNames.Block(SampleModel, BlockPrefix.Text);
            var department = ExportNames.Department(SampleModel, _departments.Select(d => d.Department));
            LabelsExample.Text = "Пример: " + SampleModel + " → {Блок} " + Or(block) + " · {Отдел} " + Or(department) +
                                 " · {Дата} " + ExportNodes.DateLabel(DateTime.Now, SelectedDateFormat);
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
                ShowError("Файл не записан: " + ex.Message);
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
                    ShowError("В файле — " + file.Kind.ToLowerInvariant() + ", а не общие настройки.");
                    return;
                }
                FillAll(file.Settings);
                UpdateExample();
                ShowError("");
                Info.Text = "Взято из «" + Path.GetFileName(dialog.FileName) + "» — сохранится по «Сохранить».";
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт общих настроек не прочитан", ex);
                ShowError(ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message);
            }
        }

        /// <summary>
        /// Что не так — красным внизу и окном поверх (разработчик, 07.11.2026: «предупреждение выходит красным где-то внизу, незаметно —
        /// пусть выходит в отдельном окошке»); пусто — только убрать строку.
        /// </summary>
        private void ShowError(string text)
        {
            Error.Text = text ?? "";
            if (!string.IsNullOrEmpty(text)) MessageBox.Show(this, text, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Настройки, как сейчас в окне; что-то не так — ошибка внизу и null.</summary>
        private ExportSettings Collect()
        {
            var error = Validate(out var faceting);
            ShowError(error ?? "");
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
            result.TrimAfter = TrimAfter.Text.Trim();
            // прежние сборки плагина у коллег читают галочку «по шаблону»
            result.TrimName = result.TrimAfter.Length > 0;
            result.BlockPrefix = BlockPrefix.Text.Trim();
            result.DateFormat = SelectedDateFormat;
            result.ParallelCopies = int.Parse(ParallelCopies.Text.Trim(), CultureInfo.InvariantCulture);
            result.Departments = _departments.Where(d => !string.IsNullOrWhiteSpace(d.Department))
                .Select(d => new DepartmentTemplate { Department = d.Department.Trim() })
                .ToList();
            result.Rvt = new RvtOptions
            {
                Audit = RvtAudit.IsChecked == true, KeepWorksets = RvtWorksets.SelectedIndex != 1, Overwrite = RvtOverwrite.IsChecked == true,
                TrimAfter = RvtTrimAfter.Text.Trim(),
            };
            result.Report = new ReportOptions
            {
                Fields = _fields.Where(p => p.On).Select(p => p.Name).ToList(),
                Statuses = _statuses.Where(p => p.On).Select(p => p.Name).ToList(),
                GroupMode = (string)GroupMode.SelectedItem ?? ReportOptions.GroupModes[0],
                FilteredOnly = FilteredOnly.IsChecked == true,
            };
            return result;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate(out double faceting)
        {
            if (!NwcOptions.TryFaceting(Faceting.Text, out faceting)) return "Коэффициент фасетизации — число больше нуля.";
            if (Scope.SelectedIndex == 1 && string.IsNullOrWhiteSpace(ViewName.Text)) return "Нужно имя вида для выгрузки.";
            var prefix = BlockPrefix.Text.Trim();
            if (prefix.Length == 0 || !prefix.All(char.IsLetter)) return "{Блок}: часть имени — буквы перед цифрами блока (например, S).";
            if (!int.TryParse(ParallelCopies.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var copies) || copies < 1 || copies > 8)
                return "Копий с Revit Server одновременно — число от 1 до 8.";
            if (!_statuses.Any(p => p.On)) return "Настройки отчётов: отметьте хотя бы один статус — иначе в отчёте будет только сводка.";
            return ExportNodes.CheckDepartmentCodes(_departments.Select(d => d.Department));
        }
    }
}
