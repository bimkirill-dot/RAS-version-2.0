using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Задачи выгрузки» (DECISIONS.md модуля, «Дерево задач с наследованием», «Боковая панель» — 03.11.2026): слева — задачи из
    /// общего хранилища на сервере, под задачей — подзадачи любой глубины; справа — настройки выбранной строки (ExportTaskPanel).
    /// Строка — номер, название, краткая информация, «Запустить», «+» (подзадача), копия (у подзадачи), корзина; щелчок — её
    /// настройки справа. Новая строка («+», копия) — сразу в панели, на сервер — по «Сохранить». Ушли со строки или закрыли окно с
    /// несохранёнными правками — вопрос. Перенос — перетаскиванием внутри своего уровня (порядковый номер задач — общий).
    /// </summary>
    public partial class ExportTasksWindow : Window
    {
        /// <summary>
        /// Ширины столбцов таблицы — общие у шапки и строк: узко — «Проверки» скрываются, совсем узко — и «Время»
        /// (название, модели, выключатели, статус и запуск — всегда).
        /// </summary>
        public sealed class TableLayout : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private bool _wide = true, _time = true;
            public GridLength Tests => _wide ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
            public GridLength Time => _time ? new GridLength(72) : new GridLength(0);
            public Visibility WideVisibility => _wide ? Visibility.Visible : Visibility.Collapsed;
            public Visibility TimeVisibility => _time ? Visibility.Visible : Visibility.Collapsed;

            /// <summary>Ширина таблицы — какие столбцы показывать.</summary>
            public void Fit(double width)
            {
                var wide = width >= 796;
                var time = width >= 596;
                if (wide == _wide && time == _time) return;
                _wide = wide;
                _time = time;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));
            }
        }

        private readonly TableLayout _layout = new TableLayout();

        /// <summary>Строка окна: задача или подзадача.</summary>
        public sealed class RowItem : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public TreeRow Row { get; set; }
            public RowColumns Columns { get; set; }
            public TableLayout Layout { get; set; }
            public bool Collapsed { get; set; }
            public bool HasInner { get; set; }
            private bool _selected;

            /// <summary>Выбрана — меняется без перестройки списка (строки не прыгают, прокрутка на месте).</summary>
            public bool IsSelected
            {
                get => _selected;
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public ExportTask Item => Row.Item;
            public string Number => Row.Number;
            public bool IsTop => Item.Parent == null;
            // галочка «делать» — у подзадач; снята у неё или выше — строка серая
            public Visibility DoVisibility => IsTop ? Visibility.Collapsed : Visibility.Visible;
            public bool DoChecked => !Item.Settings.Skipped;
            public double RowOpacity => Item.IsSkipped ? 0.45 : 1;
            public string DoTip => Item.Settings.Skipped ? "Не делается при запуске строки выше — включить" : "Делается при запуске строки выше — выключить";
            public string Name => Item.Name;
            public double NameSize => IsTop ? 15 : 13;
            public FontWeight NameWeight => Row.Level <= 1 ? FontWeights.SemiBold : FontWeights.Normal;
            // задачи — с промежутком сверху, подзадачи — плотно; отступ уровня (20) — внутри столбца «Название», номер — ровным столбцом слева
            // задача — с промежутком сверху (группы задач — отдельно), подзадачи — плотно под ней
            public Thickness RowMargin => new Thickness(0, IsTop ? (Row.Number == "1" ? 2 : 14) : 0, 0, 1);
            // линия дерева: от стрелки строки выше — вниз вдоль подзадач (разработчик, 07.11.2026: «выделить задачу над подзадачами —
            // сейчас они сливаются»)
            public Visibility GuideVisibility => Row.Level > 0 ? Visibility.Visible : Visibility.Collapsed;
            // номер (44) + отступ строки выше (20 на уровень) + середина её стрелки (9)
            public Thickness GuideMargin => new Thickness(44 + (Row.Level - 1) * 20 + 9, -5, 0, -6);
            public Thickness Indent => new Thickness(Row.Level * 20, 0, 0, 0);

            // ── столбцы: «—» и значение от строки выше — бледно, «↑» — от строки выше ──
            private Brush Shade(bool muted) => muted ? Muted : Normal;
            public Brush Muted { get; set; }
            public Brush Normal { get; set; }
            public string ModelsText => Columns.Models + (Columns.ModelsFrom != null ? " ↑" : "");
            public string ModelsTip => Columns.ModelsFrom != null ? "от «" + Columns.ModelsFrom + "»" : null;
            public Brush ModelsBrush => Shade(Columns.ModelsFrom != null || Columns.Models == "0");
            public string TestsText => Columns.Tests + (Columns.TestsFrom != null ? " ↑" : "");
            public string TestsTip => Columns.Tests == "—" ? null : Columns.Tests + (Columns.TestsFrom != null ? "\nот «" + Columns.TestsFrom + "»" : "");
            public Brush TestsBrush => Shade(Columns.TestsFrom != null || Columns.Tests == "—");
            // имя NWF — в подсказке у галочки
            public string NwfTip => "Формировать NWF" + (Columns.NwfOn ? ": " + Columns.Nwf : "");

            // ── статус, запуск, время (в фоне: файл состояния задачи, даты NWC и моделей) ──
            private RowRun _run;
            private List<string> _changed = new List<string>();
            private bool _unknown;
            private string _estimate = "", _estimateTip;

            public RowRun LastRun => _run;

            public void SetStatus(RowRun run, List<string> changed, bool unknown, string estimate, string estimateTip)
            {
                _run = run;
                _changed = changed ?? new List<string>();
                _unknown = unknown;
                _estimate = estimate;
                _estimateTip = estimateTip;
                Notify();
            }

            private void Notify() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));

            // значки — рядом с галочкой своей функции (разработчик, 07.11.2026: «статус чего — NWC, NWF, отчётов?»); выключена — без
            // значка
            private const bool Marks = true;
            private StepMark Mark(string kind) => RunStates.MarkOf(_run, kind);
            private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            private bool NwcOn => Columns.NwcState != false;
            private bool NwfOn => Columns.NwfState != false;
            public Visibility NwcOkVisibility => Show(Marks && NwcOn && Mark("NWC") == StepMark.Ok);
            public Visibility NwcErrorVisibility => Show(Marks && NwcOn && Mark("NWC") == StepMark.Errors);
            public Visibility NwcChangedVisibility => Show(Marks && NwcOn && _changed.Count > 0);
            public Visibility NwcUnknownVisibility => Show(Marks && NwcOn && _unknown);
            public Visibility NwcRetryVisibility => Show(Marks && NwcOn && Mark("NWC") == StepMark.Errors);
            public string NwcStatusTip => RunStates.StepTip(_run, "NWC", _changed, _unknown);
            public Visibility NwfOkVisibility => Show(Marks && NwfOn && Mark("NWF") == StepMark.Ok);
            public Visibility NwfErrorVisibility => Show(Marks && NwfOn && Mark("NWF") == StepMark.Errors);
            public Visibility NwfRetryVisibility => Show(Marks && NwfOn && Mark("NWF") == StepMark.Errors);
            public string NwfStatusTip => RunStates.StepTip(_run, "NWF", new List<string>(), false);
            public string RunAt => _run == null ? "" : _run.At.ToString("dd.MM HH:mm");
            public string RunAtTip => _run == null ? null : $"Запускал {_run.By}, {_run.At:dd.MM.yyyy HH:mm}";
            public string TimeText => _estimate;
            public string TimeTip => _estimateTip;

            // Segoe MDL2 Assets: E70D — вниз (развёрнуто), E76C — вправо (свёрнуто)
            public string Arrow => Collapsed ? "\uE76C" : "\uE70D";
            public Visibility ArrowVisibility => HasInner ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>Версия Revit — для выбора моделей с Revit Server (настройки серверов — свои у каждой версии).</summary>
        public int RevitYear { get; set; } = 2026;

        /// <summary>
        /// Выполнить в Revit (внешним событием: окно немодальное, Revit API — только в вызове от Revit); параметр действия — UIApplication.
        /// Вне Revit (снимки окна) — null: действие выполняется сразу.
        /// </summary>
        public System.Action<System.Action<object>> InRevit { get; set; }

        /// <summary>Revit (UIApplication) — для выгрузки; вне Revit (снимки окна) — null. object — чтобы окно открывалось без Revit API.</summary>
        public object Revit { get; set; }

        /// <summary>Общие настройки — шестерёнка (из общего хранилища).</summary>
        public ExportSettings Settings { get; private set; } = new ExportSettings();

        private readonly ExportStore _store;
        private readonly List<ExportTask> _roots = new List<ExportTask>();
        private readonly HashSet<ExportTask> _collapsed = new HashSet<ExportTask>();
        private Point _dragStart;
        private bool _dragArmed;
        // выбранная строка и её панель; новая строка, ещё не сохранённая на сервер («+», копия) — _pending
        private ExportTask _selected;
        private ExportTaskPanel _panel;
        private ExportTask _pending;
        private List<RowItem> _items = new List<RowItem>();
        // несколько выбранных строк (Ctrl / Shift / Ctrl+A) — пакетное копирование и удаление; одна строка — _selected и её панель
        private readonly List<ExportTask> _picked = new List<ExportTask>();
        private ExportTask _anchor;

        /// <summary>Задачи и общие настройки — свои у каждого пользователя (с 08.11.2026; было — общее хранилище на сервере).</summary>
        public ExportTasksWindow(ExportStore store)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            TableHead.DataContext = _layout;
            _store = store;
            try
            {
                var broken = new List<string>();
                Settings = store.LoadCommon();
                _roots.AddRange(store.LoadProjects(broken));
                if (broken.Count > 0) ShowBanner("Не прочитаны файлы задач: " + string.Join(", ", broken) + " — подробности в журнале плагина.");
            }
            catch (System.Exception ex)
            {
                // папка задач не читается — править нельзя: записалось бы поверх того, что не прочитано
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: папка задач не прочитана — " + store.ProjectsDir, ex);
                ShowBanner("Задачи не загрузились — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
                Toolbar.Visibility = Visibility.Collapsed;
                RowsList.IsEnabled = false;
            }
            // по умолчанию — больше (разработчик, 07.11.2026: «окно по стандарту больше, но 90 % — слишком»): 80 % рабочего стола
            var desk = SystemParameters.WorkArea;
            Width = System.Math.Max(MinWidth, desk.Width * 0.8);
            Height = System.Math.Max(MinHeight, desk.Height * 0.85);
            Refresh();
            // закрыть окно с несохранёнными правками — спросить (идёт выгрузка — окно хода поверх, сюда не закрыть)
            Closing += (s, e) => e.Cancel = !Leave();
        }

        private void ShowBanner(string text)
        {
            Banner.Text = text;
            Banner.Visibility = Visibility.Visible;
        }

        // задачи — по порядковому номеру (разработчик, 02.11.2026: «зачем по алфавиту»): так прочитаны, новые — в конец
        private void Refresh()
        {
            // перестройка списка — с прокруткой на месте
            var offset = TreeScroll.VerticalOffset;
            var muted = (Brush)FindResource("Muted");
            var normal = (Brush)FindResource("Fg");
            RowsList.ItemsSource = _items = ExportNodes.Rows(_roots, _collapsed)
                .Select(r => new RowItem
                {
                    Row = r, Columns = ExportNodes.Columns(r), Collapsed = _collapsed.Contains(r.Item), HasInner = r.Item.Children.Count > 0,
                    IsSelected = r.Item == _selected || _picked.Contains(r.Item), Muted = muted, Normal = normal, Layout = _layout,
                })
                .ToList();
            Dispatcher.BeginInvoke(new System.Action(() => TreeScroll.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
            LoadStatus(_items);
        }

        // шапка таблицы — над столбцами строк: появилась полоса прокрутки — у шапки справа столько же места
        private void TreeScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var bar = TreeScroll.ActualWidth - TreeScroll.ViewportWidth;
            TableHead.Margin = new Thickness(0, 0, 4 + System.Math.Max(0, bar), 0);
            _layout.Fit(TreeScroll.ViewportWidth);
        }

        /// <summary>
        /// Задача — на сервер. Коллега сохранил её после того, как окно её прочитало, — спросить: записать поверх или открыть
        /// заново. false — не записана (ошибка или «открыть заново»).
        /// </summary>
        private bool SaveTop(ExportTask top)
        {
            try
            {
                if (_store.SaveProject(top, false, out var who, out var when) == SaveResult.Saved) return true;
                var text = "Задачу «" + top.Name + "» изменил " + (who ?? "коллега") + " в " + when.ToString("HH:mm") +
                           ", пока она была открыта у вас.\n\nДа — записать ваши правки поверх.\nНет — открыть задачу с сервера заново " +
                           "(ваши правки пропадут).";
                if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                    return _store.SaveProject(top, true, out _, out _) == SaveResult.Saved;
                var fresh = _store.Reload(top);
                var at = _roots.IndexOf(top);
                if (at >= 0)
                {
                    if (fresh != null) _roots[at] = fresh;
                    else _roots.RemoveAt(at);
                }
                Refresh();
                return false;
            }
            catch (System.Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: задача не записана на сервер — " + top.Name, ex);
                MessageBox.Show(this, "Задача «" + top.Name + "» не сохранена: " + ex.Message +
                                      "\n\nПравки остались в этом окне — закроете его, и они пропадут.", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static RowItem RowOf(object sender) => (RowItem)((FrameworkElement)sender).DataContext;

        private List<ExportTask> SiblingsOf(ExportTask task) => task.Parent?.Children ?? _roots;

        // ── выбор строки и панель настроек ──

        /// <summary>
        /// Уйти со строки: правок нет — да; есть (или строка новая) — «Сохранить изменения?»: Да — сохранить (не прошла проверка —
        /// остаться), Нет — правки пропадают (новая строка исчезает), Отмена — остаться.
        /// </summary>
        private bool Leave()
        {
            if (_panel == null || !_panel.Dirty && _pending == null) return true;
            var name = string.IsNullOrWhiteSpace(_selected?.Name) ? "новую строку" : "«" + _selected.Name + "»";
            var answer = MessageBox.Show(this, "Сохранить изменения в " + name + "?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes)
            {
                if (!_panel.TrySave()) return false;
                Commit();
                Refresh();
                return true;
            }
            DropPending();
            Refresh();
            return true;
        }

        /// <summary>Открыть настройки строки справа (после Leave); null — пусто.</summary>
        private void Show(ExportTask task)
        {
            _selected = task;
            _panel = null;
            if (task == null) Side.Content = SideEmpty;
            else
            {
                try
                {
                    var panel = new ExportTaskPanel(task, Settings)
                    {
                        RevitYear = RevitYear, TakenNames = SiblingsOf(task).Where(t => t != task).Select(t => t.Name).ToList(),
                        LastJournal = () => FindRun(task)?.Journal,
                    };
                    panel.Saved += (s, e) =>
                    {
                        Commit();
                        Refresh();
                        Show(_selected);
                    };
                    panel.Cancelled += (s, e) =>
                    {
                        DropPending();
                        Refresh();
                        Show(_selected);
                    };
                    _panel = panel;
                    Side.Content = panel;
                }
                catch (System.Exception ex)
                {
                    // исключение сквозь разметку панели роняло бы окно (CLAUDE.md, «Правила, о которые уже спотыкались»)
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: настройки строки не открылись — " + task.Name, ex);
                    _selected = null;
                    Side.Content = SideEmpty;
                    MessageBox.Show(this, "Настройки не открылись — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).",
                        Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            // строка уже в списке — только отметить; новой ещё нет — перестроить
            if (_selected == null || _items.Any(i => i.Item == _selected))
                foreach (var i in _items) i.IsSelected = i.Item == _selected || _picked.Contains(i.Item);
            else Refresh();
        }

        /// <summary>Строка сохранена в панели — задача на сервер; строка уже не новая. Коллега поменял, а открыли заново — её нет в дереве.</summary>
        private void Commit()
        {
            var top = ExportNodes.Root(_selected);
            _pending = null;
            // модели подзадач — и в задачу (разработчик, 03.11.2026: «модели из подзадач передаются в задачу»)
            foreach (var t in _selected.Self()) ExportNodes.PushModelsUp(t);
            SaveTop(top);
            if (!_roots.SelectMany(r => r.Self()).Contains(_selected)) _selected = null;
        }

        /// <summary>Новая несохранённая строка — убрать (выбранной становится строка выше).</summary>
        private void DropPending()
        {
            if (_pending == null) return;
            var parent = _pending.Parent;
            if (parent != null) parent.Remove(_pending);
            else _roots.Remove(_pending);
            if (_selected == _pending) _selected = parent;
            _pending = null;
        }

        /// <summary>Новая строка — в дерево и сразу в панель (название вписывает человек, разработчик, 02.11.2026); на сервер — по «Сохранить».</summary>
        private void Open(ExportTask fresh)
        {
            _pending = fresh;
            Show(fresh);
        }

        private void AddTop_Click(object sender, RoutedEventArgs e)
        {
            if (!Leave()) return;
            var top = ExportTask.NewTop("");
            top.StoreOrder = NextOrder();
            _roots.Add(top);
            Open(top);
        }

        /// <summary>Задача из файла — новой задачей; подзадача из файла — тоже, со своими настройками и по умолчанию; занятое название — «(2)».</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!Leave()) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                if (file.Kind == ExportFile.Common)
                {
                    MessageBox.Show(this, "В файле — общие настройки. Их открывают в шестерёнке → «Импорт…».", Title,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var taken = _roots.Select(r => r.Name);
                var top = file.Kind == ExportFile.Project
                    ? ExportFiles.ToTop(file.ProjectData, taken)
                    : ExportFiles.ToTop(new ProjectFile { Name = file.TaskData.Name, Node = ExportFiles.ToTask(file.TaskData, new string[0]).Settings,
                        Tasks = file.TaskData.Tasks }, taken);
                top.StoreOrder = NextOrder();
                _roots.Add(top);
                SaveTop(top);
                Show(top);
            }
            catch (System.Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт задачи не прочитан", ex);
                MessageBox.Show(this, ex is System.IO.InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private int NextOrder() => _roots.Count == 0 ? 1 : _roots.Max(r => r.StoreOrder) + 1;

        // новая подзадача — всё от строки выше
        private void AddSub_Click(object sender, RoutedEventArgs e)
        {
            var parent = RowOf(sender).Item;
            if (!Leave()) return;
            var sub = parent.Add(new ExportTask { Name = "" });
            _collapsed.Remove(parent);
            Open(sub);
        }

        /// <summary>
        /// Копия подзадачи с вложенными — сразу под ней и сразу в панели: переименовать, поменять файл проверок и имя NWF
        /// (разработчик, 03.11.2026: «копируем, переименовываем на „Средняя критичность“»). «Отменить» — копии нет.
        /// </summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            if (!Leave()) return;
            var copy = task.Copy();
            var parent = task.Parent;
            if (parent == null)
            {
                // задача — тоже (разработчик, 07.11.2026: «не можем скопировать задачу — нет значка»): в конец списка, новым файлом
                copy.Name = ExportFiles.UniqueName(task.Name, _roots.Select(r => r.Name));
                copy.StoreOrder = NextOrder();
                _roots.Add(copy);
                Open(copy);
                return;
            }
            copy.Name = ExportFiles.UniqueName(task.Name, parent.Children.Select(c => c.Name));
            parent.Add(copy);
            parent.Children.Remove(copy);
            parent.Children.Insert(parent.Children.IndexOf(task) + 1, copy);
            Open(copy);
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            var item = RowOf(sender).Item;
            if (!_collapsed.Remove(item)) _collapsed.Add(item);
            Refresh();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            var inner = task.Descendants;
            var text = "Удалить «" + task.Name + "»" + (inner > 0 ? " и " + inner + " " + ExportTask.SubtasksWord(inner) : "") + "?";
            if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            // удаляется выбранная строка или ветка над ней — панель закрывается без вопроса о правках
            var closes = _selected != null && task.Self().Contains(_selected);
            if (closes) _panel = null;
            if (_pending != null && task.Self().Contains(_pending))
            {
                // новая, ещё не сохранённая строка — убрать из дерева (на сервере её нет); удаляют её саму — на этом всё
                var onlyNew = _pending == task;
                DropPending();
                if (onlyNew)
                {
                    Refresh();
                    Show(closes ? null : _selected);
                    return;
                }
            }
            if (task.Parent != null)
            {
                var parent = task.Parent;
                parent.Remove(task);
                SaveTop(ExportNodes.Root(parent));
            }
            else
            {
                try
                {
                    _store.DeleteProject(task);
                    _roots.Remove(task);
                }
                catch (System.Exception ex)
                {
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: задача не удалена с сервера — " + task.Name, ex);
                    MessageBox.Show(this, "Задача «" + task.Name + "» не удалена: " + ex.Message, Title, MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            // список — заново (строки больше нет), затем — панель
            Refresh();
            if (closes) Show(null);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (Nested(() => new ExportSettingsWindow(Settings) { Owner = this }.Free().ShowDialog(), "Общие настройки"))
            {
                try
                {
                    _store.SaveCommon(Settings);
                }
                catch (System.Exception ex)
                {
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: общие настройки не записаны на сервер", ex);
                    MessageBox.Show(this, "Общие настройки не сохранены: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                // примеры в панели — по общим настройкам: открыть строку заново, если в ней нет правок
                if (_panel != null && !_panel.Dirty) Show(_selected);
            }
            Refresh();
        }

        /// <summary>Вложенное окно; исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»).</summary>
        private bool Nested(System.Func<bool?> show, string what)
        {
            try
            {
                return show() == true;
            }
            catch (System.Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: " + what + " — окно не открылось", ex);
                MessageBox.Show(this, what + " не открылись — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>Статус — щелчок: журнал последнего запуска строки (копия в папке задачи, у запускавшего — свой).</summary>
        private void Status_Click(object sender, RoutedEventArgs e)
        {
            var run = RowOf(sender).LastRun;
            if (run == null) return;
            if (string.IsNullOrEmpty(run.Journal) || !System.IO.File.Exists(run.Journal))
            {
                MessageBox.Show(this, "Журнал запуска не найден: " + (run.Journal ?? "—"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(run.Journal) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, "Журнал не открылся: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Выключатели списка — NWC, NWF, отчёты (разработчик, 07.11.2026: «выключатель должен быть у всего»; набросок принят): у строки
        /// с подзадачами — сразу у всех ниже; «■» (у подзадач по-разному) — щелчок включает у всех.
        /// </summary>
        private void Flag_Click(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            var row = RowOf(sender);
            var flag = (RowFlag)System.Enum.Parse(typeof(RowFlag), (string)box.Tag);
            var state = flag == RowFlag.Nwc ? row.Columns.NwcState : flag == RowFlag.Nwf ? row.Columns.NwfState : row.Columns.ReportsState;
            var on = state != true;
            Change(row.Item, () => ExportNodes.SetFlag(row.Item, flag, on, Settings));
        }

        /// <summary>Галочка подзадачи: снята — при ▷ у строки выше не делается и серая (разработчик, 07.11.2026); сразу на сервер.</summary>
        private void Do_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            var on = ((CheckBox)sender).IsChecked == true;
            Change(task, () =>
            {
                task.Settings.Skipped = !on;
                return null;
            });
        }

        /// <summary>
        /// Правка из списка — сразу на сервер. Строка (или строка ниже) открыта в панели с правками, или есть новая несохранённая —
        /// сначала вопрос: иначе «Сохранить» панели вернуло бы прежнее, а новая строка ушла бы на сервер без названия. Ошибка проверки —
        /// сообщением; не записалось — как было. Открытая строка — заново, с новым.
        /// </summary>
        private void Change(ExportTask task, System.Func<string> change)
        {
            var rows = task.Self().ToList();
            if ((_selected != null && rows.Contains(_selected) || _pending != null) && !Leave() || !_roots.SelectMany(r => r.Self()).Contains(task))
            {
                Refresh();
                return;
            }
            var before = rows.Select(t => t.Settings.Clone()).ToList();
            var error = change();
            if (error != null)
            {
                MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                Refresh();
                return;
            }
            if (!SaveTop(ExportNodes.Root(task)))
                for (var i = 0; i < rows.Count; i++) rows[i].Settings = before[i];
            Refresh();
            if (_selected != null && rows.Contains(_selected)) Show(_roots.SelectMany(r => r.Self()).Contains(_selected) ? _selected : null);
        }

        // перетаскивание строки: нажали на строку (не на кнопку) и повели мышью — перенос внутри своего уровня; нажатие — выбор
        private void Row_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var onButton = e.OriginalSource is DependencyObject d && FindButton(d);
            _dragArmed = !onButton;
            _dragStart = e.GetPosition(this);
            // щелчок по строке (не по кнопке) — её настройки справа; с Ctrl / Shift — выбрать несколько (пакетно копировать, удалить)
            var item = RowOf(sender).Item;
            if (onButton) return;
            var mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                _dragArmed = false;
                Pick(item, (mods & ModifierKeys.Shift) != 0);
                return;
            }
            if (_picked.Count > 0) _picked.Clear();
            else if (item == _selected) return;
            if (!Leave())
            {
                _dragArmed = false;
                return;
            }
            Show(item);
        }

        private void Row_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (System.Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragArmed = false;
            DragDrop.DoDragDrop((DependencyObject)sender, RowOf(sender).Item, DragDropEffects.Move);
        }

        private void Row_DragOver(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var row = RowOf(sender);
            e.Effects = item != null && item != row.Item && item.Parent == row.Item.Parent ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var row = RowOf(sender);
            var target = row.Item;
            if (item == null || item.Parent != target.Parent) return;
            if (!ExportTask.MoveTo(SiblingsOf(item), item, target)) return;
            if (item.Parent != null) SaveTop(ExportNodes.Root(item));
            else
            {
                // порядковые номера задач — заново; на сервер — у кого номер поменялся
                for (var i = 0; i < _roots.Count; i++)
                {
                    if (_roots[i].StoreOrder == i + 1) continue;
                    _roots[i].StoreOrder = i + 1;
                    SaveTop(_roots[i]);
                }
            }
            Refresh();
        }

        private static bool FindButton(DependencyObject d)
        {
            // Run и прочие элементы текста — не в дереве отображения: их родителя — по логическому дереву
            for (; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                // и галочка NWF: щелчок по ней — не выбор строки и не перетаскивание
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            return false;
        }
    }
}
