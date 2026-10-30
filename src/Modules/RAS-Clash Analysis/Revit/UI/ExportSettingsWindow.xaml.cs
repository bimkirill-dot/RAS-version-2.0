using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Общие настройки «Задач выгрузки» (шестерёнка): папки строкой с метками и пример под строкой, свои метки, имя модели
    /// (обрезка, раздел, отдел — с примером), таблица отделов, вид, что удалить перед выгрузкой, временная папка. Примерное окно
    /// (30.10.2026): «Проверить» и «Настройки выгрузки NWC…» — следующими шагами.
    /// </summary>
    public partial class ExportSettingsWindow : Window
    {
        /// <summary>Своя метка в списке — правится на месте.</summary>
        public sealed class LabelRow
        {
            public string Name { get; set; }
        }

        // пример для подсказок под полями — имя модели по регламенту
        private const string SampleModel = "0000_01_01_S6_KJ01_01_M3 еще текст.rvt";

        private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "yyyyMMdd", "yyyy.MM.dd" };

        private readonly ExportSettings _settings;
        private readonly ObservableCollection<LabelRow> _labels;
        private readonly ObservableCollection<DepartmentTemplate> _departments;
        private readonly bool _ready;

        public ExportSettingsWindow(ExportSettings settings)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _settings = settings;

            NwcFolder.Text = settings.NwcFolder;
            ReportFolder.Text = settings.ReportFolder;
            var today = DateTime.Today;
            foreach (var f in DateFormats) DateFormat.Items.Add(new ComboBoxItem { Content = today.ToString(f), Tag = f });
            DateFormat.SelectedIndex = Math.Max(0, Array.IndexOf(DateFormats, settings.DateFormat));
            BuiltIn.Text = "Встроенные: " + string.Join("  ", ExportNames.BuiltInLabels.Select(l => "{" + l + "}"));
            _labels = new ObservableCollection<LabelRow>(settings.Labels.Select(l => new LabelRow { Name = l }));
            LabelsList.ItemsSource = _labels;

            TrimName.IsChecked = settings.TrimName;
            NameParts.Text = settings.NameParts.ToString();
            SectionPart.Text = settings.SectionPart.ToString();

            // правится копия: «Отмена» оставляет таблицу как была
            _departments = new ObservableCollection<DepartmentTemplate>(settings.Departments.Select(d =>
                new DepartmentTemplate { Department = d.Department, Etalon = d.Etalon, ViewTemplate = d.ViewTemplate }));
            DepartmentsList.ItemsSource = _departments;

            ViewName.Text = settings.ViewName;
            RemoveRevitLinks.IsChecked = settings.RemoveRevitLinks;
            RemoveCad.IsChecked = settings.RemoveCad;
            RemovePointClouds.IsChecked = settings.RemovePointClouds;
            RemoveIfc.IsChecked = settings.RemoveIfc;
            TempFolder.Text = settings.TempFolder;

            _ready = true;
            UpdateExamples();
        }

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) UpdateExamples();
        }

        private string SelectedDateFormat => (string)((ComboBoxItem)DateFormat.SelectedItem)?.Tag ?? DateFormats[0];

        /// <summary>Примеры под полями — на имени модели по регламенту и узле «Арман Кала → Блок 1».</summary>
        private void UpdateExamples()
        {
            int.TryParse(NameParts.Text, out var parts);
            int.TryParse(SectionPart.Text, out var sectionPart);
            var nwc = ExportNames.NwcName(SampleModel, TrimName.IsChecked == true, parts, out var matches);
            var section = ExportNames.Section(SampleModel, sectionPart);
            var department = ExportNames.Department(section);
            NameExample.Text = "Пример: " + SampleModel + " → " + nwc + ".nwc" + (matches ? "" : " (не по шаблону)") +
                               " · раздел " + Or(section) + " · отдел " + Or(department);

            var values = new Dictionary<string, string>
            {
                ["Проект"] = "Арман Кала",
                ["Блок"] = "Блок 1",
                ["Критичность"] = "Высокая критичность",
                ["Дата"] = DateTime.Today.ToString(SelectedDateFormat),
                ["Раздел"] = section,
                ["Отдел"] = department,
            };
            NwcExample.Text = "Пример: " + ExportNames.Fill(NwcFolder.Text, values) + "\\" + nwc + ".nwc";
            ReportExample.Text = "Пример: " + ExportNames.Fill(ReportFolder.Text, values);
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "—" : s;

        private void AddLabel_Click(object sender, RoutedEventArgs e) => _labels.Add(new LabelRow { Name = "" });

        private void DeleteLabel_Click(object sender, RoutedEventArgs e) =>
            _labels.Remove((LabelRow)((FrameworkElement)sender).DataContext);

        private void AddDepartment_Click(object sender, RoutedEventArgs e) =>
            _departments.Add(new DepartmentTemplate { ViewTemplate = ViewName.Text });

        private void DeleteDepartment_Click(object sender, RoutedEventArgs e) =>
            _departments.Remove((DepartmentTemplate)((FrameworkElement)sender).DataContext);

        private void BrowseEtalon_Click(object sender, RoutedEventArgs e)
        {
            var row = (DepartmentTemplate)((FrameworkElement)sender).DataContext;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Шаблон проекта Revit (*.rte)|*.rte", FileName = row.Etalon ?? "" };
            if (dialog.ShowDialog(this) != true) return;
            row.Etalon = dialog.FileName;
            DepartmentsList.Items.Refresh();
        }

        // открыть эталоны и найти шаблоны вида — нужен Revit; настройки выгрузки NWC — после прототипа выгрузки
        private void CheckDepartments_Click(object sender, RoutedEventArgs e) { }

        private void NwcOptions_Click(object sender, RoutedEventArgs e) { }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate(out var parts, out var sectionPart, out var labels);
            Error.Text = error ?? "";
            if (error != null) return;

            _settings.NwcFolder = NwcFolder.Text.Trim();
            _settings.ReportFolder = ReportFolder.Text.Trim();
            _settings.DateFormat = SelectedDateFormat;
            _settings.Labels = labels;
            _settings.TrimName = TrimName.IsChecked == true;
            _settings.NameParts = parts;
            _settings.SectionPart = sectionPart;
            _settings.Departments = _departments.Where(d => !string.IsNullOrWhiteSpace(d.Department)).ToList();
            _settings.ViewName = ViewName.Text.Trim();
            _settings.RemoveRevitLinks = RemoveRevitLinks.IsChecked == true;
            _settings.RemoveCad = RemoveCad.IsChecked == true;
            _settings.RemovePointClouds = RemovePointClouds.IsChecked == true;
            _settings.RemoveIfc = RemoveIfc.IsChecked == true;
            _settings.TempFolder = TempFolder.Text.Trim();
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate(out int parts, out int sectionPart, out List<string> labels)
        {
            labels = _labels.Select(l => (l.Name ?? "").Trim()).Where(l => l.Length > 0).ToList();
            sectionPart = 0;
            if (!int.TryParse(NameParts.Text, out parts) || parts < 1 || parts > 30)
                return "Частей через «_» — число от 1 до 30.";
            if (!int.TryParse(SectionPart.Text, out sectionPart) || sectionPart < 1 || sectionPart > parts)
                return "Раздел — номер части от 1 до " + parts + ".";
            var bad = labels.FirstOrDefault(l => l.IndexOfAny(new[] { '{', '}', '\\', '/' }) >= 0);
            if (bad != null) return "В имени метки «" + bad + "» — лишние знаки: { } \\ /.";
            var builtIn = labels.FirstOrDefault(l => ExportNames.BuiltInLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
            if (builtIn != null) return "Метка «" + builtIn + "» уже есть среди встроенных.";
            var twice = labels.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) return "Метка «" + twice.Key + "» — дважды.";
            var known = new HashSet<string>(ExportNames.BuiltInLabels.Concat(labels), StringComparer.OrdinalIgnoreCase);
            foreach (var field in new[] { NwcFolder.Text, ReportFolder.Text })
            {
                var unknown = ExportNames.LabelsIn(field).FirstOrDefault(l => !known.Contains(l));
                if (unknown != null) return "Метки {" + unknown + "} нет — добавьте её в «Метки» или поправьте строку папки.";
            }
            if (string.IsNullOrWhiteSpace(ViewName.Text)) return "Нужно имя вида для выгрузки.";
            return null;
        }
    }
}
