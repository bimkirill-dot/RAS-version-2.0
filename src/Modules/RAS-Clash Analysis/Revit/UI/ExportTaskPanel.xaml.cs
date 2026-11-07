using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RASv2.ClashAnalysis.Export;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Настройки задачи или подзадачи «Задач выгрузки» — боковая панель главного окна (разработчик, 03.11.2026; разделы — по
    /// разработчику): «Общее» — название, папка задачи; «Экспорт» — вид, папка для NWC, модели, «Настройки NWC Exporter»; «NWF» —
    /// «Формировать NWF», имя, проверки, скрыть, папка; «Отчёты» — «Формировать отчёты», формат, папка. Каждая настройка — своя или
    /// от уровня выше; у задачи верхнего уровня всё своё. Правятся копии: «Сохранить» применяет к строке (Saved — главное окно пишет
    /// на сервер), «Отменить» — Cancelled.
    /// </summary>
    public partial class ExportTaskPanel : UserControl, INotifyPropertyChanged
    {
        /// <summary>Строка списка моделей: имя (без «.rvt») и путь (разработчик, 03.11.2026: «отдел, блок — не надо»).</summary>
        public sealed class ModelRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            private readonly ExportSettings _common;
            private DateTime? _modified;
            private bool _grey;

            /// <summary>Когда изменена модель (Revit Server — сведения о модели, файл — дата файла); не известна — null.</summary>
            public DateTime? Modified
            {
                get => _modified;
                set
                {
                    _modified = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateText)));
                }
            }

            /// <summary>Модель не изменялась в выбранный срок — NWC актуален, строка серая.</summary>
            public bool Grey
            {
                get => _grey;
                set
                {
                    if (_grey == value) return;
                    _grey = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowOpacity)));
                }
            }

            public double RowOpacity => _grey ? 0.4 : 1;
            public string DateText => _modified == null ? "" : "  ·  изм. " + _modified.Value.ToString("dd.MM.yyyy HH:mm");
            public ExportModel Model { get; }

            public ModelRow(ExportSettings common, ExportModel model)
            {
                _common = common;
                Model = model;
            }

            /// <summary>Какой получится NWC: «0000_01_01_S1_KJ01_01_M3.nwc».</summary>
            public string Nwc => "NWC: " + ExportNodes.NwcNameOf(Model, _common) + ".nwc";

            public string Path => Model.Path;
            public string Folder => new FileRow(Path).Folder;

            public string DisplayName
            {
                get
                {
                    var n = new FileRow(Path).Name;
                    return n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                }
            }
        }

        /// <summary>Формат отчёта в выпадающем списке: название, как в Navisworks, и галочка.</summary>
        public sealed class FormatItem
        {
            public string Name { get; set; }
            public bool On { get; set; }
        }

        private readonly ExportTask _task;
        private readonly ExportTask _parent;
        private readonly ExportSettings _common;
        private readonly List<ModelRow> _models = new List<ModelRow>();
        private readonly ObservableCollection<FileRow> _tests = new ObservableCollection<FileRow>();
        private readonly ObservableCollection<HideSet> _hide = new ObservableCollection<HideSet>();
        private readonly List<ExportTask> _imported = new List<ExportTask>();
        private readonly InheritField _folder, _nwc, _nwf, _report, _view;
        private readonly bool _hasView;
        private readonly bool _ready;
        private readonly string _opened;
        private bool _modelsOwn, _testsOwn, _hideOwn, _formatsOwn, _editedOwn, _showModels;
        // строки списка моделей, как сейчас показаны (для дат и серых)
        private List<ModelRow> _shownRows = new List<ModelRow>();
        // даты изменения моделей — на время работы Revit (по пути)
        private static readonly Dictionary<string, DateTime> KnownDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FormatItem> _formats = ExportNodes.ReportFormatNames.Select(n => new FormatItem { Name = n }).ToList();
        // свои параметры выгрузки (null — от уровня выше или общие); _exportEdits — для «Отмена с правками спрашивает»
        private ExportOverride _export;
        private int _exportEdits;
        private string _serverFolder;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Корзина и правка имени NWC у моделей — только у своего списка.</summary>
        public Visibility ModelsEditVisibility => _modelsOwn ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Названия соседей по уровню — название не должно повторяться (две одинаковые строки не различить).</summary>
        public IReadOnlyCollection<string> TakenNames { get; set; } = new string[0];

        /// <summary>Версия Revit — серверы Revit Server для «Добавить модели…» берутся из её настроек (RSN.ini).</summary>
        public int RevitYear { get; set; } = 2026;

        public ExportTaskPanel(ExportTask task, ExportSettings common)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _task = task;
            _parent = task.Parent;
            _common = common;
            var s = task.Settings;
            TaskName.Text = task.Name;
            DataContext = this;

            _modelsOwn = _parent == null || s.Models != null;
            if (_modelsOwn) _models.AddRange(Copies(s.Models ?? ExportNodes.ModelsOf(task)).Select(m => new ModelRow(common, m)));
            _testsOwn = _parent == null || s.TestsFiles != null;
            TestsList.ItemsSource = _tests;
            FillTests(_testsOwn ? s.TestsFiles ?? ExportNodes.TestsOf(task) : ExportNodes.TestsOf(_parent));
            _hideOwn = _parent == null || s.HideSets != null;
            HideList.ItemsSource = _hide;
            FillHide(_hideOwn ? s.HideSets ?? ExportNodes.HideSetsOf(task) : ExportNodes.HideSetsOf(_parent));
            _editedOwn = _parent == null || s.Edited != null;
            FillEdited(_editedOwn ? s.Edited : ExportNodes.EditedOf(_parent));
            _formatsOwn = _parent == null || s.ReportFormats != null;
            FillFormats(_formatsOwn ? s.ReportFormats ?? ExportNodes.FormatsOf(task) : ExportNodes.FormatsOf(_parent));
            Formats.ItemsSource = _formats;
            _export = s.Export?.Clone();

            // «Формировать NWF» и имя NWF — свои у строки; у строки с подзадачами галочка недоступна
            MakeNwf.IsChecked = s.MakeNwf && task.Children.Count == 0;
            MakeNwf.IsEnabled = task.Children.Count == 0;
            MakeReports.IsChecked = s.MakeReports && task.Children.Count == 0;
            NwfName.Text = s.NwfName ?? "";
            _folder = Field(Folder, FolderFrom, FolderLink, x => x.Folder, ExportNodes.FolderOf, FolderBrowse);
            _nwc = Field(NwcFolder, NwcFrom, NwcLink, x => x.NwcFolder, ExportNodes.NwcFolderOf, NwcBrowse, NwcChips);
            _nwf = Field(NwfFolder, NwfFrom, NwfLink, x => x.NwfFolder, ExportNodes.NwfFolderOf, NwfBrowse);
            _report = Field(ReportFolder, ReportFrom, ReportLink, x => x.ReportFolder, ExportNodes.ReportFolderOf, ReportBrowse, ReportChips);
            // вид — нигде не задан — «по общим настройкам» (разработчик, 02.11.2026), и у задачи верхнего уровня тоже
            var viewOwner = _parent == null ? null : ExportNodes.OwnerOf(_parent, x => x.ViewName);
            _view = new InheritField(ViewName, ViewFrom, ViewLink, null, s.ViewName,
                _parent == null ? common.Nwc.ViewName : ExportNodes.ViewOf(_parent, common),
                viewOwner == null ? "По общим настройкам" : "от «" + viewOwner.Name + "»", Changed);
            _hasView = common.Nwc.Scope == NwcScope.View;
            ViewRow.Visibility = _hasView ? Visibility.Visible : Visibility.Collapsed;

            // где строка в дереве: «в «Высокая критичность» · ArmanQala»
            PathInfo.Text = _parent == null ? "задача" : "в " + string.Join(" · ", Ancestors().Select((a, i) => i == 0 ? "«" + a.Name + "»" : a.Name));

            // новая строка и копия: сразу печатать название
            if (string.IsNullOrEmpty(task.Name))
                Loaded += (o, e) =>
                {
                    TaskName.Focus();
                    TaskName.SelectAll();
                };
            _ready = true;
            ShowModels();
            ShowTests();
            ShowHide();
            ShowFormats();
            ShowEdited();
            ShowExport();
            _opened = State();
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
            string.Join(";", _tests.Select(r => r.Path)), _hideOwn, string.Join(";", _hide.Select(h => h.Xml)), _formatsOwn, string.Join(";", Checked()), _export == null, _exportEdits, _editedOwn, EditedOn.IsChecked, EditedMode.SelectedIndex,
            EditedDate1.Text, EditedDate2.Text,
            MakeNwf.IsChecked, MakeReports.IsChecked, NwfName.Text, Own(_folder), Own(_nwc), Own(_nwf), Own(_report), Own(_view), _imported.Count);

        private static string Own(InheritField f) => f.Value ?? "\u0001";

        private void Changed()
        {
            if (_ready) UpdateExamples();
        }

        private void Any_Changed(object sender, RoutedEventArgs e)
        {
            Changed();
            if (_ready) ShowEdited();
        }

        private static IEnumerable<ExportModel> Copies(IEnumerable<ExportModel> models) =>
            models.Select(m => m.Clone());

        // ── модели: свои или от уровня выше ──

        private List<ExportModel> EffectiveModels() => _modelsOwn ? _models.Select(m => m.Model).ToList() : ExportNodes.ModelsOf(_parent);

        private IEnumerable<ExportTask> Ancestors()
        {
            for (var a = _parent; a != null; a = a.Parent) yield return a;
        }

        private void ShowModels()
        {
            AddModels.Visibility = _modelsOwn ? Visibility.Visible : Visibility.Collapsed;
            ModelsFrom.Text = _modelsOwn ? "" : Source(x => x.Models);
            ModelsLink.Content = _modelsOwn ? "Вернуть " + Source(x => x.Models) : "Изменить";
            ModelsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModelsEditVisibility)));
            Regroup();
        }


        private void ModelsShow_Click(object sender, RoutedEventArgs e)
        {
            _showModels = !_showModels;
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

        /// <summary>Модели — по имени (разработчик, 03.11.2026: «только имя и путь»); от уровня выше — только смотреть.</summary>
        private void Regroup()
        {
            var rows = _modelsOwn ? _models : Copies(ExportNodes.ModelsOf(_parent)).Select(m => new ModelRow(_common, m)).ToList();
            var sorted = rows.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_modelsOwn)
            {
                _models.Clear();
                _models.AddRange(sorted);
            }
            ModelsList.ItemsSource = null;
            ModelsList.ItemsSource = sorted;
            UpdateRemove();
            _shownRows = sorted;
            LoadModelDates(sorted);
            ModelsSummary.Text = sorted.Count + " " + ExportTask.ModelsWord(sorted.Count);
            ModelsShow.Visibility = sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelsShow.Content = _showModels ? "Скрыть" : "Показать";
            ModelsPanel.Visibility = _showModels && sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateExamples();
        }


        private void AddModels_Click(object sender, RoutedEventArgs e)
        {
            List<ExportModel> picked;
            try
            {
                // окно Revit «Открыть» из плагина Revit Server не показывает (проверка разработчика, 02.11.2026) — своё окно; с
                // папки, откуда добавляли в прошлый раз
                var picker = new ModelPickerWindow(RevitYear, _serverFolder ?? ExportNodes.StartServerFolder(_task)) { Owner = Owner };
                if (picker.ShowDialog() != true) return;
                picked = picker.Picked;
                var server = picked.Select(m => ExportNodes.ServerFolderOf(m.Path)).FirstOrDefault(f => f.Length > 0);
                if (server != null) _serverFolder = server;
            }
            catch (Exception ex)
            {
                // исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»)
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: выбор моделей не открылся", ex);
                ShowError("Окно выбора моделей не открылось — подробности в журнале плагина.");
                return;
            }
            var list = _models.Select(m => m.Model).ToList();
            var added = ExportNodes.AddModels(list, picked);
            foreach (var m in list.Skip(_models.Count)) _models.Add(new ModelRow(_common, m));
            Info.Text = added == picked.Count ? "" : "Уже есть: " + (picked.Count - added) + " " + ExportTask.ModelsWord(picked.Count - added) + ".";
            _showModels = true;
            Regroup();
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            _models.Remove((ModelRow)((FrameworkElement)sender).DataContext);
            Regroup();
        }

        // ── пакетное удаление (разработчик, 05.11.2026): выделить (Ctrl, Shift, Ctrl+A) — «Убрать выбранные (N)» или Delete ──

        private void UpdateRemove()
        {
            var n = _modelsOwn ? ModelsList.SelectedItems.Count : 0;
            ModelsRemove.Content = "Убрать выбранные (" + n + ")";
            ModelsRemove.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemove();

        private void ModelsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || !_modelsOwn || ModelsList.SelectedItems.Count == 0) return;
            e.Handled = true;
            RemoveSelected_Click(sender, e);
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_modelsOwn) return;
            var selected = new HashSet<ModelRow>(ModelsList.SelectedItems.Cast<ModelRow>());
            if (selected.Count == 0) return;
            _models.RemoveAll(selected.Contains);
            Info.Text = "Убрано: " + selected.Count + " " + ExportTask.ModelsWord(selected.Count) + ".";
            Regroup();
        }

        // ── файлы проверок: свои или от уровня выше ──

        private void FillTests(IEnumerable<string> files)
        {
            _tests.Clear();
            foreach (var f in files ?? new List<string>()) _tests.Add(new FileRow(f));
        }

        // от уровня выше — именами в строку; свои — списком с корзинами
        private void ShowTests()
        {
            TestsPanel.Visibility = _testsOwn ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)TestsInline.Parent).Visibility = !_testsOwn || _tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TestsInline.Text = _tests.Count == 0 ? "файлов нет" : string.Join(", ", _tests.Select(r => r.Name));
            TestsInline.Foreground = (System.Windows.Media.Brush)FindResource(_tests.Count == 0 ? "Muted" : "Fg");
            TestsFrom.Text = _testsOwn ? "" : Source(x => x.TestsFiles);
            TestsLink.Content = _testsOwn ? "Вернуть " + Source(x => x.TestsFiles) : "Изменить";
            TestsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
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
            if (dialog.ShowDialog(Owner) != true) return;
            foreach (var f in dialog.FileNames)
                if (!_tests.Any(r => string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) _tests.Add(new FileRow(f));
            ShowTests();
        }

        private void DeleteTests_Click(object sender, RoutedEventArgs e)
        {
            _tests.Remove((FileRow)((FrameworkElement)sender).DataContext);
            ShowTests();
        }

        // ── скрыть: поисковые наборы Navisworks; свои или от уровня выше ──

        private void FillHide(IEnumerable<HideSet> sets)
        {
            _hide.Clear();
            foreach (var s in sets ?? new List<HideSet>()) _hide.Add(s.Clone());
        }

        // от уровня выше — названиями через «;»; свои — наборами с условиями и корзинами
        private void ShowHide()
        {
            HidePanel.Visibility = _hideOwn ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)HideInline.Parent).Visibility = !_hideOwn || _hide.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HideInline.Text = _hide.Count == 0 ? "ничего" : string.Join("; ", _hide.Select(h => h.Name));
            HideInline.ToolTip = _hide.Count == 0 ? null : string.Join("\n", _hide.Select(h => h.Name + ": " + h.Text));
            HideInline.Foreground = (System.Windows.Media.Brush)FindResource(_hide.Count == 0 ? "Muted" : "Fg");
            HideFrom.Text = _hideOwn ? "" : Source(x => x.HideSets);
            HideLink.Content = _hideOwn ? "Вернуть " + Source(x => x.HideSets) : "Изменить";
            HideLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // «Изменить» — свои, начиная с правил уровня выше; «Вернуть» — снова от уровня выше
        private void HideLink_Click(object sender, RoutedEventArgs e)
        {
            _hideOwn = !_hideOwn;
            FillHide(ExportNodes.HideSetsOf(_parent));
            ShowHide();
        }

        /// <summary>
        /// Правила — из файла поисковых наборов Navisworks («Наборы» → «Экспорт поисковых наборов»): все поисковые наборы файла;
        /// такие же уже есть — мимо; простые наборы выбора (список элементов) не годятся — пропускаются с сообщением.
        /// </summary>
        private void AddHide_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Поисковые наборы Navisworks (*.xml)|*.xml" };
            if (dialog.ShowDialog(Owner) != true) return;
            try
            {
                List<HideSet> sets;
                int skipped;
                using (var stream = File.OpenRead(dialog.FileName))
                    sets = SearchSets.Read(stream, out skipped);
                var added = 0;
                foreach (var s in sets)
                {
                    if (_hide.Any(h => h.Xml == s.Xml)) continue;
                    _hide.Add(s);
                    added++;
                }
                ShowError("");
                Info.Text = "Добавлено правил: " + added + (sets.Count > added ? ", уже были: " + (sets.Count - added) : "") +
                            (skipped > 0 ? "; простые наборы выбора не годятся — пропущено: " + skipped : "") + ".";
                ShowHide();
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: поисковые наборы не прочитаны", ex);
                ShowError(ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message);
            }
        }

        private void DeleteHide_Click(object sender, RoutedEventArgs e)
        {
            _hide.Remove((HideSet)((FrameworkElement)sender).DataContext);
            ShowHide();
        }

        // ── форматы: выпадающий список с галочками; свои или от уровня выше ──

        private void FillFormats(IEnumerable<string> names)
        {
            var set = new HashSet<string>(names ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var f in _formats) f.On = set.Contains(f.Name);
            Formats.Items.Refresh();
        }

        private List<string> Checked() => _formats.Where(f => f.On).Select(f => f.Name).ToList();

        private void ShowFormats()
        {
            Formats.IsEnabled = _formatsOwn;
            var on = Checked();
            Formats.Text = on.Count == 0 ? "не выбраны" : string.Join(", ", on);
            FormatsFrom.Text = _formatsOwn ? "" : Source(x => x.ReportFormats);
            FormatsLink.Content = _formatsOwn ? "Вернуть " + Source(x => x.ReportFormats) : "Изменить";
            FormatsLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
            if (_ready) UpdateExamples();
        }

        private void Format_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) ShowFormats();
        }

        // выбор строки списка подставил бы её название в поле — вместо него список отмеченных
        private void Formats_Closed(object sender, EventArgs e)
        {
            Formats.SelectedItem = null;
            ShowFormats();
        }

        private void FormatsLink_Click(object sender, RoutedEventArgs e)
        {
            _formatsOwn = !_formatsOwn;
            if (!_formatsOwn) FillFormats(ExportNodes.FormatsOf(_parent));
            ShowFormats();
        }

        // ── отбор моделей по дате изменения: свой или от уровня выше ──

        private static readonly string[] EditedModes = { ModelDateFilter.After, ModelDateFilter.Before, ModelDateFilter.Between };

        private void FillEdited(ModelDateFilter f)
        {
            f = f ?? new ModelDateFilter();
            EditedOn.IsChecked = f.On;
            EditedMode.SelectedIndex = Math.Max(0, Array.IndexOf(EditedModes, f.Mode));
            EditedDate1.Text = f.From?.ToString("dd.MM.yyyy") ?? "";
            EditedDate2.Text = f.To?.ToString("dd.MM.yyyy") ?? "";
        }

        private ModelDateFilter CollectEdited() => new ModelDateFilter
        {
            On = EditedOn.IsChecked == true, Mode = EditedModes[Math.Max(0, EditedMode.SelectedIndex)],
            From = DateOf(EditedDate1), To = EditedMode.SelectedIndex == 2 ? DateOf(EditedDate2) : null,
        };

        private void ShowEdited()
        {
            EditedOn.IsEnabled = _editedOwn;
            var on = _editedOwn && EditedOn.IsChecked == true;
            EditedMode.IsEnabled = EditedDate1.IsEnabled = EditedDate2.IsEnabled = EditedPick1.IsEnabled = EditedPick2.IsEnabled = on;
            var period = EditedMode.SelectedIndex == 2;
            EditedDash.Visibility = EditedDate2Box.Visibility = period ? Visibility.Visible : Visibility.Collapsed;
            EditedFromText.Text = _editedOwn ? "" : Source(x => x.Edited);
            EditedLink.Content = _editedOwn ? "Вернуть " + Source(x => x.Edited) : "Изменить";
            EditedLink.Visibility = _parent == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateGrey();
        }

        /// <summary>Дата из поля «дд.мм.гггг»; не дата — null.</summary>
        private static DateTime? DateOf(TextBox box) =>
            DateTime.TryParseExact(box.Text.Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null;

        /// <summary>Календарь под значком: выбрали день — он в поле.</summary>
        private void PickDate_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var box = (TextBox)button.Tag;
            // шрифт — плагина: иначе календарь берёт шрифт значка-кнопки (Segoe MDL2 Assets), а в нём нет цифр и букв — квадратики
            var calendar = new System.Windows.Controls.Calendar
            {
                SelectedDate = DateOf(box), DisplayDate = DateOf(box) ?? DateTime.Today,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
            };
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
                Child = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("Bg"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4), Child = calendar,
                },
            };
            calendar.SelectedDatesChanged += (s, a) =>
            {
                if (calendar.SelectedDate != null) box.Text = calendar.SelectedDate.Value.ToString("dd.MM.yyyy");
                popup.IsOpen = false;
            };
            popup.IsOpen = true;
        }

        private void EditedLink_Click(object sender, RoutedEventArgs e)
        {
            _editedOwn = !_editedOwn;
            if (!_editedOwn) FillEdited(ExportNodes.EditedOf(_parent));
            ShowEdited();
        }

        /// <summary>Строки моделей, не изменявшихся в выбранный срок, — серые (NWC актуален).</summary>
        private void UpdateGrey()
        {
            var f = _editedOwn ? CollectEdited() : ExportNodes.EditedOf(_parent);
            foreach (var r in _shownRows) r.Grey = f != null && !f.Passes(r.Modified);
        }

        /// <summary>
        /// Даты изменения моделей — в фоне: Revit Server — из кэша окна выбора или сведений о модели (по версии модели; не известна
        /// — версия этого Revit), файл — дата файла. Пришло — строка серая или нет по отбору.
        /// </summary>
        private async void LoadModelDates(List<ModelRow> rows)
        {
            try
            {
                // продолжение после ответа сервера — в потоке панели: без контекста WPF оно ушло бы в чужой поток и тронуло
                // строки оттуда (вне Revit — переполнение стека, в Revit упал бы Revit; так же — окно выбора моделей)
                if (!(System.Threading.SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext))
                    System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
                foreach (var r in rows)
                {
                    if (KnownDates.TryGetValue(r.Path, out var known))
                    {
                        r.Modified = known;
                        continue;
                    }
                    DateTime? date = null;
                    if (ExportNodes.IsServerPath(r.Path))
                    {
                        var parts = r.Path.Trim().Substring("RSN://".Length).Split('/');
                        var host = parts[0];
                        var folder = parts.Skip(1).Take(parts.Length - 2).ToList();
                        var name = parts[parts.Length - 1];
                        var year = r.Model.Year > 0 ? r.Model.Year : RevitYear;
                        if (ServerCache.Of(host).TryGet(year, folder, out var contents, out _))
                            date = contents.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))?.Modified;
                        if (date == null)
                        {
                            try
                            {
                                date = (await RevitServerClient.ModelInfoAsync(host, year, folder, name)).DateModified;
                            }
                            catch (Exception)
                            {
                                // сервер не ответил — даты нет, строка не серая
                            }
                        }
                    }
                    else
                    {
                        var path = r.Path;
                        date = await System.Threading.Tasks.Task.Run(() => File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null);
                    }
                    if (date == null) continue;
                    KnownDates[r.Path] = date.Value;
                    r.Modified = date;
                }
                UpdateGrey();
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: даты изменения моделей", ex);
            }
        }

        // ── параметры выгрузки: по общим настройкам или свои (то же окно, что шестерёнка) ──

        private void ShowExport()
        {
            var owner = _parent == null ? null : ExportNodes.OwnerOf(_parent, x => x.Export);
            var inherited = owner == null ? "По общим настройкам" : "от «" + owner.Name + "»";
            ExportFrom.Text = _export != null ? "Свои" : inherited;
            ExportReset.Content = "Вернуть " + (owner == null ? "по общим настройкам" : inherited);
            ExportReset.Visibility = _export != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ExportEdit_Click(object sender, RoutedEventArgs e)
        {
            // начать с того, что действует сейчас: своих, уровня выше или общих
            var current = _export != null ? _export.ApplyTo(_common) : (_parent == null ? _common : ExportNodes.ExportOf(_parent, _common)).Clone();
            try
            {
                var name = TaskName.Text.Trim().Length > 0 ? TaskName.Text.Trim() : "новая строка";
                if (new ExportSettingsWindow(current, name) { Owner = Owner }.ShowDialog() != true) return;
            }
            catch (Exception ex)
            {
                // исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»)
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: параметры выгрузки задачи не открылись", ex);
                ShowError("Окно параметров выгрузки не открылось — подробности в журнале плагина.");
                return;
            }
            _export = ExportOverride.From(current);
            _exportEdits++;
            ShowExport();
        }

        private void ExportReset_Click(object sender, RoutedEventArgs e)
        {
            _export = null;
            _exportEdits++;
            ShowExport();
        }

        // ── примеры ──

        /// <summary>Примеры — по блокам моделей строки (как в окне сейчас); блоков нет — «S1».</summary>
        private void UpdateExamples()
        {
            var models = EffectiveModels();
            // NWF строка не собирает — имя бледным, пример папки — без файла
            // галочка снята — раздел серый и не правится (разработчик, 03.11.2026); у строки с подзадачами галочек нет, а поля
            // задаются для подзадач — правятся
            var leaf = _task.Children.Count == 0;
            var makes = MakeNwf.IsChecked == true;
            MakeReports.IsEnabled = leaf && makes;
            var nwfOn = !leaf || makes;
            var reportsOn = !leaf || makes && MakeReports.IsChecked == true;
            NwfFields.IsEnabled = nwfOn;
            NwfFields.Opacity = nwfOn ? 1 : 0.45;
            ReportFields.IsEnabled = reportsOn;
            ReportFields.Opacity = reportsOn ? 1 : 0.45;
            // {Блок} — общий блок моделей строки; разные — для примера первый из них (сохранить с {Блок} не даст проверка)
            var blocks = models.Select(m => ExportNodes.BlockOf(m, _common)).Where(b => b.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var block = blocks.FirstOrDefault() ?? "S1";
            var root = Folder.Text.Trim();
            // примеров под папками NWC и NWF нет (разработчик, 07.11.2026: «пример просто уберём, он не нужен»)

            var on = Checked();
            var format = on.Contains("HTML") ? "HTML" : on.FirstOrDefault() ?? "";
            var report = ExportNodes.InProject(root, ExportNames.Fill(ReportFolder.Text, ExportNodes.Labels(block, null, _common, DateTime.Today, format)));
            ReportExample.Text = report.Length == 0 ? "Папка не задана" : "Пример: " + report;
            ReportExample.ToolTip = report;
        }

        /// <summary>Имя NWF, как сейчас в окне: своё или название строки.</summary>
        private string NwfNameText() => (NwfName.Text.Trim().Length > 0 ? NwfName.Text : TaskName.Text).Trim();

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
            var path = FolderDialog.Pick(Owner, Folder.Text, "Папка проекта");
            if (path != null) Folder.Text = path;
        }

        /// <summary>
        /// Папка NWC / NWF / отчётов — полным путём (разработчик, 07.11.2026: «пишем полный путь к папкам; в полях для задания пути
        /// тоже пишется полный путь»; было — внутри папки задачи коротко). Открывается с папки в поле, нет — с папки задачи.
        /// </summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)((Button)sender).Tag;
            var current = ExportNodes.InProject(Folder.Text, box.Text);
            var start = Directory.Exists(current) ? current : Folder.Text.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var path = FolderDialog.Pick(Owner, start, "Папка");
            if (path != null) box.Text = path;
        }

        // ── экспорт и импорт ──

        /// <summary>Свои настройки, как сейчас в окне; от уровня выше — null.</summary>
        private NodeSettings Collect() => new NodeSettings
        {
            Models = _modelsOwn ? _models.Select(m => m.Model).ToList() : null,
            TestsFiles = _testsOwn ? _tests.Select(r => r.Path).ToList() : null,
            HideSets = _hideOwn ? _hide.Select(h => h.Clone()).ToList() : null,
            MakeReports = MakeReports.IsChecked == true,
            NwfName = NwfName.Text.Trim().Length > 0 ? NwfName.Text.Trim() : null,
            MakeNwf = MakeNwf.IsChecked == true,
            // выключатель «NWC» — только в списке задач: панель его не меняет
            NoNwc = _task.Settings.NoNwc,
            Skipped = _task.Settings.Skipped,
            Folder = _folder.Value,
            NwcFolder = _nwc.Value,
            NwfFolder = _nwf.Value,
            ReportFolder = _report.Value,
            ReportFormats = _formatsOwn ? Checked() : null,
            Edited = _editedOwn ? CollectEdited() : null,
            Export = _export?.Clone(),
            ViewName = _hasView ? _view.Value : _task.Settings.ViewName,
            ServerFolder = _serverFolder ?? _task.Settings.ServerFolder,
        };

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var error = Validate();
            ShowError(error ?? "");
            if (error != null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ExportFiles.Filter,
                FileName = string.Concat(TaskName.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json",
            };
            if (dialog.ShowDialog(Owner) != true) return;
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
                ShowError("Файл не записан: " + ex.Message);
            }
        }

        /// <summary>Задача или подзадача из файла — подзадачей сюда (с вложенными); добавится по «Сохранить».</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(Owner) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                if (file.Kind == ExportFile.Common)
                {
                    ShowError("В файле — общие настройки, а не задача: их открывают в шестерёнке.");
                    return;
                }
                var taken = _task.Children.Concat(_imported).Select(c => c.Name);
                _imported.Add(file.Kind == ExportFile.Project ? ExportFiles.ToTop(file.ProjectData, taken) : ExportFiles.ToTask(file.TaskData, taken));
                ShowError("");
                Info.Text = "Добавятся по «Сохранить»: " + string.Join(", ", _imported.Select(t => t.Name));
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт не прочитан", ex);
                ShowError(ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message);
            }
        }

        // ── сохранить и отменить: решает главное окно (запись на сервер, перестройка дерева) ──

        /// <summary>Строка записана в дерево (настройки применены) — главное окно пишет задачу на сервер.</summary>
        public event EventHandler Saved;
        /// <summary>«Отменить» — главное окно открывает строку заново (новую несохранённую — убирает).</summary>
        public event EventHandler Cancelled;

        /// <summary>Панель узкая — «от «…»» и «Изменить» под значением, а не справа (разработчик, 03.11.2026: «на любой ширине — удобно»).</summary>
        public bool Narrow { get; private set; }

        private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var narrow = e.NewSize.Width < 760;
            if (narrow == Narrow) return;
            Narrow = narrow;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Narrow)));
        }

        /// <summary>В панели есть несохранённые правки.</summary>
        public bool Dirty => State() != _opened;

        /// <summary>Окно, где стоит панель, — хозяин диалогов.</summary>
        private new Window Owner => Window.GetWindow(this);

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (TrySave()) Saved?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Что не так — красным внизу и окном поверх (разработчик, 07.11.2026: «предупреждение выходит красным где-то внизу, незаметно —
        /// пусть выходит в отдельном окошке»); пусто — только убрать строку.
        /// </summary>
        private void ShowError(string text)
        {
            Error.Text = text ?? "";
            if (string.IsNullOrEmpty(text)) return;
            var owner = Window.GetWindow(this);
            if (owner != null) MessageBox.Show(owner, text, "Менеджер задач", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(text, "Менеджер задач", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

        /// <summary>Журнал последней выгрузки строки — его даёт список задач (итог запуска в папке задачи); null — не выгружали.</summary>
        public Func<string> LastJournal { get; set; }

        /// <summary>«Журнал выгрузки»: журнал последней выгрузки строки; нет — папка «Журналы выгрузки» задачи; нет и её — сказать.</summary>
        private void Journal_Click(object sender, RoutedEventArgs e)
        {
            var journal = LastJournal?.Invoke();
            var folder = ExportNodes.FolderOf(ExportNodes.Root(_task));
            var dir = NwcRunPlan.IsRooted(folder) ? Path.Combine(folder, "Журналы выгрузки") : null;
            var target = !string.IsNullOrEmpty(journal) && File.Exists(journal) ? journal : dir != null && Directory.Exists(dir) ? dir : null;
            if (target == null)
            {
                var owner = Window.GetWindow(this);
                const string text = "Журналов выгрузки ещё нет — строку не выгружали.";
                if (owner != null) MessageBox.Show(owner, text, "Менеджер задач", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show(text, "Менеджер задач", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("Не открылось: " + target + " — " + ex.Message);
            }
        }

        /// <summary>Проверить и применить к строке дерева; ошибка — строкой внизу панели и false.</summary>
        public bool TrySave()
        {
            var error = Validate();
            ShowError(error ?? "");
            if (error != null) return false;

            // проверки по всему дереву (имена NWC, строки с NWF) — на уже применённых настройках; ошибка — вернуть как было
            var name = _task.Name;
            var settings = _task.Settings;
            _task.Name = TaskName.Text.Trim();
            _task.Settings = Collect();
            foreach (var t in _imported) _task.Add(t);
            var root = ExportNodes.Root(_task);
            error = root.Self().Select(t => ExportNodes.CheckNwcNames(t, _common)).FirstOrDefault(x => x != null)
                    ?? ExportNodes.CheckNwf(root, _common);
            if (error != null)
            {
                foreach (var t in _imported) _task.Remove(t);
                _task.Name = name;
                _task.Settings = settings;
                ShowError(error);
                return false;
            }
            _imported.Clear();
            return true;
        }

        /// <summary>Что не так в окне — первой ошибкой; null — всё верно.</summary>
        private string Validate()
        {
            var name = ExportNodes.CheckName(TaskName.Text, TakenNames);
            if (name != null) return name;
            if (_parent == null && Folder.Text.Trim().Length == 0) return "Нужна папка задачи — в ней NWC, NWF и отчёты.";
            if (_formatsOwn && Checked().Count == 0) return "Нужен хотя бы один формат отчётов.";
            if (_editedOwn && EditedOn.IsChecked == true)
            {
                if (DateOf(EditedDate1) == null || EditedMode.SelectedIndex == 2 && DateOf(EditedDate2) == null)
                    return "Отредактированные модели: дата — дд.мм.гггг.";
                if (EditedMode.SelectedIndex == 2 && DateOf(EditedDate1) > DateOf(EditedDate2))
                    return "Отредактированные модели: начало периода позже конца.";
            }
            if (_hasView && _view.Own && ViewName.Text.Trim().Length == 0) return "Нужно имя вида для выгрузки.";
            return (_testsOwn ? ExportNodes.CheckTestsFiles(_tests.Select(r => r.Path).ToList(), TestNames) : null)
                   ?? (MakeNwf.IsChecked == true ? ExportNodes.CheckNwfName(NwfNameText()) : null)
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
