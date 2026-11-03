using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Настройки задачи или подзадачи «Задач выгрузки» (карандаш; «Дерево задач с наследованием», 03.11.2026): название, модели,
    /// файлы проверок, имя NWF, папка проекта, папки NWC / NWF / отчётов, форматы, вид. Каждая настройка — своя или от уровня выше
    /// (бледным, «от «…»», «Изменить»); у задачи верхнего уровня всё своё. Правятся копии: «Отмена» оставляет как было.
    /// </summary>
    public partial class ExportTaskWindow : Window, INotifyPropertyChanged
    {
        /// <summary>Строка списка моделей: блок (у первой модели блока; без блока в имени — поле), имя NWC, отдел.</summary>
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
            public string FileName => new FileRow(Path).Name;
            public string Folder => new FileRow(Path).Folder;

            /// <summary>Блока в имени модели нет — вписывается руками («Генплан.rvt»).</summary>
            public bool Manual => ExportNames.Block(Path, _common.BlockPrefix).Length == 0;
            public string Effective => ExportNodes.BlockOf(Model, _common);

            public string Block
            {
                get => Model.Block ?? "";
                set => Model.Block = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            /// <summary>Первая строка блока — над ней отступ и название блока.</summary>
            public bool First { get; set; }
            public string BlockTitle => Effective;
            public Visibility TitleVisibility => First && !Manual ? Visibility.Visible : Visibility.Collapsed;
            public Visibility BoxVisibility => Manual ? Visibility.Visible : Visibility.Collapsed;
            public Thickness Gap => new Thickness(0, First ? 10 : 0, 0, 4);

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

            public string Department
            {
                get
                {
                    var d = ExportNames.Department(Path, _common.Departments.Select(x => x.Department));
                    return string.IsNullOrEmpty(d) ? "не найден" : d;
                }
            }
        }

        // пример, пока моделей нет
        private const string SampleModel = "0000_01_01_S1_KJ01_01_M3.rvt";

        private readonly ExportTask _task;
        private readonly ExportTask _parent;
        private readonly ExportSettings _common;
        private readonly List<ModelRow> _models = new List<ModelRow>();
        private readonly ObservableCollection<FileRow> _tests = new ObservableCollection<FileRow>();
        private readonly List<ExportTask> _imported = new List<ExportTask>();
        private readonly InheritField _nwfName, _folder, _nwc, _nwf, _report, _view;
        private readonly bool _hasView;
        private readonly bool _ready;
        private readonly string _opened;
        private bool _modelsOwn, _testsOwn, _formatsOwn;
        private string _serverFolder;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Корзина у файла проверок — только у своего списка.</summary>
        public Visibility TestsEditVisibility => _testsOwn ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Названия соседей по уровню — название не должно повторяться (две одинаковые строки не различить).</summary>
        public IReadOnlyCollection<string> TakenNames { get; set; } = new string[0];

        /// <summary>Версия Revit — серверы Revit Server для «Добавить модели…» берутся из её настроек (RSN.ini).</summary>
        public int RevitYear { get; set; } = 2026;

        public ExportTaskWindow(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _parent = task.Parent;
            _common = common;
            var s = task.Settings;
            Title = string.IsNullOrEmpty(task.Name) ? (_parent == null ? "Новая задача" : "Новая подзадача") : task.Name;
            TaskName.Text = task.Name;
            DataContext = this;

            _modelsOwn = _parent == null || s.Models != null;
            if (_modelsOwn) _models.AddRange(Copies(s.Models ?? ExportNodes.ModelsOf(task)).Select(m => new ModelRow(common, m)));
            _testsOwn = _parent == null || s.TestsFiles != null;
            TestsList.ItemsSource = _tests;
            FillTests(_testsOwn ? s.TestsFiles ?? ExportNodes.TestsOf(task) : ExportNodes.TestsOf(_parent));
            _formatsOwn = _parent == null || s.Xml != null;
            Xml.IsChecked = _formatsOwn ? s.Xml ?? true : ExportNodes.XmlOf(_parent);
            Html.IsChecked = _formatsOwn ? s.Html ?? true : ExportNodes.HtmlOf(_parent);

            _nwfName = Field(NwfName, NwfNameFrom, NwfNameLink, x => x.NwfName, ExportNodes.NwfNameOf);
            _folder = Field(Folder, FolderFrom, FolderLink, x => x.Folder, ExportNodes.FolderOf, FolderBrowse);
            _nwc = Field(NwcFolder, NwcFrom, NwcLink, x => x.NwcFolder, ExportNodes.NwcFolderOf, NwcBrowse, NwcChips);
            _nwf = Field(NwfFolder, NwfFrom, NwfLink, x => x.NwfFolder, ExportNodes.NwfFolderOf, NwfBrowse);
            _report = Field(ReportFolder, ReportFrom, ReportLink, x => x.ReportFolder, ExportNodes.ReportFolderOf, ReportBrowse, ReportChips);
            // вид — нигде не задан — «по общим настройкам» (разработчик, 02.11.2026), и у задачи верхнего уровня тоже
            var viewOwner = _parent == null ? null : ExportNodes.OwnerOf(_parent, x => x.ViewName);
            _view = new InheritField(ViewName, ViewFrom, ViewLink, null, s.ViewName,
                _parent == null ? common.Nwc.ViewName : ExportNodes.ViewOf(_parent, common),
                viewOwner == null ? "по общим настройкам" : "от «" + viewOwner.Name + "»", Changed);
            _hasView = common.Nwc.Scope == NwcScope.View;
            foreach (var e in new FrameworkElement[] { ViewCaption, ViewRow })
                e.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;

            // новая строка и копия: сразу печатать название
            Loaded += (o, e) =>
            {
                TaskName.Focus();
                TaskName.SelectAll();
            };
            _ready = true;
            ShowModels();
            ShowTests();
            ShowFormats();
            _opened = State();
            Closing += (o, e) => e.Cancel = !MayClose(this, DialogResult == true || State() == _opened);
        }

        /// <summary>Закрыть окно настроек: сохранено или правок нет — да; иначе — спросить (разбор 02.11.2026: «Отмена» молча теряла правки).</summary>
        internal static bool MayClose(Window window, bool clean) =>
            clean || MessageBox.Show(window, "Закрыть без сохранения? Правки пропадут.", window.Title, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        /// <summary>Строка «своё или от уровня выше»; у задачи верхнего уровня — всегда своё, без ссылки.</summary>
        private InheritField Field(TextBox box, TextBlock from, Button link, Func<NodeSettings, string> field, Func<ExportTask, string> of,
            params FrameworkElement[] extras)
        {
            var top = _parent == null;
            var f = new InheritField(box, from, link, extras, top ? field(_task.Settings) ?? of(_task) : field(_task.Settings),
                top ? "" : of(_parent), Source(x => field(x)), Changed);
            if (top) from.Visibility = link.Visibility = Visibility.Collapsed;
            return f;
        }

        /// <summary>От кого настройка: «от «ArmanQala»»; выше нигде не задана — «по умолчанию».</summary>
        private string Source(Func<NodeSettings, object> field)
        {
            var owner = _parent == null ? null : ExportNodes.OwnerOf(_parent, field);
            return owner == null ? "по умолчанию" : "от «" + owner.Name + "»";
        }

        /// <summary>Всё, что правится в окне, — строкой: «Отмена» с правками спрашивает.</summary>
        private string State() => string.Join("|", TaskName.Text, _modelsOwn,
            string.Join(";", _models.Select(m => m.Path + ">" + m.Model.NwcName + ">" + m.Model.Block)), _testsOwn,
            string.Join(";", _tests.Select(r => r.Path)), _formatsOwn, Xml.IsChecked, Html.IsChecked,
            Own(_nwfName), Own(_folder), Own(_nwc), Own(_nwf), Own(_report), Own(_view), _imported.Count);

        private static string Own(InheritField f) => f.Value ?? "\u0001";

        private void Changed()
        {
            if (_ready) UpdateExamples();
        }

        private void Any_Changed(object sender, RoutedEventArgs e) => Changed();

        private static IEnumerable<ExportModel> Copies(IEnumerable<ExportModel> models) =>
            models.Select(m => new ExportModel { Path = m.Path, NwcName = m.NwcName, Block = m.Block });

        // ── модели: свои или от уровня выше ──

        private List<ExportModel> EffectiveModels() => _modelsOwn ? _models.Select(m => m.Model).ToList() : ExportNodes.ModelsOf(_parent);

        private void ShowModels()
        {
            var count = EffectiveModels().Count;
            ModelsInfo.Text = count + " " + ExportTask.ModelsWord(count);
            AddModels.Visibility = _modelsOwn ? Visibility.Visible : Visibility.Collapsed;
            ModelsFrom.Text = _modelsOwn ? "" : Source(x => x.Models);
            ModelsLink.Content = _modelsOwn ? "Вернуть " + Source(x => x.Models) : "Изменить";
            ModelsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
            Regroup();
        }

        // «Изменить» — свои, начиная с моделей уровня выше (лишние — убрать); «Вернуть» — снова от уровня выше
        private void ModelsLink_Click(object sender, RoutedEventArgs e)
        {
            _modelsOwn = !_modelsOwn;
            _models.Clear();
            if (_modelsOwn) _models.AddRange(Copies(ExportNodes.ModelsOf(_parent)).Select(m => new ModelRow(_common, m)));
            ShowModels();
        }

        /// <summary>Свои модели — по блокам (S1, S2, S10; без блока — в конце), в блоке — сначала с блоком в имени.</summary>
        private void Regroup()
        {
            var sorted = _models
                .OrderBy(m => m.Effective, Comparer<string>.Create((a, b) =>
                    a.Length == 0 ? (b.Length == 0 ? 0 : 1) : b.Length == 0 ? -1 : ExportNodes.NaturalCompare(a, b)))
                .ThenBy(m => m.Manual)
                .ThenBy(m => m.FileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            string last = null;
            foreach (var m in sorted)
            {
                m.First = last == null || !string.Equals(last, m.Effective, StringComparison.OrdinalIgnoreCase);
                last = m.Effective;
            }
            _models.Clear();
            _models.AddRange(sorted);
            ModelsList.ItemsSource = null;
            ModelsList.ItemsSource = _models;
            ModelsPanel.Visibility = _modelsOwn && _models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_modelsOwn) ModelsInfo.Text = _models.Count + " " + ExportTask.ModelsWord(_models.Count);
            UpdateExamples();
        }

        // вписали блок — модель переходит в свой блок, когда курсор ушёл из поля
        private void Block_LostFocus(object sender, RoutedEventArgs e) => Regroup();

        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked;
            try
            {
                // окно Revit «Открыть» из плагина Revit Server не показывает (проверка разработчика, 02.11.2026) — своё окно; с
                // папки, откуда добавляли в прошлый раз
                var picker = new ModelPickerWindow(RevitYear, _serverFolder ?? ExportNodes.StartServerFolder(_task)) { Owner = this };
                if (picker.ShowDialog() != true) return;
                picked = picker.Picked;
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
            var list = _models.Select(m => m.Model).ToList();
            var added = ExportNodes.AddModels(list, picked);
            foreach (var m in list.Skip(_models.Count)) _models.Add(new ModelRow(_common, m));
            Info.Text = added == picked.Count ? "" : "Уже есть: " + (picked.Count - added) + " " + ExportTask.ModelsWord(picked.Count - added) + ".";
            Regroup();
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            _models.Remove((ModelRow)((FrameworkElement)sender).DataContext);
            Regroup();
        }

        // ── файлы проверок: свои или от уровня выше ──

        private void FillTests(IEnumerable<string> files)
        {
            _tests.Clear();
            foreach (var f in files ?? new List<string>()) _tests.Add(new FileRow(f));
        }

        private void ShowTests()
        {
            TestsList.Opacity = _testsOwn ? 1 : 0.5;   // от уровня выше — бледным
            AddTests.Visibility = _testsOwn ? Visibility.Visible : Visibility.Collapsed;
            NoTests.Visibility = _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TestsFrom.Text = _testsOwn ? "" : Source(x => x.TestsFiles);
            TestsLink.Content = _testsOwn ? "Вернуть " + Source(x => x.TestsFiles) : "Изменить";
            TestsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TestsEditVisibility)));
        }

        // «Изменить» — свои, начиная с файлов уровня выше; «Вернуть» — снова от уровня выше
        private void TestsLink_Click(object sender, RoutedEventArgs e)
        {
            _testsOwn = !_testsOwn;
            FillTests(ExportNodes.TestsOf(_parent));
            ShowTests();
        }

        private void AddTests_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var f in dialog.FileNames)
                if (!_tests.Any(r => string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) _tests.Add(new FileRow(f));
            ShowTests();
        }

        private void DeleteTests_Click(object sender, RoutedEventArgs e)
        {
            _tests.Remove((FileRow)((FrameworkElement)sender).DataContext);
            ShowTests();
        }

        // ── форматы: свои или от уровня выше ──

        private void ShowFormats()
        {
            Xml.IsEnabled = Html.IsEnabled = _formatsOwn;
            FormatsFrom.Text = _formatsOwn ? "" : Source(x => x.Xml);
            FormatsLink.Content = _formatsOwn ? "Вернуть " + Source(x => x.Xml) : "Изменить";
            FormatsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void FormatsLink_Click(object sender, RoutedEventArgs e)
        {
            _formatsOwn = !_formatsOwn;
            if (!_formatsOwn)
            {
                Xml.IsChecked = ExportNodes.XmlOf(_parent);
                Html.IsChecked = ExportNodes.HtmlOf(_parent);
            }
            ShowFormats();
        }

        // ── примеры ──

        /// <summary>Примеры — по блокам моделей строки (как в окне сейчас); блоков нет — «S1».</summary>
        private void UpdateExamples()
        {
            var models = EffectiveModels();
            var blocks = ExportNodes.Blocks(models, _common).Where(b => b.Name.Length > 0).Select(b => b.Name).ToList();
            if (blocks.Count == 0) blocks.Add("S1");
            var root = Folder.Text.Trim();
            string Nwf(string block) => ExportNames.Fill(NwfName.Text.Trim(), ExportNodes.Labels(block, null, _common, DateTime.Today)) + ".nwf";
            NwfNameExample.Text = "Пример: " + (blocks.Count == 1 ? Nwf(blocks[0])
                : blocks.Count == 2 ? Nwf(blocks[0]) + ", " + Nwf(blocks[1])
                : Nwf(blocks[0]) + " … " + Nwf(blocks[blocks.Count - 1]));

            var labels = ExportNodes.Labels(blocks[0], null, _common, DateTime.Today);
            var nwf = ExportNodes.InProject(root, ExportNames.Fill(NwfFolder.Text, labels)) + "\\" + Nwf(blocks[0]);
            NwfExample.Text = "Пример: " + ExportNodes.Short(root, nwf);
            NwfExample.ToolTip = nwf;

            var model = models.FirstOrDefault() ?? new ExportModel { Path = SampleModel };
            var nwc = ExportNodes.InProject(root, ExportNames.Fill(NwcFolder.Text,
                ExportNodes.Labels(ExportNodes.BlockOf(model, _common), model, _common, DateTime.Today))) + "\\" + ExportNodes.NwcNameOf(model, _common) + ".nwc";
            NwcExample.Text = "Пример: " + ExportNodes.Short(root, nwc);
            NwcExample.ToolTip = nwc;

            var format = Html.IsChecked == true ? "HTML" : Xml.IsChecked == true ? "XML" : "";
            var report = ExportNodes.InProject(root, ExportNames.Fill(ReportFolder.Text, ExportNodes.Labels(blocks[0], null, _common, DateTime.Today, format)));
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

        // ── «Обзор» ──

        private void BrowseProject_Click(object sender, RoutedEventArgs e)
        {
            var path = FolderDialog.Pick(this, Folder.Text, "Папка проекта");
            if (path != null) Folder.Text = path;
        }

        /// <summary>Папка NWC / NWF / отчётов: внутри папки проекта — коротко («S1»; S1 на {Блок} — меткой), вне — полный путь.</summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)((Button)sender).Tag;
            var root = Folder.Text.Trim().TrimEnd('\\');
            var path = FolderDialog.Pick(this, root, "Папка внутри проекта");
            if (path == null) return;
            box.Text = root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length + 1)
                : path;
        }

        // ── экспорт и импорт ──

        /// <summary>Свои настройки, как сейчас в окне; от уровня выше — null.</summary>
        private NodeSettings Collect() => new NodeSettings
        {
            Models = _modelsOwn ? _models.Select(m => m.Model).ToList() : null,
            TestsFiles = _testsOwn ? _tests.Select(r => r.Path).ToList() : null,
            NwfName = _nwfName.Value,
            Folder = _folder.Value,
            NwcFolder = _nwc.Value,
            NwfFolder = _nwf.Value,
            ReportFolder = _report.Value,
            Xml = _formatsOwn ? Xml.IsChecked == true : (bool?)null,
            Html = _formatsOwn ? Html.IsChecked == true : (bool?)null,
            ViewName = _hasView ? _view.Value : _task.Settings.ViewName,
            ServerFolder = _serverFolder ?? _task.Settings.ServerFolder,
        };

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ExportFiles.Filter,
                FileName = string.Concat(TaskName.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var copy = _task.Copy();
                copy.Name = TaskName.Text.Trim();
                copy.Settings = Collect();
                foreach (var t in _imported) copy.Add(t.Copy());
                ExportFiles.Save(dialog.FileName, _parent == null ? ExportFiles.FromTop(copy) : ExportFiles.FromTask(copy));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        /// <summary>Задача или подзадача из файла — подзадачей сюда (с вложенными); добавится по «Сохранить».</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                if (file.Kind == ExportFile.Common)
                {
                    Error.Text = "В файле — общие настройки, а не задача: их открывают в шестерёнке.";
                    return;
                }
                var taken = _task.Children.Concat(_imported).Select(c => c.Name);
                _imported.Add(file.Kind == ExportFile.Project ? ExportFiles.ToTop(file.ProjectData, taken) : ExportFiles.ToTask(file.TaskData, taken));
                Error.Text = "";
                Info.Text = "Добавятся по «Сохранить»: " + string.Join(", ", _imported.Select(t => t.Name));
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт не прочитан", ex);
                Error.Text = ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message;
            }
        }

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            // проверки по всему дереву (имена NWC, NWF двух строк в один файл) — на уже применённых настройках; ошибка — вернуть как было
            var name = _task.Name;
            var settings = _task.Settings;
            _task.Name = TaskName.Text.Trim();
            _task.Settings = Collect();
            foreach (var t in _imported) _task.Add(t);
            var root = ExportNodes.Root(_task);
            error = root.Self().Select(t => ExportNodes.CheckNwcNames(t, _common)).FirstOrDefault(x => x != null)
                    ?? ExportNodes.CheckNwfTaken(root, _common);
            if (error != null)
            {
                foreach (var t in _imported) _task.Remove(t);
                _task.Name = name;
                _task.Settings = settings;
                Error.Text = error;
                return;
            }
            DialogResult = true;
        }

        /// <summary>Что не так в окне — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            var name = ExportNodes.CheckName(TaskName.Text, TakenNames);
            if (name != null) return name;
            if (_parent == null && Folder.Text.Trim().Length == 0) return "Нужна папка проекта — в ней NWC, NWF и отчёты.";
            if (_formatsOwn && Xml.IsChecked != true && Html.IsChecked != true) return "Нужен хотя бы один формат отчётов.";
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";
            return (_testsOwn ? ExportNodes.CheckTestsFiles(_tests.Select(r => r.Path).ToList(), TestNames) : null)
                   ?? (_nwfName.Own ? ExportNodes.CheckNwfName(NwfName.Text) : null)
                   ?? (_nwc.Own ? ExportNodes.CheckNwcFolder(NwcFolder.Text) : null)
                   ?? (_nwf.Own ? ExportNodes.CheckNwfFolder(NwfFolder.Text) : null)
                   ?? (_report.Own ? ExportNodes.CheckReportFolder(ReportFolder.Text) : null);
        }

        /// <summary>Названия проверок файла (.xml Clash Detective) — тем же чтением, что на сайте («Категории проверок»).</summary>
        internal static IEnumerable<string> TestNames(string file)
        {
            using (var stream = File.OpenRead(file))
                return TestCategories.NamesFromXml(stream);
        }
    }
}
