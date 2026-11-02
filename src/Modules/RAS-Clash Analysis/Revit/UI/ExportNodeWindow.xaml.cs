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
    /// Настройки узла «Задач выгрузки» (карандаш): название, модели, папки, имя NWC, отделы, параметры выгрузки NWC.
    /// Раздел без «Своё» — значение узла выше (бледным, «как у «…»»); у самого верхнего — по умолчанию, параметры NWC — общие
    /// (шестерёнка). Правится копия: «Отмена» оставляет узел как был. Примерное окно (02.11.2026), рисунка нет.
    /// </summary>
    public partial class ExportNodeWindow : Window
    {
        /// <summary>Строка списка моделей: имя NWC — из имени модели по правилам раздела «Имя NWC» или поправленное руками.</summary>
        public sealed class ModelRow
        {
            private readonly ExportNodeWindow _window;
            public ExportModel Model { get; }

            public ModelRow(ExportNodeWindow window, ExportModel model)
            {
                _window = window;
                Model = model;
            }

            public string Path => Model.Path;

            private string Auto => ExportNames.NwcName(Path, _window.CurrentNames().Trim, _window.CurrentNames().Parts, out _);

            public string Name
            {
                get => ExportNodes.NwcNameOf(Model, _window.CurrentNames());
                // пусто или как из имени модели — снова «само»
                set
                {
                    var v = (value ?? "").Trim();
                    Model.NwcName = v.Length == 0 || v == Auto ? null : v;
                }
            }

            public Visibility MismatchVisibility
            {
                get
                {
                    var names = _window.CurrentNames();
                    ExportNames.NwcName(Path, names.Trim, names.Parts, out var matches);
                    return names.Trim && Model.NwcName == null && !matches ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            public string Department => Or(ExportNames.Department(ExportNames.Section(Path, _window._common.DepartmentPart)));
        }

        // пример под полями, пока у узла нет моделей — имя модели по регламенту
        private const string SampleModel = "0000_01_01_S6_KJ01_01_M3 еще текст.rvt";

        private readonly ExportTask _task;
        private readonly ExportSettings _common;
        private readonly ObservableCollection<ModelRow> _models = new ObservableCollection<ModelRow>();
        private readonly ObservableCollection<DepartmentTemplate> _departments = new ObservableCollection<DepartmentTemplate>();
        private ExportSettings _nwc;
        private readonly bool _ready;

        /// <summary>
        /// Выбор моделей: в Revit — окно Revit «Открыть» (сетевые папки и Revit Server, путь RSN://…); null — окно Windows
        /// (вне Revit — снимок окна).
        /// </summary>
        public Func<List<string>> PickModels { get; set; }

        public ExportNodeWindow(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _common = common;
            Title = "Настройки узла — " + task.Name;

            NodeName.Text = task.Name;
            foreach (var m in task.ModelFiles)
                _models.Add(new ModelRow(this, new ExportModel { Path = m.Path, NwcName = m.NwcName }));
            ModelsList.ItemsSource = _models;
            DepartmentsList.ItemsSource = _departments;

            var own = task.Own;
            // у задачи выше никого: папки, имя, отделы — всегда свои, без «Своё» (разработчик, 02.11.2026: снимок пустых
            // бледных папок «по умолчанию» у задачи); параметры NWC — «Своё» остаётся: иначе общие
            var top = task.Parent == null;
            foreach (var box in new[] { FoldersOwn, NamesOwn, DepartmentsOwn })
                box.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
            FoldersOwn.IsChecked = top || own.Folders != null;
            ShowFolders(own.Folders ?? InheritedFolders().Value);
            NamesOwn.IsChecked = top || own.Names != null;
            ShowNames(own.Names ?? InheritedNames().Value);
            DepartmentsOwn.IsChecked = top || own.Departments != null;
            ShowDepartments(own.Departments ?? InheritedDepartments().Value);
            NwcOwn.IsChecked = own.Nwc != null;
            _nwc = (own.Nwc ?? InheritedNwc().Value).Clone();

            _ready = true;
            UpdateAll();
        }

        // ── значения узлов выше; нигде не заданы — по умолчанию (параметры NWC — общие) ──

        private (FolderSettings Value, ExportTask From) InheritedFolders()
        {
            var (v, from) = ExportNodes.Inherited(_task, s => s.Folders);
            return (v ?? new FolderSettings(), from);
        }

        private (NameSettings Value, ExportTask From) InheritedNames()
        {
            var (v, from) = ExportNodes.Inherited(_task, s => s.Names);
            return (v ?? new NameSettings(), from);
        }

        private (List<DepartmentTemplate> Value, ExportTask From) InheritedDepartments()
        {
            var (v, from) = ExportNodes.Inherited(_task, s => s.Departments);
            return (v ?? new List<DepartmentTemplate>(), from);
        }

        private (ExportSettings Value, ExportTask From) InheritedNwc()
        {
            var (v, from) = ExportNodes.Inherited(_task, s => s.Nwc);
            return (v ?? _common, from);
        }

        // ── показать и прочитать разделы ──

        private void ShowFolders(FolderSettings f)
        {
            NwcFolder.Text = f.NwcFolder;
            ReportFolder.Text = f.ReportFolder;
        }

        private FolderSettings ReadFolders() => new FolderSettings
        {
            NwcFolder = NwcFolder.Text.Trim(),
            ReportFolder = ReportFolder.Text.Trim(),
        };

        private void ShowNames(NameSettings n)
        {
            TrimName.IsChecked = n.Trim;
            NameParts.Text = n.Parts.ToString();
        }

        /// <summary>Правила имени, как сейчас в окне; неверное число — по умолчанию (ошибку покажет «Сохранить»).</summary>
        internal NameSettings CurrentNames()
        {
            var n = new NameSettings { Trim = TrimName.IsChecked == true };
            if (int.TryParse(NameParts.Text, out var parts) && parts >= 1 && parts <= 30) n.Parts = parts;
            return n;
        }

        private void ShowDepartments(List<DepartmentTemplate> list)
        {
            _departments.Clear();
            foreach (var d in list) _departments.Add(d.Clone());
        }

        // ── «Своё» и подписи «как у …» ──

        private void Own_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            // сняли «Своё» — снова значение узла выше; поставили — правится то, что было видно
            if (sender == FoldersOwn && FoldersOwn.IsChecked != true) ShowFolders(InheritedFolders().Value);
            if (sender == NamesOwn && NamesOwn.IsChecked != true) ShowNames(InheritedNames().Value);
            if (sender == DepartmentsOwn && DepartmentsOwn.IsChecked != true) ShowDepartments(InheritedDepartments().Value);
            if (sender == NwcOwn && NwcOwn.IsChecked != true) _nwc = InheritedNwc().Value.Clone();
            UpdateAll();
        }

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) UpdateAll();
        }

        private void UpdateAll()
        {
            Section(FoldersOwn, FoldersPanel, FoldersFrom, InheritedFolders().From, "по умолчанию");
            Section(NamesOwn, NamesPanel, NamesFrom, InheritedNames().From, "по умолчанию");
            Section(DepartmentsOwn, DepartmentsPanel, DepartmentsFrom, InheritedDepartments().From, "не заданы");
            Section(NwcOwn, NwcPanel, NwcFrom, InheritedNwc().From, "общие (шестерёнка)");
            NwcSummary.Text = _nwc.Scope == NwcScope.View ? "вид «" + _nwc.ViewName + "»" : "целый проект";

            ModelsFrom.Text = ModelsText();
            ModelsHead.Visibility = _models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelsList.Items.Refresh();
            UpdateExamples();
        }

        private static void Section(CheckBox own, FrameworkElement panel, TextBlock from, ExportTask source, string none)
        {
            var isOwn = own.IsChecked == true;
            panel.IsEnabled = isOwn;
            from.Text = isOwn ? "" : source != null ? "как у «" + source.Name + "»" : none;
        }

        private string ModelsText()
        {
            if (_models.Count > 0) return _models.Count + " " + ExportTask.ModelsWord(_models.Count);
            if (_task.Children.Count > 0)
            {
                var inner = _task.Children.Sum(c => c.AllModels);
                return inner > 0 ? "своих нет — выгружаются модели вложенных: " + inner : "";
            }
            for (var p = _task.Parent; p != null; p = p.Parent)
                if (p.Models > 0) return "своих нет — выгружаются модели «" + p.Name + "»: " + p.Models;
            return "";
        }

        /// <summary>Примеры под полями — на первой модели узла (нет — имя по регламенту): метки {Блок} и {Отдел} — из её имени.</summary>
        private void UpdateExamples()
        {
            var names = CurrentNames();
            var model = _models.FirstOrDefault()?.Model ?? new ExportModel { Path = SampleModel };
            var nwc = ExportNodes.NwcNameOf(model, names);
            ExportNames.NwcName(model.Path, names.Trim, names.Parts, out var matches);
            NameExample.Text = "Пример: " + System.IO.Path.GetFileName(model.Path) + " → " + nwc + ".nwc" +
                               (matches || !names.Trim ? "" : " (не по шаблону)");

            var values = ExportNames.LabelValues(model.Path, _common, DateTime.Today);
            NwcExample.Text = NwcFolder.Text.Trim().Length == 0 ? "Папка не задана"
                : "Пример: " + ExportNames.Fill(NwcFolder.Text.Trim(), values) + "\\" + nwc + ".nwc";
            ReportExample.Text = ReportFolder.Text.Trim().Length == 0 ? "Папка не задана"
                : "Пример: " + ExportNames.Fill(ReportFolder.Text.Trim(), values);
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "—" : s;

        // ── модели ──

        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked;
            try
            {
                picked = PickModels != null ? PickModels() : PickFiles();
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: выбор моделей не открылся", ex);
                Error.Text = "Окно выбора моделей не открылось — подробности в журнале плагина.";
                return;
            }
            foreach (var f in picked) AddModel(f);
            UpdateAll();
        }

        private List<string> PickFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Модели Revit (*.rvt)|*.rvt", Multiselect = true };
            return dialog.ShowDialog(this) == true ? dialog.FileNames.ToList() : new List<string>();
        }

        private void AddModel(string path)
        {
            if (_models.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            _models.Add(new ModelRow(this, new ExportModel { Path = path }));
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            _models.Remove((ModelRow)((FrameworkElement)sender).DataContext);
            UpdateAll();
        }

        // ── отделы ──

        private void AddDepartment_Click(object sender, RoutedEventArgs e) =>
            _departments.Add(new DepartmentTemplate { ViewTemplate = _nwc.ViewName });

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

        // ── параметры выгрузки NWC узла — то же окно, что у шестерёнки ──

        private void NwcOptions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var copy = _nwc.Clone();
                if (new ExportSettingsWindow(copy) { Owner = this, ForNode = true, Title = "Параметры выгрузки NWC — " + NodeName.Text.Trim() }.ShowDialog() == true)
                    _nwc = copy;
                UpdateAll();
            }
            catch (Exception ex)
            {
                // исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»)
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: параметры выгрузки узла не открылись", ex);
                MessageBox.Show(this, "Параметры выгрузки не открылись — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _task.Name = NodeName.Text.Trim();
            _task.ModelFiles = _models.Select(m => m.Model).ToList();
            var own = _task.Own;
            own.Folders = FoldersOwn.IsChecked == true ? ReadFolders() : null;
            own.Names = NamesOwn.IsChecked == true ? CurrentNames() : null;
            own.Departments = DepartmentsOwn.IsChecked == true
                ? _departments.Where(d => !string.IsNullOrWhiteSpace(d.Department)).Select(d => d.Clone()).ToList()
                : null;
            own.Nwc = NwcOwn.IsChecked == true ? _nwc.Clone() : null;
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (NodeName.Text.Trim().Length == 0) return "Нужно название.";
            if (FoldersOwn.IsChecked == true)
            {
                var folder = ExportNodes.CheckFolder(NwcFolder.Text, "Папка NWC") ?? ExportNodes.CheckFolder(ReportFolder.Text, "Папка отчётов");
                if (folder != null) return folder;
            }
            if (NamesOwn.IsChecked == true)
            {
                if (!int.TryParse(NameParts.Text, out var parts) || parts < 1 || parts > 30)
                    return "Частей через «_» — число от 1 до 30.";
            }
            return ExportNodes.CheckNwcNames(_models.Select(m => m.Name));
        }
    }
}
