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

        /// <summary>«↻ Повторить ошибки»: строка, её последний запуск и файлы с ошибками; null — не нажимали.</summary>
        public ExportTask RetryTask { get; private set; }
        public RowRun RetryRun { get; private set; }
        public HashSet<string> RetryPaths { get; private set; }

        /// <param name="start">Строка, на которой открыть; null — «Все задачи».</param>
        public WarningsWindow(IList<ExportTask> roots, Dictionary<ExportTask, WarningGroup> groups, ExportTask start)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _roots = roots;
            _groups = groups;
            _error = (Brush)FindResource("Error");
            _changed = (Brush)FindResource("MarkChanged");
            _muted = (Brush)FindResource("Muted");

            var views = new List<RowView> { new RowView { Name = "Все задачи", Weight = FontWeights.SemiBold, Indent = new Thickness(0) } };
            Count(views[0], roots.SelectMany(r => r.Self()));
            foreach (var t in roots.SelectMany(r => r.Self()))
            {
                var level = 0;
                for (var p = t.Parent; p != null; p = p.Parent) level++;
                var v = new RowView
                {
                    Task = t, Name = string.IsNullOrWhiteSpace(t.Name) ? "Без названия" : t.Name, Indent = new Thickness(level * 16 + 10, 0, 0, 0),
                    Weight = level == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                Count(v, t.Self());
                views.Add(v);
            }
            Rows.ItemsSource = views;
            Rows.SelectedItem = views.FirstOrDefault(v => v.Task == start && start != null) ?? views[0];
            _ready = true;
            ShowSelected();
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
            var picked = (Rows.SelectedItem as RowView)?.Task;
            var tasks = picked == null ? _roots.SelectMany(r => r.Self()) : picked.Self();
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
            var box = new StackPanel { Margin = new Thickness(0, first ? 0 : 22, 0, 0) };
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

            if (errors.Count > 0)
            {
                box.Children.Add(Section("Ошибки", errors.Count, _error));
                foreach (var e in errors) box.Children.Add(ErrorLine(e));
            }
            if (changes.Count > 0 || unknown)
            {
                box.Children.Add(Section("Изменения", changes.Count, _changed));
                foreach (var c in changes)
                    box.Children.Add(Line(c.Name, "модель изменилась после выгрузки NWC — выгрузить заново", c.Full, null));
                if (unknown)
                    box.Children.Add(new TextBlock
                    {
                        Text = "Дату части моделей Revit Server не сообщил — изменились ли они, не известно.", Foreground = _muted, FontSize = 12,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 2, 0, 0),
                    });
            }
            if (skips.Count > 0)
            {
                box.Children.Add(Section("Пропущено", skips.Count, _muted));
                foreach (var s in skips) box.Children.Add(Line(s.Text, s.Name, s.Detail, s.Full, muted: true));
            }
            Details.Children.Add(box);
        }

        // заголовок раздела: значок и «Ошибки — 2» цветом раздела
        private UIElement Section(string title, int count, Brush brush)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
            p.Children.Add(new TextBlock
            {
                Text = ((char)0xE7BA).ToString(), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
            });
            p.Children.Add(new TextBlock
            {
                Text = count > 0 ? title + " — " + count : title, FontWeight = FontWeights.SemiBold, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return p;
        }

        // ошибка: функция (бледно), имя (коротко, полное — в подсказке), причина (первая фраза, целиком — в подсказке)
        private UIElement ErrorLine(WarningLine e)
        {
            var grid = new Grid { Margin = new Thickness(22, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 120 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = e.Kind, Foreground = _muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
            var name = new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0), ToolTip = Tip(e.Full) };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var text = new TextBlock { Text = e.Text, TextWrapping = TextWrapping.Wrap, ToolTip = Tip(e.Detail) };
            ToolTipService.SetShowDuration(text, 60000);
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
            return grid;
        }

        // строка изменения или пропуска: главное и пояснение бледно
        private UIElement Line(string main, string note, string mainTip, string noteTip, bool muted = false)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 2, 0, 2) };
            t.Inlines.Add(new System.Windows.Documents.Run(main) { FontWeight = muted ? FontWeights.Normal : FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(note))
                t.Inlines.Add(new System.Windows.Documents.Run(" — " + note) { Foreground = _muted });
            var tip = string.Join("\n\n", new[] { mainTip, noteTip }.Where(x => !string.IsNullOrWhiteSpace(x) && x != main));
            if (tip.Length > 0)
            {
                t.ToolTip = Tip(tip);
                ToolTipService.SetShowDuration(t, 60000);
            }
            return t;
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
