using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Задачи выгрузки» (DECISIONS.md модуля, «Дерево задач с наследованием (03.11.2026)»): задачи из общего хранилища на
    /// сервере, под задачей — подзадачи любой глубины, у нижней строки ветки — её блоки. Строка — номер, название, краткая
    /// информация, «Запустить», «+» (подзадача), копия (у подзадачи), карандаш, корзина; у блока — только «Запустить». Перенос —
    /// перетаскиванием внутри своего уровня (порядковый номер задач — общий).
    /// </summary>
    public partial class ExportTasksWindow : Window
    {
        /// <summary>Строка окна: задача, подзадача или блок.</summary>
        public sealed class RowItem
        {
            public TreeRow Row { get; set; }
            public string Summary { get; set; }
            public bool Collapsed { get; set; }
            public bool HasInner { get; set; }

            public ExportTask Item => Row.Item;
            public string Number => Row.Number;
            public bool IsBlock => Row.Block != null;
            public bool IsTop => !IsBlock && Item.Parent == null;
            public string Name => IsBlock ? Row.Block.Name : Item.Name;
            public double NameSize => IsTop ? 18 : IsBlock ? 13 : 15;
            public FontWeight NameWeight => IsBlock ? FontWeights.Normal : FontWeights.SemiBold;
            public Thickness Indent => new Thickness(Row.Level * 28, 0, 0, IsBlock ? 2 : 6);
            // Segoe MDL2 Assets: E70D — вниз (развёрнуто), E76C — вправо (свёрнуто)
            public string Arrow => Collapsed ? "\uE76C" : "\uE70D";
            public Visibility ArrowVisibility => HasInner ? Visibility.Visible : Visibility.Hidden;
            // места кнопок остаются и у строки, где их нет, — значки строк стоят ровно
            public Visibility CopyVisibility => !IsBlock && !IsTop ? Visibility.Visible : Visibility.Hidden;
            public Visibility EditVisibility => IsBlock ? Visibility.Hidden : Visibility.Visible;
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
            // блоки свёрнуты: открыли окно — видно задачи и подзадачи
            foreach (var t in _roots.SelectMany(r => r.Self()).Where(t => t.Children.Count == 0)) _collapsed.Add(t);
            Refresh();
        }

        private void ShowBanner(string text)
        {
            Banner.Text = text;
            Banner.Visibility = Visibility.Visible;
        }

        // задачи — по порядковому номеру (разработчик, 02.11.2026: «зачем по алфавиту»): так прочитаны, новые — в конец
        private void Refresh()
        {
            RowsList.ItemsSource = ExportNodes.Rows(_roots, _collapsed, Settings)
                .Select(r => new RowItem
                {
                    Row = r,
                    Summary = ExportNodes.Summary(r, Settings),
                    Collapsed = r.Block == null && _collapsed.Contains(r.Item),
                    HasInner = r.Block == null && (r.Item.Children.Count > 0 || ExportNodes.TaskBlocks(r.Item, Settings).Count > 0),
                })
                .ToList();
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

        // «+» — сразу настройки новой строки (разбор настроек, 02.11.2026: без поиска строки и карандаша); «Отмена» — строки нет.
        // Названия вписывает человек (разработчик, 02.11.2026: «зачем автоматическое название»)
        private void AddTop_Click(object sender, RoutedEventArgs e)
        {
            var top = ExportTask.NewTop("");
            top.StoreOrder = NextOrder();
            _roots.Add(top);
            if (OpenSettings(top)) SaveTop(top);
            else _roots.Remove(top);
            Refresh();
        }

        /// <summary>Задача из файла — новой задачей; подзадача из файла — тоже, со своими настройками и по умолчанию; занятое название — «(2)».</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
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
                Refresh();
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
            var sub = parent.Add(new ExportTask { Name = "" });
            _collapsed.Remove(parent);
            _collapsed.Add(sub);
            if (OpenSettings(sub)) SaveTop(ExportNodes.Root(parent));
            else parent.Remove(sub);
            Refresh();
        }

        /// <summary>
        /// Копия подзадачи с вложенными — сразу под ней и сразу в её настройки: переименовать, поменять файл проверок и имя NWF
        /// (разработчик, 03.11.2026: «копируем, переименовываем на „Средняя критичность“»). «Отмена» — копии нет.
        /// </summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            var parent = task.Parent;
            var copy = task.Copy();
            copy.Name = ExportFiles.UniqueName(task.Name, parent.Children.Select(c => c.Name));
            parent.Add(copy);
            parent.Children.Remove(copy);
            parent.Children.Insert(parent.Children.IndexOf(task) + 1, copy);
            foreach (var t in copy.Self().Where(t => t.Children.Count == 0)) _collapsed.Add(t);
            if (OpenSettings(copy)) SaveTop(ExportNodes.Root(parent));
            else parent.Remove(copy);
            Refresh();
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
            Refresh();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            if (OpenSettings(task)) SaveTop(ExportNodes.Root(task));
            Refresh();
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
            }
            Refresh();
        }

        /// <summary>Настройки строки (карандаш); true — сохранены.</summary>
        private bool OpenSettings(ExportTask task) =>
            Nested(() => new ExportTaskWindow(task, Settings)
            {
                Owner = this, RevitYear = RevitYear, TakenNames = SiblingsOf(task).Where(t => t != task).Select(t => t.Name).ToList(),
            }.ShowDialog(), "Настройки задачи");

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

        // перетаскивание строки: нажали на строку (не на кнопку) и повели мышью — перенос внутри своего уровня; блоки не переносятся
        private void Row_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragArmed = !RowOf(sender).IsBlock && !(e.OriginalSource is DependencyObject d && FindButton(d));
            _dragStart = e.GetPosition(this);
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
            e.Effects = item != null && !row.IsBlock && item != row.Item && item.Parent == row.Item.Parent ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var row = RowOf(sender);
            var target = row.Item;
            if (item == null || row.IsBlock || item.Parent != target.Parent) return;
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
