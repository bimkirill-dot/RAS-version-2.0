using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        /// <summary>Строка окна: задача или подзадача.</summary>
        public sealed class RowItem : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public TreeRow Row { get; set; }
            public string Summary { get; set; }
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
            public string Name => Item.Name;
            public double NameSize => IsTop ? 14 : 13;
            public FontWeight NameWeight => Row.Level <= 1 ? FontWeights.SemiBold : FontWeights.Normal;
            // задачи — с промежутком сверху, подзадачи — плотно, отступ уровня — 22
            public Thickness Indent => new Thickness(Row.Level * 22, IsTop ? 8 : 0, 0, 2);
            // Segoe MDL2 Assets: E70D — вниз (развёрнуто), E76C — вправо (свёрнуто)
            public string Arrow => Collapsed ? "\uE76C" : "\uE70D";
            public Visibility ArrowVisibility => HasInner ? Visibility.Visible : Visibility.Hidden;
            // место кнопки остаётся и у задачи, где её нет, — значки строк стоят ровно
            public Visibility CopyVisibility => IsTop ? Visibility.Hidden : Visibility.Visible;
        }

        /// <summary>Версия Revit — для выбора моделей с Revit Server (настройки серверов — свои у каждой версии).</summary>
        public int RevitYear { get; set; } = 2026;

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

        /// <summary>Задачи и общие настройки — из общего хранилища на сервере: открыли окно — видно свежее у всех.</summary>
        public ExportTasksWindow(ExportStore store)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
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
                // сервер недоступен — смотреть можно нечего, править нельзя: правки не дошли бы до коллег
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: общее хранилище не прочитано — " + store.ProjectsDir, ex);
                ShowBanner("Сервер недоступен — задачи не загрузились. Проверьте сеть и откройте окно снова.");
                Toolbar.Visibility = Visibility.Collapsed;
                RowsList.IsEnabled = false;
            }
            Refresh();
            // закрыть окно с несохранёнными правками — спросить
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
            RowsList.ItemsSource = _items = ExportNodes.Rows(_roots, _collapsed)
                .Select(r => new RowItem
                {
                    Row = r, Summary = ExportNodes.Summary(r), Collapsed = _collapsed.Contains(r.Item), HasInner = r.Item.Children.Count > 0,
                    IsSelected = r.Item == _selected,
                })
                .ToList();
            Dispatcher.BeginInvoke(new System.Action(() => TreeScroll.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
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
                MessageBox.Show(this, "Задача «" + top.Name + "» не сохранена на сервере: " + ex.Message +
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
                foreach (var i in _items) i.IsSelected = i.Item == _selected;
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
            var parent = task.Parent;
            var copy = task.Copy();
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
                DropPending();
                Show(closes ? null : _selected);
                return;
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
                    MessageBox.Show(this, "Задача «" + task.Name + "» не удалена с сервера: " + ex.Message, Title, MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            if (closes) Show(null);
            else Refresh();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (Nested(() => new ExportSettingsWindow(Settings) { Owner = this }.ShowDialog(), "Общие настройки"))
            {
                try
                {
                    _store.SaveCommon(Settings);
                }
                catch (System.Exception ex)
                {
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: общие настройки не записаны на сервер", ex);
                    MessageBox.Show(this, "Общие настройки не сохранены на сервере: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

        // выгрузка — следующим шагом; молчащая кнопка выглядела бы сломанной (разбор 02.11.2026)
        private void Run_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show(this, "Выгрузка — в следующей версии: сейчас задачи только настраиваются и хранятся.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);

        // перетаскивание строки: нажали на строку (не на кнопку) и повели мышью — перенос внутри своего уровня; нажатие — выбор
        private void Row_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var onButton = e.OriginalSource is DependencyObject d && FindButton(d);
            _dragArmed = !onButton;
            _dragStart = e.GetPosition(this);
            // щелчок по строке (не по кнопке) — её настройки справа
            var item = RowOf(sender).Item;
            if (onButton || item == _selected) return;
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
                if (d is Button) return true;
            return false;
        }
    }
}
