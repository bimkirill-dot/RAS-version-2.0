using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Задача проекта «Задач выгрузки» (карандаш у задачи): название — оно же имя NWF, модели, файл проверок (как у проекта или
    /// свой), куда ляжет — по папкам проекта. Правится копия: «Отмена» оставляет задачу как была.
    /// </summary>
    public partial class ExportTaskWindow : Window
    {
        /// <summary>Строка списка моделей: имя NWC — по общему правилу или поправленное руками; блок и отдел — из имени.</summary>
        public sealed class ModelRow
        {
            private readonly ExportSettings _common;
            public ExportModel Model { get; }

            public ModelRow(ExportSettings common, ExportModel model)
            {
                _common = common;
                Model = model;
            }

            public string Path => Model.Path;

            private string Auto => ExportNames.NwcName(Path, _common.TrimName, _common.NameParts, out _);

            public string Name
            {
                get => ExportNodes.NwcNameOf(Model, _common);
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
                    ExportNames.NwcName(Path, _common.TrimName, _common.NameParts, out var matches);
                    return _common.TrimName && Model.NwcName == null && !matches ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            public string Block => Or(ExportNames.Block(Path, _common.BlockPrefix));
            public string Department => Or(ExportNames.Department(Path, _common.Departments.Select(d => d.Department)));

            private static string Or(string s) => string.IsNullOrEmpty(s) ? "не найден" : s;
        }

        private readonly ExportTask _task;
        private readonly ExportSettings _common;
        private readonly ObservableCollection<ModelRow> _models = new ObservableCollection<ModelRow>();
        private readonly InheritField _tests;
        private readonly bool _ready;

        /// <summary>Версия Revit — серверы Revit Server для «Добавить модели…» берутся из её настроек (RSN.ini).</summary>
        public int RevitYear { get; set; } = 2026;

        public ExportTaskWindow(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _common = common;
            Title = "Задача — " + task.Name;
            TaskName.Text = task.Name;
            // новая задача: сразу печатать название
            Loaded += (s, e) =>
            {
                TaskName.Focus();
                TaskName.SelectAll();
            };
            foreach (var m in task.ModelFiles)
                _models.Add(new ModelRow(common, new ExportModel { Path = m.Path, NwcName = m.NwcName }));
            ModelsList.ItemsSource = _models;
            _tests = new InheritField(TestsFile, TestsFrom, TestsLink, BrowseTests, task.TestsFile,
                ExportNodes.Root(task).Project.TestsFile, "как у проекта", Changed);
            _ready = true;
            UpdateAll();
        }

        private void Changed()
        {
            if (_ready) UpdateAll();
        }

        private void Any_Changed(object sender, RoutedEventArgs e) => Changed();

        /// <summary>Задача, как она сейчас в окне, — для «куда»: название и модели из окна.</summary>
        private ExportTask Draft()
        {
            var draft = new ExportTask { Name = TaskName.Text.Trim(), ModelFiles = _models.Select(m => m.Model).ToList() };
            ExportNodes.Root(_task).Add(draft);
            return draft;
        }

        private void UpdateAll()
        {
            ModelsInfo.Text = _models.Count > 0 ? _models.Count + " " + ExportTask.ModelsWord(_models.Count) : "";
            ModelsHead.Visibility = _models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var draft = Draft();
            try
            {
                var model = draft.ModelFiles.FirstOrDefault();
                NwcWhere.Text = "NWC: " + (model == null ? "—" : ExportNodes.NwcPath(draft, model, _common));
                NwfWhere.Text = "NWF: " + ExportNodes.NwfPath(draft, _common);
                ReportWhere.Text = "Отчёты: " + ExportNodes.ReportPath(draft, _common, DateTime.Today);
            }
            finally
            {
                draft.Parent.Remove(draft);
            }
        }

        // ── модели ──

        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked;
            try
            {
                // окно Revit «Открыть» из плагина Revit Server не показывает (проверка разработчика, 02.11.2026) — своё окно; с
                // папки проекта на сервере
                var picker = new ModelPickerWindow(RevitYear, ExportNodes.Root(_task).Project.ServerFolder) { Owner = this };
                if (picker.ShowDialog() != true) return;
                picked = picker.Picked;
            }
            catch (Exception ex)
            {
                // исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»)
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: выбор моделей не открылся", ex);
                Error.Text = "Окно выбора моделей не открылось — подробности в журнале плагина.";
                return;
            }
            foreach (var f in picked)
                if (!_models.Any(m => string.Equals(m.Path, f, StringComparison.OrdinalIgnoreCase)))
                    _models.Add(new ModelRow(_common, new ExportModel { Path = f }));
            UpdateAll();
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            _models.Remove((ModelRow)((FrameworkElement)sender).DataContext);
            UpdateAll();
        }

        private void BrowseTests_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", FileName = TestsFile.Text };
            if (dialog.ShowDialog(this) == true) TestsFile.Text = dialog.FileName;
        }

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = ExportNodes.CheckTaskName(_task, TaskName.Text)
                        ?? ExportNodes.CheckNwcNames(_models.Select(m => m.Name))
                        ?? (_tests.Own ? ExportNodes.CheckTestsFile(TestsFile.Text) : null);
            Error.Text = error ?? "";
            if (error != null) return;

            _task.Name = TaskName.Text.Trim();
            _task.ModelFiles = _models.Select(m => m.Model).ToList();
            _task.TestsFile = _tests.Value;
            // папка проекта на сервере не задана — по первой модели с сервера: следующее «Добавить модели…» откроется там же
            var project = ExportNodes.Root(_task).Project;
            if (string.IsNullOrWhiteSpace(project.ServerFolder))
                project.ServerFolder = _task.ModelFiles.Select(m => ExportNodes.ServerFolderOf(m.Path)).FirstOrDefault(f => f.Length > 0) ?? "";
            DialogResult = true;
        }
    }
}
