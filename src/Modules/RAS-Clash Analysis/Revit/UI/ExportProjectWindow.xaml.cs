using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Настройки проекта «Задач выгрузки» (карандаш у проекта): название, папка проекта, папки NWC, NWF и отчётов внутри проекта,
    /// файлы проверок, вид — общие для всех задач проекта. Примеры — на первой задаче с моделями. Внизу — экспорт проекта с
    /// задачами и импорт задач (добавятся по «Сохранить»).
    /// </summary>
    public partial class ExportProjectWindow : Window
    {
        // пример, пока в проекте нет задач с моделями
        private const string SampleModel = "0000_01_01_S1_KJ01_01_M3.rvt";

        private readonly ExportTask _project;
        private readonly ExportSettings _common;
        private readonly ObservableCollection<FileRow> _tests;
        private readonly List<ExportTask> _imported = new List<ExportTask>();
        private readonly InheritField _view;
        private readonly bool _hasView;
        private readonly bool _ready;

        public ExportProjectWindow(ExportTask project, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _project = project;
            _common = common;
            var p = project.Project;
            Title = "Проект — " + project.Name;
            ProjectName.Text = project.Name;
            Folder.Text = p.Folder;
            NwcFolder.Text = p.NwcFolder;
            NwfFolder.Text = p.NwfFolder;
            ReportFolder.Text = p.ReportFolder;
            _tests = new ObservableCollection<FileRow>((p.TestsFiles ?? new List<string>()).Select(f => new FileRow(f)));
            TestsList.ItemsSource = _tests;
            // разработчик, 02.11.2026: не «как в общих», а «по общим настройкам»
            _view = new InheritField(ViewName, ViewFrom, ViewLink, null, p.ViewName, common.Nwc.ViewName, "по общим настройкам", Changed);
            _hasView = common.Nwc.Scope == NwcScope.View;
            foreach (var e in new FrameworkElement[] { ViewCaption, ViewName, ViewState })
                e.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;
            // новый проект: сразу печатать название
            Loaded += (s, e) =>
            {
                ProjectName.Focus();
                ProjectName.SelectAll();
            };
            _ready = true;
            UpdateExamples();
        }

        private void Changed()
        {
            if (_ready) UpdateExamples();
        }

        private void Any_Changed(object sender, RoutedEventArgs e) => Changed();

        /// <summary>Примеры — на первой задаче с моделями и её первой модели; нет — задача «S1» с моделью по регламенту.</summary>
        private void UpdateExamples()
        {
            var task = _project.Children.Concat(_imported).FirstOrDefault(c => c.Models > 0);
            if (task == null)
            {
                task = new ExportTask { Name = "S1" };
                task.ModelFiles.Add(new ExportModel { Path = SampleModel });
            }
            var model = task.ModelFiles[0];
            var labels = ExportNodes.Labels(task, model, _common, DateTime.Today);
            var folder = Folder.Text.Trim();
            NwcExample.Text = Example(folder, NwcFolder.Text, labels, "\\" + ExportNodes.NwcNameOf(model, _common) + ".nwc");
            NwfExample.Text = Example(folder, NwfFolder.Text, labels, "\\" + task.Name.Trim() + ".nwf");
            ReportExample.Text = Example(folder, ReportFolder.Text, labels, "");
        }

        private static string Example(string projectFolder, string folder, Dictionary<string, string> labels, string file)
        {
            var path = ExportNodes.InProject(projectFolder, ExportNames.Fill(folder, labels));
            return path.Length == 0 ? "Папка не задана" : "Пример: " + ExportNodes.Short(projectFolder, path + file);
        }

        /// <summary>Метка под строкой — в строку, в место курсора.</summary>
        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (!(button.Tag is TextBox box)) return;
            var label = (string)button.Content;
            var at = Math.Min(box.CaretIndex, box.Text.Length);
            box.Text = box.Text.Insert(at, label);
            box.Focus();
            box.CaretIndex = at + label.Length;
        }

        // ── «Обзор» ──

        private void BrowseProject_Click(object sender, RoutedEventArgs e)
        {
            var path = FolderDialog.Pick(this, Folder.Text, "Папка проекта");
            if (path != null) Folder.Text = path;
        }

        /// <summary>Папка NWC / NWF / отчётов: внутри папки проекта — коротко («S1\NWC»; S1 на {Блок} — меткой), вне — полный путь.</summary>
        private void BrowseInner_Click(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)((Button)sender).Tag;
            var root = Folder.Text.Trim().TrimEnd('\\');
            var path = FolderDialog.Pick(this, root, "Папка внутри проекта");
            if (path == null) return;
            box.Text = root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length + 1)
                : path;
        }

        // ── файлы проверок ──

        private void AddTests_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var f in dialog.FileNames)
                if (!_tests.Any(r => string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) _tests.Add(new FileRow(f));
        }

        private void DeleteTests_Click(object sender, RoutedEventArgs e) => _tests.Remove((FileRow)((FrameworkElement)sender).DataContext);

        // ── экспорт и импорт ──

        private ProjectSettings Collect() => new ProjectSettings
        {
            Folder = Folder.Text.Trim(),
            ServerFolder = _project.Project.ServerFolder,
            NwcFolder = NwcFolder.Text.Trim(),
            NwfFolder = NwfFolder.Text.Trim(),
            ReportFolder = ReportFolder.Text.Trim(),
            TestsFiles = _tests.Select(r => r.Path).ToList(),
            ViewName = _hasView ? _view.Value : _project.Project.ViewName,
        };

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ExportFiles.Filter,
                FileName = Safe("Проект " + ProjectName.Text.Trim()) + ".json",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                ExportFiles.Save(dialog.FileName, ExportFiles.FromProject(ProjectName.Text.Trim(), Collect(), _project.Children.Concat(_imported)));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт проекта не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        /// <summary>Задача или все задачи проекта из файла — в этот проект; добавятся по «Сохранить».</summary>
        private void ImportTasks_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                var tasks = file.Kind == ExportFile.Task ? new List<TaskFile> { file.TaskData }
                    : file.Kind == ExportFile.Project ? file.ProjectData.Tasks
                    : null;
                if (tasks == null)
                {
                    Error.Text = "В файле — общие настройки, а не задачи: их открывают в шестерёнке.";
                    return;
                }
                foreach (var t in tasks)
                    _imported.Add(ExportFiles.ToTask(t, _project.Children.Concat(_imported).Select(c => c.Name)));
                Error.Text = "";
                Info.Text = "Добавятся по «Сохранить»: " + string.Join(", ", _imported.Select(t => t.Name));
                UpdateExamples();
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт задач не прочитан", ex);
                Error.Text = ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message;
            }
        }

        private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _project.Name = ProjectName.Text.Trim();
            var s = Collect();
            var p = _project.Project;
            p.Folder = s.Folder;
            p.NwcFolder = s.NwcFolder;
            p.NwfFolder = s.NwfFolder;
            p.ReportFolder = s.ReportFolder;
            p.TestsFiles = s.TestsFiles;
            p.ViewName = s.ViewName;
            foreach (var t in _imported) _project.Add(t);
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (ProjectName.Text.Trim().Length == 0) return "Нужно название.";
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";
            return ExportNodes.CheckNwcFolder(NwcFolder.Text)
                   ?? ExportNodes.CheckNwfFolder(NwfFolder.Text)
                   ?? ExportNodes.CheckReportFolder(ReportFolder.Text)
                   ?? ExportNodes.CheckTestsFiles(_tests.Select(r => r.Path).ToList(), TestNames);
        }

        /// <summary>Названия проверок файла (.xml Clash Detective) — тем же чтением, что на сайте («Категории проверок»).</summary>
        internal static IEnumerable<string> TestNames(string file)
        {
            using (var stream = File.OpenRead(file))
                return TestCategories.NamesFromXml(stream);
        }
    }
}
