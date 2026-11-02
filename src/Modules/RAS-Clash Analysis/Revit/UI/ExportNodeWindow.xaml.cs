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
    /// Настройки строки «Задач выгрузки» (карандаш): название, модели, папка NWC, папка отчётов, вид. Остальное — общее
    /// (шестерёнка). Папки и вид наследуются построчно: «как у «…»» и «Изменить» / «Вернуть как у «…»». Правится копия:
    /// «Отмена» оставляет строку как была.
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

        /// <summary>
        /// Строка «своё или как выше»: своё — поле правится, «Вернуть как у «…»»; как выше — значение выше бледным, «Изменить».
        /// Source == null — наследовать не от кого (папки у задачи): поле всегда своё.
        /// </summary>
        private sealed class Field
        {
            private readonly TextBox _box;
            private readonly TextBlock _from;
            private readonly Button _link;
            private readonly string _inherited;
            private readonly string _source;
            public bool Own { get; private set; }

            public Field(TextBox box, TextBlock from, Button link, string own, string inherited, string source, Action changed)
            {
                _box = box;
                _from = from;
                _link = link;
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
        private readonly bool _canHaveModels;
        private readonly bool _hasNwcFolder;
        private readonly bool _hasView;
        private readonly Field _nwcFolder;
        private readonly Field _reportFolder;
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

            var top = task.Parent == null;
            var (nwc, nwcFrom) = ExportNodes.Inherited(task, s => s.NwcFolder);
            _nwcFolder = new Field(NwcFolder, NwcFrom, NwcLink, task.Own.NwcFolder, nwc, top ? null : Like(nwcFrom), Changed);
            var (report, reportFrom) = ExportNodes.Inherited(task, s => s.ReportFolder);
            _reportFolder = new Field(ReportFolder, ReportFrom, ReportLink, task.Own.ReportFolder, report, top ? null : Like(reportFrom), Changed);
            var (view, viewFrom) = ExportNodes.Inherited(task, s => s.ViewName);
            _view = new Field(ViewName, ViewFrom, ViewLink, task.Own.ViewName, view ?? common.Nwc.ViewName,
                viewFrom != null ? Like(viewFrom) : "как в общих", Changed);

            _hasNwcFolder = ExportNodes.HasNwcFolder(task);
            foreach (var e in new FrameworkElement[] { NwcCaption, NwcFolder, NwcState, NwcExample })
                e.Visibility = _hasNwcFolder ? Visibility.Visible : Visibility.Collapsed;
            _hasView = common.Nwc.Scope == NwcScope.View;
            foreach (var e in new FrameworkElement[] { ViewCaption, ViewName, ViewState })
                e.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;

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

        private void UpdateExamples()
        {
            var model = ExampleModel();
            var values = ExportNames.LabelValues(model.Path, _common, DateTime.Today);
            var nwc = NwcFolder.Text.Trim();
            NwcExample.Text = nwc.Length == 0 ? "Папка не задана"
                : "Пример: " + ExportNames.Fill(nwc, values) + "\\" + ExportNodes.NwcNameOf(model, _common) + ".nwc";
            var report = ReportFolder.Text.Trim();
            ReportExample.Text = report.Length == 0 ? "Папка не задана" : "Пример: " + ExportNames.Fill(report, values);
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

        // ── сохранить ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            Error.Text = error ?? "";
            if (error != null) return;

            _task.Name = NodeName.Text.Trim();
            if (_canHaveModels) _task.ModelFiles = _models.Select(m => m.Model).ToList();
            var own = _task.Own;
            if (_hasNwcFolder) own.NwcFolder = _nwcFolder.Value;
            own.ReportFolder = _reportFolder.Value;
            if (_hasView) own.ViewName = _view.Value;
            DialogResult = true;
        }

        /// <summary>Что не так — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            if (NodeName.Text.Trim().Length == 0) return "Нужно название.";
            if (_hasNwcFolder && _nwcFolder.Own)
            {
                var nwc = ExportNodes.CheckNwcFolder(NwcFolder.Text);
                if (nwc != null) return nwc;
            }
            if (_reportFolder.Own)
            {
                var report = ExportNodes.CheckReportFolder(ReportFolder.Text);
                if (report != null) return report;
            }
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";

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
