using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Настройки проекта «Задач выгрузки» (карандаш у проекта): название, папка проекта, папка на Revit Server, папки NWC, NWF и
    /// отчётов внутри проекта, файл проверок, вид — общие для всех задач проекта. Примеры — на первой задаче с моделями.
    /// </summary>
    public partial class ExportProjectWindow : Window
    {
        // пример, пока в проекте нет задач с моделями
        private const string SampleModel = "0000_01_01_S1_KJ01_01_M3.rvt";

        private readonly ExportTask _project;
        private readonly ExportSettings _common;
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
            ServerFolder.Text = p.ServerFolder;
            NwcFolder.Text = p.NwcFolder;
            NwfFolder.Text = p.NwfFolder;
            ReportFolder.Text = p.ReportFolder;
            TestsFile.Text = p.TestsFile;
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
            var task = _project.Children.FirstOrDefault(c => c.Models > 0);
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

        private static string Example(string projectFolder, string folder, System.Collections.Generic.Dictionary<string, string> labels, string file)
        {
            var path = ExportNodes.InProject(projectFolder, ExportNames.Fill(folder, labels));
            return path.Length == 0 ? "Папка не задана" : "Пример: " + path + file;
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

        private void BrowseTests_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", FileName = TestsFile.Text };
            if (dialog.ShowDialog(this) == true) TestsFile.Text = dialog.FileName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _project.Name = ProjectName.Text.Trim();
            var p = _project.Project;
            p.Folder = Folder.Text.Trim();
            p.ServerFolder = ServerFolder.Text.Trim().TrimEnd('/');
            p.NwcFolder = NwcFolder.Text.Trim();
            p.NwfFolder = NwfFolder.Text.Trim();
            p.ReportFolder = ReportFolder.Text.Trim();
            p.TestsFile = TestsFile.Text.Trim();
            if (_hasView) p.ViewName = _view.Value;
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (ProjectName.Text.Trim().Length == 0) return "Нужно название.";
            var server = ServerFolder.Text.Trim();
            if (server.Length > 0 && !server.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                return "Модели на сервере — папка Revit Server: RSN://сервер/папка.";
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";
            return ExportNodes.CheckNwcFolder(NwcFolder.Text)
                   ?? ExportNodes.CheckNwfFolder(NwfFolder.Text)
                   ?? ExportNodes.CheckReportFolder(ReportFolder.Text)
                   ?? ExportNodes.CheckTestsFile(TestsFile.Text);
        }
    }
}
