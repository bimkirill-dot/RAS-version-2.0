using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Задача проекта «Задач выгрузки» (карандаш у задачи; набросок 03.11.2026): название, файлы проверок, имя и папка NWF, папка
    /// отчётов, форматы, блоки (из моделей проекта; снятый задача не делает). Примеры — на первом блоке задачи. Правится копия:
    /// «Отмена» оставляет задачу как была.
    /// </summary>
    public partial class ExportTaskWindow : Window
    {
        /// <summary>Блок проекта с галочкой «задача его делает».</summary>
        public sealed class BlockItem
        {
            public string Name { get; set; }
            public bool On { get; set; }
        }

        private readonly ExportTask _task;
        private readonly ExportSettings _common;
        private readonly ObservableCollection<FileRow> _tests;
        private readonly List<BlockItem> _blocks;
        private readonly bool _ready;
        private readonly string _opened;

        public ExportTaskWindow(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _common = common;
            var o = task.Options;
            Title = string.IsNullOrEmpty(task.Name) ? "Новая задача" : "Задача — " + task.Name;
            TaskName.Text = task.Name;
            _tests = new ObservableCollection<FileRow>((o.TestsFiles ?? new List<string>()).Select(f => new FileRow(f)));
            TestsList.ItemsSource = _tests;
            NwfName.Text = o.NwfName;
            NwfFolder.Text = o.NwfFolder;
            ReportFolder.Text = o.ReportFolder;
            Xml.IsChecked = o.Xml;
            Html.IsChecked = o.Html;
            var skipped = o.SkippedBlocks ?? new List<string>();
            _blocks = ExportNodes.Blocks(ExportNodes.Root(task), common).Where(b => b.Name.Length > 0)
                .Select(b => new BlockItem { Name = b.Name, On = !skipped.Contains(b.Name, StringComparer.OrdinalIgnoreCase) })
                .ToList();
            BlocksList.ItemsSource = _blocks;
            NoBlocks.Visibility = _blocks.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            // новая задача и копия: сразу печатать название
            Loaded += (s, e) =>
            {
                TaskName.Focus();
                TaskName.SelectAll();
            };
            _ready = true;
            UpdateAll();
            _opened = State();
            Closing += (s, e) => e.Cancel = !MayClose(this, DialogResult == true || State() == _opened);
        }

        /// <summary>Всё, что правится в окне, — строкой: «Отмена» с правками спрашивает.</summary>
        private string State() => string.Join("|", TaskName.Text, string.Join(";", _tests.Select(r => r.Path)), NwfName.Text, NwfFolder.Text,
            ReportFolder.Text, Xml.IsChecked, Html.IsChecked, string.Join(";", _blocks.Select(b => b.Name + ">" + b.On)));

        /// <summary>Закрыть окно настроек: сохранено или правок нет — да; иначе — спросить (разбор 02.11.2026: «Отмена» молча теряла правки).</summary>
        internal static bool MayClose(Window window, bool clean) =>
            clean || MessageBox.Show(window, "Закрыть без сохранения? Правки пропадут.", window.Title, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) UpdateAll();
        }

        private void UpdateAll()
        {
            NoTests.Visibility = _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // примеры — на первом блоке задачи; блоков нет — «S1»
            var block = _blocks.FirstOrDefault(b => b.On)?.Name ?? _blocks.FirstOrDefault()?.Name ?? "S1";
            var root = ExportNodes.Root(_task).Project.Folder;
            var labels = ExportNodes.Labels(block, null, _common, DateTime.Today);
            var name = ExportNames.Fill(NwfName.Text.Trim(), labels) + ".nwf";
            NwfNameExample.Text = "Пример: " + name;
            var nwf = ExportNodes.InProject(root, ExportNames.Fill(NwfFolder.Text, labels)) + "\\" + name;
            NwfExample.Text = "Пример: " + ExportNodes.Short(root, nwf);
            NwfExample.ToolTip = nwf;
            var format = Html.IsChecked == true ? "HTML" : Xml.IsChecked == true ? "XML" : "";
            var report = ExportNodes.InProject(root, ExportNames.Fill(ReportFolder.Text, ExportNodes.Labels(block, null, _common, DateTime.Today, format)));
            ReportExample.Text = report.Length == 0 ? "Папка не задана" : "Пример: " + ExportNodes.Short(root, report);
            ReportExample.ToolTip = report;
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

        /// <summary>Папка NWF / отчётов: внутри папки проекта — коротко («S1»; S1 на {Блок} — меткой), вне — полный путь.</summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)((Button)sender).Tag;
            var root = ExportNodes.Root(_task).Project.Folder.Trim().TrimEnd('\\');
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
            UpdateAll();
        }

        private void DeleteTests_Click(object sender, RoutedEventArgs e)
        {
            _tests.Remove((FileRow)((FrameworkElement)sender).DataContext);
            UpdateAll();
        }

        // ── экспорт и сохранить ──

        /// <summary>Настройки задачи, как сейчас в окне; снятые блоки, которых уже нет в проекте, остаются снятыми.</summary>
        private TaskSettings Collect()
        {
            var shown = new HashSet<string>(_blocks.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
            return new TaskSettings
            {
                TestsFiles = _tests.Select(r => r.Path).ToList(),
                NwfName = NwfName.Text.Trim(),
                NwfFolder = NwfFolder.Text.Trim(),
                ReportFolder = ReportFolder.Text.Trim(),
                Xml = Xml.IsChecked == true,
                Html = Html.IsChecked == true,
                SkippedBlocks = _blocks.Where(b => !b.On).Select(b => b.Name)
                    .Concat((_task.Options.SkippedBlocks ?? new List<string>()).Where(s => !shown.Contains(s))).ToList(),
            };
        }

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
                ExportFiles.Save(dialog.FileName, ExportFiles.FromTask(new ExportTask { Name = TaskName.Text.Trim(), Options = Collect() }));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт задачи не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;
            _task.Name = TaskName.Text.Trim();
            _task.Options = Collect();
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (Xml.IsChecked != true && Html.IsChecked != true) return "Нужен хотя бы один формат отчётов.";
            return ExportNodes.CheckTaskName(_task, TaskName.Text)
                   ?? ExportNodes.CheckTestsFiles(_tests.Select(r => r.Path).ToList(), ExportProjectWindow.TestNames)
                   ?? ExportNodes.CheckNwfName(NwfName.Text)
                   ?? ExportNodes.CheckNwfFolder(NwfFolder.Text)
                   ?? ExportNodes.CheckNwfTaken(_task, NwfFolder.Text, NwfName.Text, _common)
                   ?? ExportNodes.CheckReportFolder(ReportFolder.Text);
        }
    }
}
