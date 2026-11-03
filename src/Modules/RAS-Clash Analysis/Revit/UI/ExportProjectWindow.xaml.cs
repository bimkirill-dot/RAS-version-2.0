using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Проект «Задач выгрузки» (карандаш у проекта; набросок 03.11.2026): название, папка проекта, папка для NWC, вид, модели —
    /// один раз на проект, по блокам. Блок и отдел — из имени модели; блока в имени нет — вписывается руками. Внизу — экспорт
    /// проекта с моделями и задачами и импорт задач (добавятся по «Сохранить»). Правятся копии моделей: «Отмена» оставляет как было.
    /// </summary>
    public partial class ExportProjectWindow : Window
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

        // пример, пока в проекте нет моделей
        private const string SampleModel = "0000_01_01_S1_KJ01_01_M3.rvt";

        private readonly ExportTask _project;
        private readonly ExportSettings _common;
        private readonly List<ModelRow> _models;
        private readonly List<ExportTask> _imported = new List<ExportTask>();
        private readonly InheritField _view;
        private readonly bool _hasView;
        private readonly bool _ready;
        private readonly string _opened;
        private string _serverFolder;

        /// <summary>Названия других проектов — название не должно повторяться (две одинаковые строки в списке не различить).</summary>
        public IReadOnlyCollection<string> TakenNames { get; set; } = new string[0];

        /// <summary>Версия Revit — серверы Revit Server для «Добавить модели…» берутся из её настроек (RSN.ini).</summary>
        public int RevitYear { get; set; } = 2026;

        public ExportProjectWindow(ExportTask project, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _project = project;
            _common = common;
            var p = project.Project;
            Title = string.IsNullOrEmpty(project.Name) ? "Новый проект" : "Проект — " + project.Name;
            ProjectName.Text = project.Name;
            Folder.Text = p.Folder;
            NwcFolder.Text = p.NwcFolder;
            _models = project.ModelFiles.Select(m => new ModelRow(common, new ExportModel { Path = m.Path, NwcName = m.NwcName, Block = m.Block }))
                .ToList();
            // разработчик, 02.11.2026: не «как в общих», а «по общим настройкам»
            _view = new InheritField(ViewName, ViewFrom, ViewLink, null, p.ViewName, common.Nwc.ViewName, "по общим настройкам", Changed);
            _hasView = common.Nwc.Scope == NwcScope.View;
            foreach (var e in new FrameworkElement[] { ViewCaption, ViewRow })
                e.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;
            // новый проект: сразу печатать название
            Loaded += (s, e) =>
            {
                ProjectName.Focus();
                ProjectName.SelectAll();
            };
            _ready = true;
            Regroup();
            _opened = State();
            Closing += (s, e) => e.Cancel = !ExportTaskWindow.MayClose(this, DialogResult == true || State() == _opened);
        }

        /// <summary>Всё, что правится в окне, — строкой: «Отмена» с правками спрашивает.</summary>
        private string State() => string.Join("|", ProjectName.Text, Folder.Text, NwcFolder.Text, _view.Value ?? "\u0001",
            string.Join(";", _models.Select(m => m.Path + ">" + m.Model.NwcName + ">" + m.Model.Block)), _imported.Count);

        private void Changed()
        {
            if (_ready) UpdateExample();
        }

        private void Any_Changed(object sender, RoutedEventArgs e) => Changed();

        /// <summary>Модели — по блокам (S1, S2, S10; без блока — в конце), в блоке — сначала с блоком в имени.</summary>
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
            ModelsHead.Text = _models.Count > 0 ? "Модели (" + _models.Count + ")" : "Модели";
            ModelsCols.Visibility = _models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoModels.Visibility = _models.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateExample();
        }

        // вписали блок — модель переходит в свой блок, когда курсор ушёл из поля
        private void Block_LostFocus(object sender, RoutedEventArgs e) => Regroup();

        /// <summary>Проект, как он сейчас в окне, — для примера и проверки имён NWC.</summary>
        private ExportTask Draft()
        {
            var draft = new ExportTask { Name = ProjectName.Text.Trim(), ModelFiles = _models.Select(m => m.Model).ToList() };
            draft.Project.Folder = Folder.Text.Trim();
            draft.Project.NwcFolder = NwcFolder.Text.Trim();
            return draft;
        }

        /// <summary>Пример под «Папкой для NWC» — на первой модели проекта; нет — модель по регламенту.</summary>
        private void UpdateExample()
        {
            var draft = Draft();
            var model = draft.ModelFiles.FirstOrDefault() ?? new ExportModel { Path = SampleModel };
            var path = ExportNodes.NwcPath(draft, model, _common);
            NwcExample.Text = draft.Project.Folder.Length == 0 && NwcFolder.Text.Trim().Length == 0
                ? "Папка не задана"
                : "Пример: " + ExportNodes.Short(draft.Project.Folder, path);
        }

        /// <summary>Метка под строкой — в строку, в место курсора.</summary>
        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            var label = (string)((Button)sender).Content;
            var at = Math.Min(NwcFolder.CaretIndex, NwcFolder.Text.Length);
            NwcFolder.Text = NwcFolder.Text.Insert(at, label);
            NwcFolder.Focus();
            NwcFolder.CaretIndex = at + label.Length;
        }

        // ── «Обзор» ──

        private void BrowseProject_Click(object sender, RoutedEventArgs e)
        {
            var path = FolderDialog.Pick(this, Folder.Text, "Папка проекта");
            if (path != null) Folder.Text = path;
        }

        /// <summary>Папка NWC: внутри папки проекта — коротко («S1\NWC»; S1 на {Блок} — меткой), вне — полный путь.</summary>
        private void BrowseNwc_Click(object sender, RoutedEventArgs e)
        {
            var root = Folder.Text.Trim().TrimEnd('\\');
            var path = FolderDialog.Pick(this, root, "Папка для NWC");
            if (path == null) return;
            NwcFolder.Text = root.Length > 0 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length + 1)
                : path;
        }

        // ── модели ──

        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked;
            try
            {
                // окно Revit «Открыть» из плагина Revit Server не показывает (проверка разработчика, 02.11.2026) — своё окно; с
                // папки проекта на сервере
                var picker = new ModelPickerWindow(RevitYear, _serverFolder ?? _project.Project.ServerFolder) { Owner = this };
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
            var draft = new ExportTask { ModelFiles = _models.Select(m => m.Model).ToList() };
            var added = ExportNodes.AddModels(draft, picked);
            foreach (var m in draft.ModelFiles.Skip(_models.Count).ToList()) _models.Add(new ModelRow(_common, m));
            Info.Text = added == picked.Count ? "" : "Уже в проекте: " + (picked.Count - added) + " " + ExportTask.ModelsWord(picked.Count - added) + ".";
            Regroup();
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            _models.Remove((ModelRow)((FrameworkElement)sender).DataContext);
            Regroup();
        }

        // ── экспорт и импорт ──

        private ProjectSettings Collect() => new ProjectSettings
        {
            Folder = Folder.Text.Trim(),
            ServerFolder = _serverFolder ?? _project.Project.ServerFolder,
            NwcFolder = NwcFolder.Text.Trim(),
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
                ExportFiles.Save(dialog.FileName, ExportFiles.FromProject(ProjectName.Text.Trim(), Collect(), _models.Select(m => m.Model),
                    _project.Children.Concat(_imported)));
                Info.Text = "Сохранено: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: экспорт проекта не записан", ex);
                Error.Text = "Файл не записан: " + ex.Message;
            }
        }

        /// <summary>Задача или все задачи проекта из файла — в этот проект (настройки задач, без моделей); добавятся по «Сохранить».</summary>
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
            p.ServerFolder = s.ServerFolder;
            p.NwcFolder = s.NwcFolder;
            p.ViewName = s.ViewName;
            _project.ModelFiles = _models.Select(m => m.Model).ToList();
            foreach (var t in _imported) _project.Add(t);
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            var name = ExportNodes.CheckProjectName(ProjectName.Text, TakenNames);
            if (name != null) return name;
            if (Folder.Text.Trim().Length == 0) return "Нужна папка проекта — в ней NWC, NWF и отчёты.";
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";
            return ExportNodes.CheckNwcFolder(NwcFolder.Text) ?? ExportNodes.CheckNwcNames(Draft(), _common);
        }

        /// <summary>Названия проверок файла (.xml Clash Detective) — тем же чтением, что на сайте («Категории проверок»).</summary>
        internal static IEnumerable<string> TestNames(string file)
        {
            using (var stream = File.OpenRead(file))
                return TestCategories.NamesFromXml(stream);
        }
    }
}
