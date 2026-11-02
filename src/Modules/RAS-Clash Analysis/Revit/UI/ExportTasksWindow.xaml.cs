using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Задачи выгрузки» (DECISIONS.md модуля): проекты и их задачи из общего хранилища на сервере. Строка — номер, название,
    /// краткая информация, «Запустить», «+» (задача — у проекта), карандаш, корзина. Перенос — перетаскиванием: проекты между
    /// собой (порядковый номер — общий), задачи внутри проекта.
    /// </summary>
    public partial class ExportTasksWindow : Window
    {
        /// <summary>Строка окна: узел с номером и уровнем.</summary>
        public sealed class RowItem
        {
            public ExportTask Task { get; set; }
            public string Number { get; set; }
            public int Level { get; set; }
            public bool Collapsed { get; set; }

            public bool IsTask => Level == 0;
            public string Name => Task.Name;
            public string Summary => Task.Summary();
            public double NameSize => IsTask ? 18 : 16;
            public Thickness Indent => new Thickness(Level * 28, 0, 0, 6);
            // Segoe MDL2 Assets: E70D — вниз (развёрнуто), E76C — вправо (свёрнуто)
            public string Arrow => Collapsed ? "\uE76C" : "\uE70D";
            public Visibility ArrowVisibility => Task.Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
            // узел уже идёт у коллеги — запускать нельзя; место кнопки остаётся, чтобы значки не прыгали
            public Visibility RunVisibility => Task.RunningBy == null ? Visibility.Visible : Visibility.Hidden;
            // место кнопки остаётся, чтобы значки строк стояли ровно
            public Visibility AddChildVisibility => IsTask ? Visibility.Visible : Visibility.Hidden;
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
                if (broken.Count > 0) ShowBanner("Не прочитаны файлы проектов: " + string.Join(", ", broken) + " — подробности в журнале плагина.");
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
        }

        private void ShowBanner(string text)
        {
            Banner.Text = text;
            Banner.Visibility = Visibility.Visible;
        }

        // проекты — по порядковому номеру (разработчик, 02.11.2026: «зачем по алфавиту»): так прочитаны, новые — в конец
        private void Refresh()
        {
            RowsList.ItemsSource = ExportTask.Rows(_roots, _collapsed)
                .Select(r => new RowItem { Task = r.Task, Number = r.Number, Level = r.Level, Collapsed = _collapsed.Contains(r.Task) })
                .ToList();
        }

        /// <summary>
        /// Проект — на сервер. Коллега сохранил его после того, как окно его прочитало, — спросить: записать поверх или открыть
        /// заново. false — не записан (ошибка или «открыть заново»).
        /// </summary>
        private bool SaveProject(ExportTask project)
        {
            try
            {
                if (_store.SaveProject(project, false, out var who, out var when) == SaveResult.Saved) return true;
                var text = "Проект «" + project.Name + "» изменил " + (who ?? "коллега") + " в " + when.ToString("HH:mm") +
                           ", пока он был открыт у вас.\n\nДа — записать ваши правки поверх.\nНет — открыть проект с сервера заново " +
                           "(ваши правки пропадут).";
                if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                    return _store.SaveProject(project, true, out _, out _) == SaveResult.Saved;
                var fresh = _store.Reload(project);
                var at = _roots.IndexOf(project);
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
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: проект не записан на сервер — " + project.Name, ex);
                MessageBox.Show(this, "Проект «" + project.Name + "» не сохранён на сервере: " + ex.Message +
                                      "\n\nПравки остались в этом окне — закроете его, и они пропадут.", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static RowItem RowOf(object sender) => (RowItem)((FrameworkElement)sender).DataContext;

        private List<ExportTask> SiblingsOf(ExportTask task) => task.Parent?.Children ?? _roots;

        // «+» — сразу настройки новой строки (разбор настроек, 02.11.2026: без поиска строки и карандаша); «Отмена» — строки нет.
        // Вверху — проект (каталог задач), в строке проекта — задача (обычно блок)
        private void AddTask_Click(object sender, RoutedEventArgs e)
        {
            // названия вписывает человек (разработчик, 02.11.2026: «зачем автоматическое название»)
            var project = new ExportTask { Name = "", StoreOrder = NextOrder() };
            _roots.Add(project);
            if (OpenSettings(project)) SaveProject(project);
            else _roots.Remove(project);
            Refresh();
        }

        /// <summary>Проект с задачами из файла — новым проектом; занятое название — «ArmanQala (2)».</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ExportFiles.Filter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = ExportFiles.Load(dialog.FileName);
                if (file.Kind != ExportFile.Project)
                {
                    MessageBox.Show(this, file.Kind == ExportFile.Task
                            ? "В файле — задача. Её добавляют в проект: карандаш проекта → «Импорт задач…»."
                            : "В файле — общие настройки. Их открывают в шестерёнке → «Импорт…».",
                        Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var project = ExportFiles.ToProject(file.ProjectData, _roots.Select(r => r.Name));
                project.StoreOrder = NextOrder();
                _roots.Add(project);
                SaveProject(project);
                Refresh();
            }
            catch (System.Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: импорт проекта не прочитан", ex);
                MessageBox.Show(this, ex is System.IO.InvalidDataException ? ex.Message : "Файл не прочитан: " + ex.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private int NextOrder() => _roots.Count == 0 ? 1 : _roots.Max(r => r.StoreOrder) + 1;

        private void AddChild_Click(object sender, RoutedEventArgs e)
        {
            var project = RowOf(sender).Task;
            var task = project.Add(new ExportTask { Name = "" });
            _collapsed.Remove(project);
            if (OpenSettings(task)) SaveProject(project);
            else project.Remove(task);
            Refresh();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Task;
            if (!_collapsed.Remove(task)) _collapsed.Add(task);
            Refresh();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Task;
            var inner = task.Descendants;
            var text = "Удалить «" + task.Name + "»" + (inner > 0 ? " и " + inner + " " + ExportTask.TasksWord(inner) : "") + "?";
            if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            if (task.Parent != null)
            {
                var project = task.Parent;
                project.Remove(task);
                SaveProject(project);
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
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: проект не удалён с сервера — " + task.Name, ex);
                    MessageBox.Show(this, "Проект «" + task.Name + "» не удалён с сервера: " + ex.Message, Title, MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            Refresh();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Task;
            if (OpenSettings(task)) SaveProject(ExportNodes.Root(task));
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

        /// <summary>Настройки строки (карандаш): проекта или задачи; true — сохранены.</summary>
        private bool OpenSettings(ExportTask task) =>
            task.Parent == null
                ? Nested(() => new ExportProjectWindow(task, Settings)
                {
                    Owner = this, TakenNames = _roots.Where(r => r != task).Select(r => r.Name).ToList(),
                }.ShowDialog(), "Настройки проекта")
                : Nested(() => new ExportTaskWindow(task, Settings) { Owner = this, RevitYear = RevitYear }.ShowDialog(), "Настройки задачи");

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

        // перетаскивание строки: нажали на строку (не на кнопку) и повели мышью — перенос внутри своего уровня
        private void Row_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragArmed = !(e.OriginalSource is DependencyObject d && FindButton(d));
            _dragStart = e.GetPosition(this);
        }

        private void Row_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (System.Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragArmed = false;
            DragDrop.DoDragDrop((DependencyObject)sender, RowOf(sender).Task, DragDropEffects.Move);
        }

        private void Row_DragOver(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var target = RowOf(sender).Task;
            // проекты — между собой, задачи — внутри своего проекта
            e.Effects = item != null && item != target && item.Parent == target.Parent ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var target = RowOf(sender).Task;
            if (item == null || item.Parent != target.Parent) return;
            if (!ExportTask.MoveTo(SiblingsOf(item), item, target)) return;
            if (item.Parent != null) SaveProject(item.Parent);
            else
            {
                // порядковые номера проектов — заново; на сервер — у кого номер поменялся
                for (var i = 0; i < _roots.Count; i++)
                {
                    if (_roots[i].StoreOrder == i + 1) continue;
                    _roots[i].StoreOrder = i + 1;
                    SaveProject(_roots[i]);
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
