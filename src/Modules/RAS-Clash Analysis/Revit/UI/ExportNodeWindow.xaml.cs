using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Настройки строки «Задач выгрузки» (карандаш): название, модели, папки NWC и NWF, вид, проверки. Остальное — общее
    /// (шестерёнка). Папки и вид наследуются построчно: «как у «…»» и «Изменить» / «Вернуть как у «…»». Проверки — у задачи; у
    /// блока — какие из них делать. Правится копия: «Отмена» оставляет строку как была.
    /// </summary>
    public partial class ExportNodeWindow : Window
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

        /// <summary>Карточка проверки у задачи; примеры под строками — на модели-примере окна.</summary>
        public sealed class CheckRow : INotifyPropertyChanged
        {
            private readonly ExportNodeWindow _window;
            public CheckSet Check { get; }

            public CheckRow(ExportNodeWindow window, CheckSet check)
            {
                _window = window;
                Check = check;
            }

            public string Name
            {
                get => Check.Name;
                set
                {
                    Check.Name = value ?? "";
                    Notify();
                    Notify(nameof(NwfExample));
                }
            }

            public string TestsFile
            {
                get => Check.TestsFile;
                set
                {
                    Check.TestsFile = value ?? "";
                    Notify();
                }
            }

            public string NwfName
            {
                get => Check.NwfName;
                set
                {
                    Check.NwfName = value ?? "";
                    Notify();
                    Notify(nameof(NwfExample));
                }
            }

            public string ReportFolder
            {
                get => Check.ReportFolder;
                set
                {
                    Check.ReportFolder = value ?? "";
                    Notify();
                    Notify(nameof(ReportExample));
                }
            }

            public string NwfExample => "Пример: " + ExportNames.Fill(Check.EffectiveNwfName, _window.ExampleValues()) + ".nwf";

            public string ReportExample => string.IsNullOrWhiteSpace(Check.ReportFolder)
                ? "Папка не задана"
                : "Пример: " + ExportNames.Fill(Check.ReportFolder.Trim(), _window.ExampleValues());

            public void Refresh()
            {
                Notify(nameof(NwfExample));
                Notify(nameof(ReportExample));
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Проверка задачи у блока: делать или нет.</summary>
        public sealed class BlockCheckRow
        {
            public CheckSet Check { get; set; }
            public string Name => Check.Name;
            public bool On { get; set; }
        }

        /// <summary>
        /// Строка «своё или как выше»: своё — поле правится, под ним метки, «Вернуть …»; как выше — значение выше бледным,
        /// «Изменить». Source == null — наследовать не от кого (папки у задачи): поле всегда своё.
        /// </summary>
        private sealed class Field
        {
            private readonly TextBox _box;
            private readonly TextBlock _from;
            private readonly Button _link;
            private readonly FrameworkElement _labels;
            private readonly string _inherited;
            private readonly string _source;
            public bool Own { get; private set; }

            public Field(TextBox box, TextBlock from, Button link, FrameworkElement labels, string own, string inherited, string source,
                Action changed)
            {
                _box = box;
                _from = from;
                _link = link;
                _labels = labels;
                _inherited = inherited ?? "";
                _source = source;
                Own = own != null || source == null;
                box.Text = Own ? own ?? "" : _inherited;
                link.Click += (s, e) =>
                {
                    Own = !Own;
                    if (!Own) _box.Text = _inherited;
                    Show();
                    if (Own)
                    {
                        _box.Focus();
                        _box.SelectAll();
                    }
                    changed();
                };
                Show();
            }

            private void Show()
            {
                _box.IsEnabled = Own;
                _box.Opacity = Own ? 1 : 0.5;   // как выше — бледным
                if (_labels != null) _labels.Visibility = Own ? Visibility.Visible : Visibility.Collapsed;
                _link.Visibility = _source == null ? Visibility.Collapsed : Visibility.Visible;
                _from.Text = _source == null || Own ? "" : _source;
                _link.Content = Own ? "Вернуть " + _source : "Изменить";
            }

            /// <summary>Своё значение; null — как выше.</summary>
            public string Value => Own ? _box.Text.Trim() : null;
        }

        // пример под полями, пока у строки нет моделей — имя модели по регламенту
        private const string SampleModel = "0000_01_01_S6_KJ01_01_M3 еще текст.rvt";

        private readonly ExportTask _task;
        private readonly ExportSettings _common;
        private readonly ObservableCollection<ModelRow> _models = new ObservableCollection<ModelRow>();
        private readonly ObservableCollection<CheckRow> _checks = new ObservableCollection<CheckRow>();
        private readonly List<BlockCheckRow> _blockChecks = new List<BlockCheckRow>();
        private readonly bool _top;
        private readonly bool _canHaveModels;
        private readonly bool _hasFolders;
        private readonly bool _hasView;
        private readonly Field _nwcFolder;
        private readonly Field _nwfFolder;
        private readonly Field _view;
        private readonly bool _ready;

        /// <summary>Версия Revit — серверы Revit Server для «Добавить модели…» берутся из её настроек (RSN.ini).</summary>
        public int RevitYear { get; set; } = 2026;

        public ExportNodeWindow(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _common = common;
            _top = task.Parent == null;
            Title = "Настройки — " + task.Name;
            NodeName.Text = task.Name;
            // новая строка: сразу печатать название
            Loaded += (s, e) =>
            {
                NodeName.Focus();
                NodeName.SelectAll();
            };

            _canHaveModels = ExportNodes.CanHaveModels(task);
            foreach (var m in task.ModelFiles)
                _models.Add(new ModelRow(common, new ExportModel { Path = m.Path, NwcName = m.NwcName }));
            ModelsList.ItemsSource = _models;
            ModelsPanel.Visibility = _canHaveModels ? Visibility.Visible : Visibility.Collapsed;

            var (nwc, nwcFrom) = ExportNodes.Inherited(task, s => s.NwcFolder);
            _nwcFolder = new Field(NwcFolder, NwcFrom, NwcLink, NwcLabels, task.Own.NwcFolder, nwc, _top ? null : Like(nwcFrom), Changed);
            var (nwf, nwfFrom) = ExportNodes.Inherited(task, s => s.NwfFolder);
            _nwfFolder = new Field(NwfFolder, NwfFrom, NwfLink, NwfLabels, task.Own.NwfFolder, nwf, _top ? null : Like(nwfFrom), Changed);
            var (view, viewFrom) = ExportNodes.Inherited(task, s => s.ViewName);
            // разработчик, 02.11.2026: не «как в общих», а «по общим настройкам»
            _view = new Field(ViewName, ViewFrom, ViewLink, null, task.Own.ViewName, view ?? common.Nwc.ViewName,
                viewFrom != null ? Like(viewFrom) : "по общим настройкам", Changed);

            _hasFolders = ExportNodes.HasNwcFolder(task);
            foreach (var e in new FrameworkElement[] { NwcCaption, NwcFolder, NwcState, NwcUnder, NwfCaption, NwfFolder, NwfState, NwfUnder })
                e.Visibility = _hasFolders ? Visibility.Visible : Visibility.Collapsed;
            _hasView = common.Nwc.Scope == NwcScope.View;
            foreach (var e in new FrameworkElement[] { ViewCaption, ViewName, ViewState })
                e.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;

            if (_top)
            {
                foreach (var c in task.Own.Checks ?? new List<CheckSet>()) _checks.Add(new CheckRow(this, c.Clone()));
                ChecksList.ItemsSource = _checks;
                BlockChecksList.Visibility = Visibility.Collapsed;
            }
            else
            {
                ChecksPanel.Visibility = Visibility.Collapsed;
                foreach (var c in ExportNodes.ChecksOf(task))
                    _blockChecks.Add(new BlockCheckRow { Check = c, On = !task.SkippedChecks.Contains(c.Id) });
                BlockChecksList.ItemsSource = _blockChecks;
                var root = ExportNodes.Root(task);
                ChecksInfo.Text = _blockChecks.Count == 0
                    ? "у «" + root.Name + "» проверок нет — только выгрузка NWC"
                    : "заданы у «" + root.Name + "»";
            }

            _ready = true;
            UpdateAll();
        }

        /// <summary>«как у «ArmanQala»»; выше никто не задал — как у задачи (пусто).</summary>
        private string Like(ExportTask from) => "как у «" + (from ?? ExportNodes.Root(_task)).Name + "»";

        private void Changed()
        {
            if (_ready) UpdateAll();
        }

        private void Any_Changed(object sender, RoutedEventArgs e) => Changed();

        private void UpdateAll()
        {
            ModelsInfo.Text = ModelsText();
            ModelsHead.Visibility = _models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_top) ChecksInfo.Text = _checks.Count == 0 ? "нет — только выгрузка NWC" : "";
            UpdateExamples();
        }

        private string ModelsText()
        {
            if (_canHaveModels) return _models.Count > 0 ? _models.Count + " " + ExportTask.ModelsWord(_models.Count) : "";
            var above = ExportNodes.ModelsAbove(_task);
            if (above != null) return "у «" + above.Name + "»: " + above.Models + " " + ExportTask.ModelsWord(above.Models);
            return "у вложенных: " + string.Join(", ", ExportNodes.ModelsBelow(_task).Select(t => t.Name + " (" + t.Models + ")"));
        }

        /// <summary>Модель для примеров: своя, иначе строки выше или первой вложенной; нет — имя по регламенту.</summary>
        private ExportModel ExampleModel() =>
            _models.FirstOrDefault()?.Model
            ?? ExportNodes.ModelsAbove(_task)?.ModelFiles.FirstOrDefault()
            ?? ExportNodes.ModelsBelow(_task).FirstOrDefault()?.ModelFiles.FirstOrDefault()
            ?? new ExportModel { Path = SampleModel };

        internal Dictionary<string, string> ExampleValues() => ExportNames.LabelValues(ExampleModel().Path, _common, DateTime.Today);

        private void UpdateExamples()
        {
            var model = ExampleModel();
            var values = ExampleValues();
            var nwc = NwcFolder.Text.Trim();
            NwcExample.Text = nwc.Length == 0 ? "Папка не задана"
                : "Пример: " + ExportNames.Fill(nwc, values) + "\\" + ExportNodes.NwcNameOf(model, _common) + ".nwc";
            var nwf = NwfFolder.Text.Trim();
            NwfExample.Text = nwf.Length == 0 ? "Папка не задана" : "Пример: " + ExportNames.Fill(nwf, values);
            foreach (var c in _checks) c.Refresh();
        }

        /// <summary>Метка под строкой — в строку, в место курсора.</summary>
        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (!(button.Tag is TextBox box) || !box.IsEnabled) return;
            var label = (string)button.Content;
            var at = Math.Min(box.CaretIndex, box.Text.Length);
            box.Text = box.Text.Insert(at, label);
            box.Focus();
            box.CaretIndex = at + label.Length;
        }

        // ── модели ──

        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked;
            try
            {
                // окно Revit «Открыть» из плагина Revit Server не показывает (проверка разработчика, 02.11.2026) — своё окно
                var picker = new ModelPickerWindow(RevitYear) { Owner = this };
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

        // ── проверки задачи ──

        private void AddCheck_Click(object sender, RoutedEventArgs e)
        {
            _checks.Add(new CheckRow(this, new CheckSet()));
            UpdateAll();
        }

        private void DeleteCheck_Click(object sender, RoutedEventArgs e)
        {
            _checks.Remove((CheckRow)((FrameworkElement)sender).DataContext);
            UpdateAll();
        }

        private void BrowseTests_Click(object sender, RoutedEventArgs e)
        {
            var row = (CheckRow)((FrameworkElement)sender).DataContext;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проверки Navisworks (*.xml)|*.xml", FileName = row.TestsFile ?? "" };
            if (dialog.ShowDialog(this) == true) row.TestsFile = dialog.FileName;
        }

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _task.Name = NodeName.Text.Trim();
            if (_canHaveModels) _task.ModelFiles = _models.Select(m => m.Model).ToList();
            var own = _task.Own;
            if (_hasFolders)
            {
                own.NwcFolder = _nwcFolder.Value;
                own.NwfFolder = _nwfFolder.Value;
            }
            if (_hasView) own.ViewName = _view.Value;
            if (_top)
                own.Checks = _checks.Select(c => new CheckSet
                {
                    Id = c.Check.Id, Name = c.Check.Name.Trim(), TestsFile = c.Check.TestsFile.Trim(),
                    NwfName = c.Check.NwfName.Trim(), ReportFolder = c.Check.ReportFolder.Trim(),
                }).ToList();
            else
                _task.SkippedChecks = new HashSet<string>(_blockChecks.Where(c => !c.On).Select(c => c.Check.Id));
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (NodeName.Text.Trim().Length == 0) return "Нужно название.";
            if (_hasFolders && _nwcFolder.Own)
            {
                var nwc = ExportNodes.CheckNwcFolder(NwcFolder.Text);
                if (nwc != null) return nwc;
            }
            if (_hasFolders && _nwfFolder.Own)
            {
                var nwf = ExportNodes.CheckNwfFolder(NwfFolder.Text);
                if (nwf != null) return nwf;
            }
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";

            if (_top)
            {
                var checks = ExportNodes.CheckChecks(_checks.Select(c => c.Check).ToList());
                if (checks != null) return checks;
                var notXml = _checks.FirstOrDefault(c => c.Check.TestsFile.Trim().Length > 0 &&
                                                         !c.Check.TestsFile.Trim().EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (notXml != null) return "«" + notXml.Check.Name.Trim() + "»: файл проверок — .xml из Clash Detective.";
            }

            var names = ExportNodes.CheckNwcNames(_models.Select(m => m.Name));
            if (names != null) return names;
            // одна модель — один раз на проект: иначе выгрузится дважды
            var root = ExportNodes.Root(_task);
            foreach (var m in _models)
            {
                var other = ExportNodes.FindModel(root, m.Path, _task);
                if (other != null) return "Модель «" + System.IO.Path.GetFileName(m.Path) + "» уже есть в «" + other.Name + "».";
            }
            return null;
        }
    }
}
