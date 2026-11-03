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
    /// Настройки задачи или подзадачи «Задач выгрузки» (карандаш; разделы — по разработчику, 03.11.2026): «Общее» — название,
    /// папка задачи; «Экспорт» — вид, папка для NWC, модели (одной строкой, список — по «Показать»), параметры выгрузки (по общим
    /// настройкам или свои — то же окно, что шестерёнка); «NWF и отчёты» — «Формировать NWF», проверки, скрыть в NWF, папки NWF и
    /// отчётов, форматы. Каждая настройка — своя или от уровня выше; у задачи верхнего уровня всё своё. Правятся копии: «Отмена»
    /// оставляет как было.
    /// </summary>
    public partial class ExportTaskWindow : Window, INotifyPropertyChanged
    {
        /// <summary>
        /// Строка списка моделей (разработчик, 03.11.2026: «компактнее, лаконичнее»): над первой моделью блока — «S1 · 4 модели»; имя
        /// модели без пути и «.rvt»; имя NWC — только если отличается (двойной щелчок — поправить); отдел; корзина — при наведении.
        /// </summary>
        public sealed class ModelRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

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

            /// <summary>Первая строка блока — над ней заголовок блока (GroupTitle — ставит окно: «S1 · 4 модели»).</summary>
            public bool First { get; set; }
            public string GroupTitle { get; set; }
            public Visibility HeaderVisibility => First ? Visibility.Visible : Visibility.Collapsed;
            public Visibility BoxVisibility => Manual ? Visibility.Visible : Visibility.Collapsed;

            /// <summary>Имя модели без папки и «.rvt».</summary>
            public string DisplayName
            {
                get
                {
                    var n = FileName;
                    return n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                }
            }

            /// <summary>Имя NWC — только если не совпадает с именем модели; «не по шаблону» — если «М3» в имени нет.</summary>
            public string NwcHint
            {
                get
                {
                    ExportNames.NwcName(Path, _common.TrimName, out var matches);
                    var hint = Name == DisplayName ? "" : "→ " + Name;
                    if (_common.TrimName && Model.NwcName == null && !matches) hint += (hint.Length > 0 ? " · " : "") + "не по шаблону";
                    return hint;
                }
            }

            /// <summary>Имя NWC правится (двойной щелчок) — поле вместо текста.</summary>
            public bool Editing
            {
                get => _editing;
                set
                {
                    _editing = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NwcHint)));
                }
            }

            private bool _editing;
            public Visibility ShowVisibility => _editing ? Visibility.Collapsed : Visibility.Visible;
            public Visibility EditVisibility => _editing ? Visibility.Visible : Visibility.Collapsed;

            private string Auto => ExportNames.NwcName(Path, _common.TrimName, out _);

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

            public string Department
            {
                get
                {
                    var d = ExportNames.Department(Path, _common.Departments.Select(x => x.Department));
                    return string.IsNullOrEmpty(d) ? "—" : d;
                }
            }
        }

        /// <summary>Формат отчёта в выпадающем списке: название, как в Navisworks, и галочка.</summary>
        public sealed class FormatItem
        {
            public string Name { get; set; }
            public bool On { get; set; }
        }


        // пример, пока моделей нет
        private const string SampleModel = "0000_01_01_S1_KJ01_01_M3.rvt";

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
        private bool _modelsOwn, _testsOwn, _hideOwn, _formatsOwn, _showModels;
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
            _hideOwn = _parent == null || s.HideSets != null;
            HideList.ItemsSource = _hide;
            FillHide(_hideOwn ? s.HideSets ?? ExportNodes.HideSetsOf(task) : ExportNodes.HideSetsOf(_parent));
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
            ShowExport();
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
            string.Join(";", _tests.Select(r => r.Path)), _hideOwn, string.Join(";", _hide.Select(h => h.Xml)), _formatsOwn, string.Join(";", Checked()), _export == null, _exportEdits,
            MakeNwf.IsChecked, MakeReports.IsChecked, NwfName.Text, Own(_folder), Own(_nwc), Own(_nwf), Own(_report), Own(_view), _imported.Count);

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

        /// <summary>Модели одной строкой: «4 модели · S1 · KJ, AR, OV, VK»; блоков больше трёх — «S1–S9 (9)».</summary>
        private string ModelsText(List<ExportModel> models)
        {
            var text = models.Count + " " + ExportTask.ModelsWord(models.Count);
            if (models.Count == 0) return text;
            var groups = ExportNodes.Blocks(models, _common);
            var named = groups.Where(g => g.Name.Length > 0).Select(g => g.Name).ToList();
            if (named.Count > 0) text += " · " + (named.Count > 3 ? named[0] + "–" + named[named.Count - 1] + " (" + named.Count + ")" : string.Join(", ", named));
            var loose = groups.Where(g => g.Name.Length == 0).Sum(g => g.Models.Count);
            if (loose > 0) text += " · без блока: " + loose;
            var departments = models.Select(m => ExportNames.Department(m.Path, _common.Departments.Select(d => d.Department)))
                .Where(d => d.Length > 0).Distinct().ToList();
            if (departments.Count > 0) text += " · " + string.Join(", ", departments);
            return text;
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

        /// <summary>
        /// Модели — по блокам (S1, S2, S10; без блока — в конце), в блоке — сначала с блоком в имени; от уровня выше — только смотреть.
        /// </summary>
        private void Regroup()
        {
            var rows = _modelsOwn ? _models : Copies(ExportNodes.ModelsOf(_parent)).Select(m => new ModelRow(_common, m)).ToList();
            var sorted = rows
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
                if (!m.First) continue;
                var n = sorted.Count(x => string.Equals(x.Effective, m.Effective, StringComparison.OrdinalIgnoreCase));
                m.GroupTitle = (m.Effective.Length == 0 ? "Без блока" : m.Effective) + " · " + n + " " + ExportTask.ModelsWord(n);
            }
            // общая папка моделей — одной строкой сверху, а не в каждой строке
            var folders = sorted.Select(m => m.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ModelsFolder.Text = folders.Count == 1 && folders[0].Length > 0 ? folders[0] : "";
            ModelsFolder.Visibility = ModelsFolder.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_modelsOwn)
            {
                _models.Clear();
                _models.AddRange(sorted);
            }
            ModelsList.ItemsSource = null;
            ModelsList.ItemsSource = sorted;
            ModelsSummary.Text = ModelsText(sorted.Select(m => m.Model).ToList());
            ModelsShow.Visibility = sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelsShow.Content = _showModels ? "Скрыть" : "Показать";
            ModelsPanel.Visibility = _showModels && sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateExamples();
        }

        // вписали блок — модель переходит в свой блок, когда курсор ушёл из поля
        private void Block_LostFocus(object sender, RoutedEventArgs e) => Regroup();

        // имя NWC — двойным щелчком (только у своих моделей)
        private void NwcName_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2 || !_modelsOwn) return;
            ((ModelRow)((FrameworkElement)sender).DataContext).Editing = true;
            e.Handled = true;
        }

        private void NwcName_LostFocus(object sender, RoutedEventArgs e) => ((ModelRow)((FrameworkElement)sender).DataContext).Editing = false;

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
            _showModels = true;
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
            if (dialog.ShowDialog(this) != true) return;
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
                Error.Text = "";
                Info.Text = "Добавлено правил: " + added + (sets.Count > added ? ", уже были: " + (sets.Count - added) : "") +
                            (skipped > 0 ? "; простые наборы выбора не годятся — пропущено: " + skipped : "") + ".";
                ShowHide();
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: поисковые наборы не прочитаны", ex);
                Error.Text = ex is InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message;
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
                var name = TaskName.Text.Trim().Length > 0 ? TaskName.Text.Trim() : Title;
                if (new ExportSettingsWindow(current, name) { Owner = this }.ShowDialog() != true) return;
            }
            catch (Exception ex)
            {
                // исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»)
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: параметры выгрузки задачи не открылись", ex);
                Error.Text = "Окно параметров выгрузки не открылось — подробности в журнале плагина.";
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
            var labels = ExportNodes.Labels(block, null, _common, DateTime.Today);
            var name = ExportNames.Fill(NwfNameText(), labels) + ".nwf";
            var nwfFolder = ExportNodes.InProject(root, ExportNames.Fill(NwfFolder.Text, labels));
            var nwf = nwfFolder + (makes ? "\\" + name : "");
            NwfExample.Text = "Пример: " + ExportNodes.Short(root, nwf);
            NwfExample.ToolTip = nwf;

            var model = models.FirstOrDefault() ?? new ExportModel { Path = SampleModel };
            var nwc = ExportNodes.InProject(root, ExportNames.Fill(NwcFolder.Text,
                ExportNodes.Labels(ExportNodes.BlockOf(model, _common), model, _common, DateTime.Today))) + "\\" + ExportNodes.NwcNameOf(model, _common) + ".nwc";
            NwcExample.Text = "Пример: " + ExportNodes.Short(root, nwc);
            NwcExample.ToolTip = nwc;

            var on = Checked();
            var format = on.Contains("HTML") ? "HTML" : on.FirstOrDefault() ?? "";
            var report = ExportNodes.InProject(root, ExportNames.Fill(ReportFolder.Text, ExportNodes.Labels(block, null, _common, DateTime.Today, format)));
            ReportExample.Text = report.Length == 0 ? "Папка не задана" : "Пример: " + ExportNodes.Short(root, report);
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
            HideSets = _hideOwn ? _hide.Select(h => h.Clone()).ToList() : null,
            MakeReports = MakeReports.IsChecked == true,
            NwfName = NwfName.Text.Trim().Length > 0 ? NwfName.Text.Trim() : null,
            MakeNwf = MakeNwf.IsChecked == true,
            Folder = _folder.Value,
            NwcFolder = _nwc.Value,
            NwfFolder = _nwf.Value,
            ReportFolder = _report.Value,
            ReportFormats = _formatsOwn ? Checked() : null,
            Export = _export?.Clone(),
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
            if (_formatsOwn && Checked().Count == 0) return "Нужен хотя бы один формат отчётов.";
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
