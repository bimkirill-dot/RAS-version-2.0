using System;
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
    /// «Предупреждения» «Менеджера задач» (разработчик, 12.11.2026: «кнопка по общим предупреждениям рядом с импортом задачи;
    /// сортировка по задачам — нормально, читаемо, понятно; нажимаешь на задачу или подзадачу — открываются предупреждения по ней»;
    /// набросок принят). Слева — все задачи и подзадачи с числом ошибок и изменений (у строки — вместе с подзадачами), сверху —
    /// «Все задачи»; справа — предупреждения выбранной строки и её подзадач, каждая строка — своим заголовком: ошибки (красные),
    /// изменения (жёлтые), пропуски (серые, по умолчанию скрыты). «Журнал» — журнал запуска; «↻ Повторить ошибки» — окно
    /// закрывается, повтор ставит «Менеджер задач» (Retry*). Что у какой строки — RunWarnings.
    /// </summary>
    public partial class WarningsWindow : Window
    {
        /// <summary>Строка дерева слева; Task = null — «Все задачи».</summary>
        public sealed class RowView
        {
            public ExportTask Task { get; set; }
            public int Level { get; set; }
            public bool Collapsed { get; set; }
            // Segoe MDL2 Assets: E70D — вниз (развёрнуто), E76C — вправо (свёрнуто)
            public string Arrow => ((char)(Collapsed ? 0xE76C : 0xE70D)).ToString();
            public Visibility ArrowVisibility => Task != null && Task.Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
            public string Name { get; set; }
            public Thickness Indent { get; set; }
            public FontWeight Weight { get; set; }
            public string CountText { get; set; }
            public Brush CountBrush { get; set; }
        }

        private readonly IList<ExportTask> _roots;
        private readonly Dictionary<ExportTask, WarningGroup> _groups;
        private readonly Brush _error, _changed, _muted;
        private bool _ready;
        private readonly List<RowView> _views = new List<RowView>();

        /// <summary>«↻ Повторить ошибки»: строка, её последний запуск и файлы с ошибками; null — не нажимали.</summary>
        public ExportTask RetryTask { get; private set; }
        public RowRun RetryRun { get; private set; }
        public HashSet<string> RetryPaths { get; private set; }

        /// <summary>«Выгрузить изменённые»: строки запуска и NWC изменившихся моделей; null — не нажимали.</summary>
        public List<ExportTask> ExportTasks { get; private set; }
        public HashSet<string> ExportPaths { get; private set; }

        /// <param name="start">Строка, на которой открыть; null — «Все задачи».</param>
        /// <param name="collapsed">Свёрнутые строки — как в «Менеджере задач»; строка, на которой открыть, видна.</param>
        public WarningsWindow(IList<ExportTask> roots, Dictionary<ExportTask, WarningGroup> groups, ExportTask start, ISet<ExportTask> collapsed)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _roots = roots;
            _groups = groups;
            _error = (Brush)FindResource("Error");
            _changed = (Brush)FindResource("MarkChanged");
            _muted = (Brush)FindResource("Muted");

            _views.Add(new RowView { Name = "Все задачи", Weight = FontWeights.SemiBold, Indent = new Thickness(22, 0, 0, 0) });
            Count(_views[0], roots.SelectMany(r => r.Self()));
            var open = new HashSet<ExportTask>();
            for (var p = start?.Parent; p != null; p = p.Parent) open.Add(p);
            foreach (var t in roots.SelectMany(r => r.Self()))
            {
                var level = 0;
                for (var p = t.Parent; p != null; p = p.Parent) level++;
                var v = new RowView
                {
                    Task = t, Level = level, Collapsed = collapsed != null && collapsed.Contains(t) && !open.Contains(t),
                    Name = string.IsNullOrWhiteSpace(t.Name) ? "Без названия" : t.Name, Indent = new Thickness(level * 16, 0, 0, 0),
                    Weight = level == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                Count(v, t.Self());
                _views.Add(v);
            }
            ShowRows(_views.FirstOrDefault(v => v.Task == start && start != null) ?? _views[0]);
            _ready = true;
            ShowSelected();
        }

        // строки слева — без тех, что под свёрнутыми; выбранная остаётся (ушла под свёрнутую — выбирается та, что её свернула)
        private void ShowRows(RowView select)
        {
            var hidden = new HashSet<ExportTask>(_views.Where(v => v.Collapsed).SelectMany(v => v.Task.Self().Skip(1)));
            var shown = _views.Where(v => v.Task == null || !hidden.Contains(v.Task)).ToList();
            var was = _ready;
            _ready = false;
            Rows.ItemsSource = shown;
            Rows.Items.Refresh();
            while (select?.Task != null && hidden.Contains(select.Task)) select = _views.First(v => v.Task == select.Task.Parent);
            Rows.SelectedItem = select;
            _ready = was;
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            var v = (RowView)((FrameworkElement)sender).DataContext;
            v.Collapsed = !v.Collapsed;
            var selected = Rows.SelectedItem as RowView;
            ShowRows(selected);
            if (Rows.SelectedItem != selected) ShowSelected();
        }

        // число у строки: ошибки и изменения у неё и подзадач; есть ошибки — красное, только изменения — жёлтое, ничего — «—»
        private void Count(RowView v, IEnumerable<ExportTask> tasks)
        {
            var mine = tasks.Where(_groups.ContainsKey).Select(t => _groups[t]).ToList();
            var errors = mine.Sum(g => g.Errors.Count);
            var count = mine.Sum(g => g.Count);
            v.CountText = count > 0 ? count.ToString() : "—";
            v.CountBrush = errors > 0 ? _error : count > 0 ? _changed : _muted;
        }

        private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

        /// <summary>Выбранные слева — самые верхние (подзадача выбранной строки — уже в ней); «Все задачи» или ничего — null (все).</summary>
        private List<ExportTask> Picked()
        {
            var views = Rows.SelectedItems.OfType<RowView>().ToList();
            if (views.Count == 0 || views.Any(v => v.Task == null)) return null;
            return ExportTask.Outermost(_roots, views.Select(v => v.Task));
        }

        // кнопки «Выгрузить изменённые»: сколько NWC у всех и у выбранных; нечего — серые
        private void UpdateExport()
        {
            var all = RunWarnings.ChangedNwcs(_roots, _groups).Count;
            ExportAllText.Text = "По всем задачам (" + all + ")";
            ExportAll.IsEnabled = all > 0;
            var chosen = Picked();
            var mine = chosen == null ? 0 : RunWarnings.ChangedNwcs(chosen, _groups).Count;
            ExportPickedText.Text = chosen == null ? "По выбранным" :
                (chosen.Count == 1 ? "По «" + chosen[0].Name + "»" : "По выбранным: " + chosen.Count) + " (" + mine + ")";
            ExportPicked.IsEnabled = mine > 0;
        }

        private void ExportAll_Click(object sender, RoutedEventArgs e) => Export(_roots.ToList());

        private void ExportPicked_Click(object sender, RoutedEventArgs e)
        {
            var chosen = Picked();
            if (chosen != null) Export(chosen);
        }

        // запуск — у строк, где есть изменившиеся модели (у всех задач — у задач с ними), только их NWC; окно закрывается, запуск ставит «Менеджер задач»
        private void Export(List<ExportTask> rows)
        {
            var paths = RunWarnings.ChangedNwcs(rows, _groups);
            if (paths.Count == 0) return;
            ExportTasks = rows.Where(r => RunWarnings.ChangedNwcs(new[] { r }, _groups).Count > 0).ToList();
            ExportPaths = paths;
            Close();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ShowSelected();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        /// <summary>Справа — строка и её подзадачи по порядку дерева, у каждой — то, что отмечено в «Показать».</summary>
        private void ShowSelected()
        {
            if (!_ready) return;
            Details.Children.Clear();
            Scroll.ScrollToTop();
            UpdateExport();
            var chosen = Picked();
            var picked = chosen != null && chosen.Count == 1 ? chosen[0] : null;
            var tasks = chosen == null ? _roots.SelectMany(r => r.Self()) : chosen.SelectMany(t => t.Self()).Distinct();
            var shown = 0;
            foreach (var t in tasks)
            {
                if (!_groups.TryGetValue(t, out var g)) continue;
                var errors = ShowErrors.IsChecked == true ? g.Errors : new List<WarningLine>();
                var changes = ShowChanges.IsChecked == true ? g.Changes : new List<WarningLine>();
                var unknown = ShowChanges.IsChecked == true && g.Unknown;
                var skips = ShowSkips.IsChecked == true ? g.Skips : new List<WarningLine>();
                if (errors.Count == 0 && changes.Count == 0 && !unknown && skips.Count == 0) continue;
                AddGroup(g, errors, changes, unknown, skips, shown == 0);
                shown++;
            }
            if (shown == 0)
                Details.Children.Add(new TextBlock
                {
                    Text = picked == null ? "Предупреждений нет." : "У «" + picked.Name + "» предупреждений нет.", Foreground = _muted,
                    Margin = new Thickness(0, 4, 0, 0),
                });
        }

        private void AddGroup(WarningGroup g, List<WarningLine> errors, List<WarningLine> changes, bool unknown, List<WarningLine> skips, bool first)
        {
            var box = new StackPanel { Margin = new Thickness(0, first ? 0 : 26, 0, 0) };
            box.Children.Add(new TextBlock
            {
                Text = RunStates.Key(g.Task), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            if (g.Run != null)
            {
                var info = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = $"Запуск {g.Run.At:dd.MM.yyyy HH:mm} · {g.Run.By}" + (g.Run.Seconds > 0 ? " · " + NwcHistory.Duration(g.Run.Seconds) : ""),
                    Foreground = _muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
                });
                if (!string.IsNullOrEmpty(g.Run.Journal))
                    info.Children.Add(Link("Журнал", "Журнал запуска: " + g.Run.Journal, () => OpenJournal(g.Run.Journal)));
                if (errors.Count > 0)
                    info.Children.Add(Link("↻ Повторить ошибки", "Заново — только то, что с ошибкой (" + errors.Count + ")", () =>
                    {
                        RetryTask = g.Task;
                        RetryRun = g.Run;
                        RetryPaths = new HashSet<string>(errors.Select(x => x.Path).Where(p => p != null), StringComparer.OrdinalIgnoreCase);
                        Close();
                    }));
                box.Children.Add(info);
            }

            // таблица (разработчик, 12.11.2026: «окно — в табличном виде»): тип, функция, модель, причина; ширины столбцов — общие у всех таблиц окна
            var rows = errors.Select(x => ("Ошибка", _error, x))
                .Concat(changes.Select(x => ("Изменение", _changed, x)))
                .Concat(skips.Select(x => ("Пропуск", _muted, x))).ToList();
            if (rows.Count > 0) box.Children.Add(Table(rows));
            if (unknown)
                box.Children.Add(new TextBlock
                {
                    Text = "Дату части моделей Revit Server не сообщил — изменились ли они, не известно.", Foreground = _muted, FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                });
            Details.Children.Add(box);
        }

        private UIElement Table(List<(string Type, Brush Brush, WarningLine Line)> rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var group in new[] { "WarnType", "WarnKind", "WarnName" })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = group });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            var line = (Brush)FindResource("Line");

            void Row(int r, Brush background, params UIElement[] cells)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var back = new Border { Background = background, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) };
                Grid.SetRow(back, r);
                Grid.SetColumnSpan(back, 4);
                grid.Children.Add(back);
                for (var c = 0; c < cells.Length; c++)
                {
                    Grid.SetRow(cells[c], r);
                    Grid.SetColumn(cells[c], c);
                    grid.Children.Add(cells[c]);
                }
            }
            TextBlock Head(string text) => new TextBlock
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _muted, Margin = new Thickness(8, 5, 16, 5),
            };
            Row(0, (Brush)FindResource("Row"), Head("Тип"), Head("Функция"), Head("Модель / файл"), Head("Причина"));

            for (var i = 0; i < rows.Count; i++)
            {
                var (type, brush, w) = rows[i];
                var typeCell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 16, 5), VerticalAlignment = VerticalAlignment.Top };
                if (type != "Пропуск")
                    typeCell.Children.Add(new TextBlock
                    {
                        Text = ((char)0xE7BA).ToString(), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = brush,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0),
                    });
                typeCell.Children.Add(new TextBlock { Text = type, Foreground = brush, FontWeight = FontWeights.SemiBold });
                var kind = new TextBlock { Text = w.Kind, Foreground = _muted, Margin = new Thickness(8, 5, 16, 5) };
                var name = new TextBlock
                {
                    Text = w.Name.Length > 0 ? w.Name : "—", Margin = new Thickness(8, 5, 16, 5), MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = Tip(w.Full),
                };
                var reason = new TextBlock { Text = w.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 5, 8, 5), ToolTip = Tip(w.Detail) };
                ToolTipService.SetShowDuration(reason, 60000);
                Row(i + 1, Brushes.Transparent, typeCell, kind, name, reason);
            }
            return grid;
        }

        private static object Tip(string text) =>
            string.IsNullOrWhiteSpace(text) ? null : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };

        private static Button Link(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, FontSize = 12, Margin = new Thickness(0, 0, 16, 0) };
            b.SetResourceReference(StyleProperty, "LinkButton");
            b.Click += (s, e) => click();
            return b;
        }

        private void OpenJournal(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                MessageBox.Show(this, "Журнал запуска не найден: " + path, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Журнал не открылся: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
