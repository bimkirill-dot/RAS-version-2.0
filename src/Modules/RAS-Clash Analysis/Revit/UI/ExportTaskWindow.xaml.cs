using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Задача проекта «Задач выгрузки» (карандаш у задачи): название — оно же имя NWF, модели, файлы проверок (как у проекта или
    /// свои), куда ляжет — по папкам проекта; экспорт задачи. Правится копия: «Отмена» оставляет задачу как была.
    /// </summary>
    public partial class ExportTaskWindow : Window, INotifyPropertyChanged
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
        private readonly ObservableCollection<FileRow> _tests = new ObservableCollection<FileRow>();
        private bool _ownTests;
        private string _serverFolder;
        private readonly bool _ready;

        /// <summary>Корзина у файла проверок — только у своего списка.</summary>
        public Visibility OwnVisibility => _ownTests ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;

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
            DataContext = this;
            TestsList.ItemsSource = _tests;
            ShowTests(task.TestsFiles != null, task.TestsFiles);
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
                var picker = new ModelPickerWindow(RevitYear, _serverFolder ?? ExportNodes.Root(_task).Project.ServerFolder) { Owner = this };
                if (picker.ShowDialog() != true) return;
                picked = picker.Picked;
                // откуда добавляли — оттуда и следующий раз (папка проекта на сервере; поля для неё нет)
                var server = picked.Select(ExportNodes.ServerFolderOf).FirstOrDefault(f => f.Length > 0);
                if (server != null) _serverFolder = server;
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

        // ── файлы проверок: как у проекта или свои ──

        private void ShowTests(bool own, IEnumerable<string> list)
        {
            _ownTests = own;
            _tests.Clear();
            foreach (var f in own ? list : ExportNodes.Root(_task).Project.TestsFiles ?? new List<string>()) _tests.Add(new FileRow(f));
            TestsList.Opacity = own ? 1 : 0.5;   // как у проекта — бледным
            AddTests.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            NoTests.Visibility = _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TestsFrom.Text = own ? "" : "как у проекта";
            TestsLink.Content = own ? "Вернуть как у проекта" : "Изменить";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OwnVisibility)));
        }

        // «Изменить» — свои, начиная с проектных; «Вернуть» — снова проектные
        private void TestsLink_Click(object sender, RoutedEventArgs e) => ShowTests(!_ownTests, _tests.Select(r => r.Path).ToList());

        private void AddTests_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var f in dialog.FileNames)
                if (!_tests.Any(r => string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) _tests.Add(new FileRow(f));
            NoTests.Visibility = _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DeleteTests_Click(object sender, RoutedEventArgs e)
        {
            _tests.Remove((FileRow)((FrameworkElement)sender).DataContext);
            NoTests.Visibility = _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── экспорт ──

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ExportFiles.Filter,
                FileName = "Задача " + string.Concat(TaskName.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var draft = new ExportTask
                {
                    Name = TaskName.Text.Trim(), ModelFiles = _models.Select(m => m.Model).ToList(), TestsFiles = _ownTests ? _tests.Select(r => r.Path).ToList() : null,
                };
                ExportFiles.Save(dialog.FileName, ExportFiles.FromTask(draft));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт задачи не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _task.Name = TaskName.Text.Trim();
            _task.ModelFiles = _models.Select(m => m.Model).ToList();
            _task.TestsFiles = _ownTests ? _tests.Select(r => r.Path).ToList() : null;
            if (_serverFolder != null) ExportNodes.Root(_task).Project.ServerFolder = _serverFolder;
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate() =>
            ExportNodes.CheckTaskName(_task, TaskName.Text)
            ?? ExportNodes.CheckNwcNames(_models.Select(m => m.Name))
            ?? (_ownTests ? ExportNodes.CheckTestsFiles(_tests.Select(r => r.Path).ToList(), ExportProjectWindow.TestNames) : null);
    }
}
