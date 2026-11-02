using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Задачи выгрузки»: дерево задач выгрузки NWC (DECISIONS.md модуля). Строка — номер, название, краткая информация,
    /// «Запустить», «+» (подзадача), карандаш (настройки узла), корзина. Перенос — перетаскиванием строки внутри своего
    /// уровня. Пока — дерево и общие настройки в памяти: хранение на сервере и окно настроек узла — следующими шагами.
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
            public string Arrow => Collapsed ? "" : "";
            public Visibility ArrowVisibility => Task.Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
            // узел уже идёт у коллеги — запускать нельзя; место кнопки остаётся, чтобы значки не прыгали
            public Visibility RunVisibility => Task.RunningBy == null ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>Выбор моделей для окна узла (в Revit — окно Revit «Открыть»); null — окно Windows.</summary>
        public System.Func<List<string>> PickModels { get; set; }

        /// <summary>Общие настройки — шестерёнка (пока в памяти).</summary>
        public ExportSettings Settings { get; set; } = new ExportSettings();

        private readonly List<ExportTask> _roots;
        private readonly HashSet<ExportTask> _collapsed = new HashSet<ExportTask>();
        private Point _dragStart;
        private bool _dragArmed;

        public ExportTasksWindow(List<ExportTask> roots)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _roots = roots;
            Refresh();
        }

        private void Refresh() =>
            RowsList.ItemsSource = ExportTask.Rows(_roots, _collapsed)
                .Select(r => new RowItem { Task = r.Task, Number = r.Number, Level = r.Level, Collapsed = _collapsed.Contains(r.Task) })
                .ToList();

        private static RowItem RowOf(object sender) => (RowItem)((FrameworkElement)sender).DataContext;

        private List<ExportTask> SiblingsOf(ExportTask task) => task.Parent?.Children ?? _roots;

        private void AddTask_Click(object sender, RoutedEventArgs e)
        {
            _roots.Add(new ExportTask { Name = "Новая задача" });
            Refresh();
        }

        private void AddChild_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Task;
            task.Add(new ExportTask { Name = "Новая подзадача" });
            _collapsed.Remove(task);
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
            var text = "Удалить «" + task.Name + "»" + (inner > 0 ? " и " + inner + " " + SubtasksWord(inner) : "") + "?";
            if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            if (task.Parent != null) task.Parent.Remove(task);
            else _roots.Remove(task);
            Refresh();
        }

        private static string SubtasksWord(int n)
        {
            var w = ExportTask.ModelsWord(n);
            return w == "модель" ? "подзадачу" : w == "модели" ? "подзадачи" : "подзадач";
        }

        private void Edit_Click(object sender, RoutedEventArgs e) =>
            Nested(() => new ExportNodeWindow(RowOf(sender).Task, Settings) { Owner = this, PickModels = PickModels }.ShowDialog(), "Настройки узла");

        private void Settings_Click(object sender, RoutedEventArgs e) =>
            Nested(() => new ExportSettingsWindow(Settings) { Owner = this }.ShowDialog(), "Общие настройки");

        /// <summary>Вложенное окно; исключение сквозь вложенный ShowDialog роняет Revit (CLAUDE.md, «Правила, о которые уже спотыкались»).</summary>
        private void Nested(System.Action show, string what)
        {
            try
            {
                show();
                Refresh();
            }
            catch (System.Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: " + what + " — окно не открылось", ex);
                MessageBox.Show(this, what + " не открылись — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Run_Click(object sender, RoutedEventArgs e) { }

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
            e.Effects = item != null && item != target && item.Parent == target.Parent ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ExportTask)) as ExportTask;
            var target = RowOf(sender).Task;
            if (item == null || item.Parent != target.Parent) return;
            if (ExportTask.MoveTo(SiblingsOf(item), item, target)) Refresh();
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
