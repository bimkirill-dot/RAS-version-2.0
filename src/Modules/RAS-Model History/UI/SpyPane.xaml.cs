using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Содержимое стыкуемой панели «RAS-Model History — журнал изменений».</summary>
    public partial class SpyPane : UserControl
    {
        /// <summary>Шире — подробности справа от списка, уже — под списком (с запасом, чтобы раскладка не «прыгала»).</summary>
        private const double WideFrom = 900;
        private const double NarrowBelow = 840;
        private const double SearchMinWidth = 200;

        private readonly MainViewModel _vm;
        private readonly UiState _state;

        /// <summary>Столбец → поле «Сортировки и группировки» (щелчок по заголовку столбца).</summary>
        private Dictionary<DataGridColumn, SortField> _sortFields;

        private bool? _wide;
        private GridLength _detailsWidth;
        private GridLength _detailsHeight;

        /// <summary>Столбцы таблицы «Изменения» по ключам (ключ хранится в ui.json).</summary>
        private readonly List<(string Key, string Label, DataGridColumn Column, bool DefaultVisible)> _columns;
        private readonly Dictionary<DataGridColumn, (DataGridLength Width, int Index)> _columnDefaults =
            new Dictionary<DataGridColumn, (DataGridLength, int)>();
        /// <summary>Столбец «Статус» текстом: что выбрал пользователь (в режиме актуализации он показывается всегда).</summary>
        private bool _statusUserVisible;
        private DataGridLength _statusUserWidth = new DataGridLength(150);
        private bool _statusForced;

        private readonly DispatcherTimer _saveTimer;
        private bool _filterDockQueued;

        public SpyPane(MainViewModel vm, UiState state)
        {
            InitializeComponent();
            _vm = vm;
            _state = state ?? new UiState();
            DataContext = vm;

            _detailsWidth = new GridLength(_state.DetailsWidth);
            _detailsHeight = new GridLength(_state.DetailsHeight);

            _columns = new List<(string, string, DataGridColumn, bool)>
            {
                ("Time", "Время", ColTime, true),
                ("Kind", "Действие", ColKind, true),
                ("Author", "Автор", ColAuthor, true),
                ("Object", "Объект", ColObject, true),
                ("Changes", "Что изменилось", ColChanges, true),
                ("SyncStatus", "Статус", ColStatus, true),
                ("Group", "Тип объекта", ColGroup, false),
                ("Category", "Категория", ColCategory, false),
                ("Type", "Тип / семейство", ColType, false),
                ("Id", "ID", ColId, false),
                ("Transaction", "Команда Revit", ColTransaction, false),
                ("Level", "Уровень", ColLevel, false),
                ("Workset", "Рабочий набор", ColWorkset, false),
                ("View", "Вид", ColView, false),
                ("Source", "Источник", ColSource, false),
                ("Creator", "Создатель элемента", ColCreator, false),
                ("Machine", "Компьютер", ColMachine, false)
            };
            foreach (var c in LogGrid.Columns) _columnDefaults[c] = (c.Width, c.DisplayIndex);
            ApplyColumnState();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                SaveUiState();
            };
            vm.RequestUiSave = QueueSave;

            vm.SaveFileDialog = defaultName =>
            {
                var dlg = new SaveFileDialog
                {
                    FileName = defaultName,
                    DefaultExt = ".csv",
                    Filter = "CSV для Excel (*.csv)|*.csv|Все файлы (*.*)|*.*"
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            };
            vm.CopyToClipboard = text =>
            {
                try { Clipboard.SetText(text); }
                catch (Exception) { /* буфер занят другим приложением */ }
            };
            vm.RevealFolder = path =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
                catch (Exception ex) { vm.Status = "Не удалось открыть папку журнала: " + ex.Message; }
            };

            IsVisibleChanged += (s, e) => vm.SetPaneVisible(IsVisible || IsDetached);
            // ширина — у корня разметки: он бывает и в панели, и в отдельном окне
            PaneRoot.SizeChanged += (s, e) => UpdateLayoutMode(e.NewSize.Width);
            MainArea.SizeChanged += (s, e) => ClampDetails();
            FilterBar.SizeChanged += (s, e) => QueueFilterDock();
            FilterGroup.SizeChanged += (s, e) => QueueFilterDock();

            // столбцы: ширина и порядок запоминаются (только когда меняется сама ширина, а не её пересчёт при раскладке)
            var widthProperty = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            foreach (var c in LogGrid.Columns)
            {
                var col = c;
                var last = (col.Width.UnitType, col.Width.Value);
                widthProperty?.AddValueChanged(col, (s, e) =>
                {
                    var now = (col.Width.UnitType, col.Width.Value);
                    if (now == last) return;
                    last = now;
                    QueueSave();
                });
            }
            LogGrid.ColumnReordered += (s, e) => QueueSave();

            // меню ячейки и заголовка, двойной щелчок, клавиши
            LogGrid.ContextMenuOpening += LogGrid_ContextMenuOpening;
            LogGrid.MouseDoubleClick += LogGrid_MouseDoubleClick;
            LogGrid.PreviewKeyDown += LogGrid_PreviewKeyDown;

            // Сортировку списка «Изменения» делает модель представления (как в спецификации Revit, с операциями и заголовками):
            // щелчок по заголовку столбца только передаёт ей поле
            _sortFields = new Dictionary<DataGridColumn, SortField>
            {
                [ColTime] = SortField.Time, [ColKind] = SortField.Kind, [ColAuthor] = SortField.Author, [ColObject] = SortField.Name,
                [ColStatus] = SortField.Status, [ParamColumn] = SortField.Param, [ColGroup] = SortField.Group,
                [ColCategory] = SortField.Category, [ColType] = SortField.TypeName, [ColId] = SortField.Id,
                [ColTransaction] = SortField.Transaction, [ColLevel] = SortField.Level, [ColWorkset] = SortField.Workset,
                [ColView] = SortField.View, [ColSource] = SortField.Source, [ColCreator] = SortField.Creator,
                [ColMachine] = SortField.Machine
            };
            LogGrid.Sorting += (s, e) =>
            {
                e.Handled = true;
                if (_sortFields.TryGetValue(e.Column, out var field)) _vm.SortByColumn(field);
            };
            vm.SortDialogRequested += OpenSortDialog;
            vm.FilterDialogRequested += OpenFilterDialog;
            vm.AskChangesView = dialog =>
            {
                var win = new ChangesViewWindow(dialog);
                OwnByRevit(win);
                return win.ShowDialog() == true;
            };
            vm.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(MainViewModel.ChangeRows):
                        ApplyGrouping();
                        // таблица сбрасывает стрелки сортировки, когда получает новый список — ставим их после неё
                        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateSortGlyphs));
                        break;
                    case nameof(MainViewModel.SortLevels):
                        UpdateSortGlyphs();
                        break;

                    case nameof(MainViewModel.ParamColumnName):
                        UpdateParamColumn();
                        break;
                    case nameof(MainViewModel.HasPurgePreview):
                        UpdateStatusColumn();
                        break;
                }
            };
            vm.ScrollRequested += row =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => BringIntoView(row)));
            UpdateParamColumn();
            UpdateStatusColumn();
            UpdateSortGlyphs();
            ApplyLayoutMode(true);
        }

        // ───────────────────────────── состояние панели (ui.json) ─────────────────────────────

        private void QueueSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Сохранить столбцы и размер подробностей (вызывается с задержкой и при выходе из Revit).</summary>
        public void SaveUiState()
        {
            if (!_statusForced && ColStatus.Width.IsAbsolute) _statusUserWidth = ColStatus.Width;
            _state.Columns = _columns.Select(c =>
            {
                var isStatus = ReferenceEquals(c.Column, ColStatus);
                var width = isStatus ? _statusUserWidth : c.Column.Width;
                return new ColumnState
                {
                    Key = c.Key,
                    Visible = isStatus ? _statusUserVisible : c.Column.Visibility == Visibility.Visible,
                    Width = width.IsAbsolute ? width.Value : 0,
                    Index = c.Column.DisplayIndex
                };
            }).ToList();
            RememberDetailsSize();
            if (_window != null) RememberWindow(_window);
            _state.DetailsWidth = _detailsWidth.IsAbsolute ? _detailsWidth.Value : 380;
            _state.DetailsHeight = _detailsHeight.IsAbsolute ? _detailsHeight.Value : 240;
            _state.Save();
        }

        private void ApplyColumnState()
        {
            var saved = (_state.Columns ?? new List<ColumnState>()).ToList();
            if (_state.Version < 2)
            {
                // ui.json прошлой версии: значок статуса стал столбцом «Значок + текст», время — длиннее
                var textStatusVisible = saved.Any(x => x.Key == "Status" && x.Visible);
                saved = saved
                    .Where(x => x.Key != "Status")
                    .Select(x => x.Key == "StatusIcon"
                        ? new ColumnState { Key = "SyncStatus", Visible = x.Visible || textStatusVisible, Index = x.Index }
                        : x.Key == "Time" ? new ColumnState { Key = x.Key, Visible = x.Visible, Index = x.Index } : x)
                    .ToList();
            }
            _statusUserWidth = ColStatus.Width;
            foreach (var c in _columns)
            {
                var s = saved.FirstOrDefault(x => x.Key == c.Key);
                var visible = s?.Visible ?? c.DefaultVisible;
                if (ReferenceEquals(c.Column, ColObject)) visible = true; // без «Объекта» таблица теряет смысл
                if (ReferenceEquals(c.Column, ColStatus))
                {
                    _statusUserVisible = visible;
                    if (s != null && s.Width > 0) _statusUserWidth = new DataGridLength(s.Width);
                    ColStatus.Width = _statusUserWidth;
                    continue; // видимость задаёт UpdateStatusColumn
                }
                c.Column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (s != null && s.Width > 0 && c.Column.CanUserResize) c.Column.Width = new DataGridLength(s.Width);
            }
            if (saved.Count == 0) return;
            // порядок: сохранённые столбцы по их местам, остальные (в том числе столбец параметра) — следом
            var order = LogGrid.Columns
                .Select(col => new
                {
                    Column = col,
                    Saved = saved.FirstOrDefault(x => _columns.Any(c => c.Key == x.Key && ReferenceEquals(c.Column, col)))
                })
                .OrderBy(x => x.Saved?.Index ?? int.MaxValue)
                .ThenBy(x => x.Column.DisplayIndex)
                .Select(x => x.Column)
                .ToList();
            try
            {
                for (int i = 0; i < order.Count; i++) order[i].DisplayIndex = i;
            }
            catch (Exception ex)
            {
                Diag.Log("Column order restore failed", ex);
            }
        }

        private void ResetColumns()
        {
            foreach (var c in _columns)
            {
                var d = _columnDefaults[c.Column];
                if (ReferenceEquals(c.Column, ColStatus))
                {
                    _statusUserVisible = c.DefaultVisible;
                    _statusUserWidth = d.Width;
                    if (!_statusForced) ColStatus.Width = d.Width;
                    continue;
                }
                c.Column.Visibility = c.DefaultVisible ? Visibility.Visible : Visibility.Collapsed;
                c.Column.Width = d.Width;
            }
            ParamColumn.Width = _columnDefaults[ParamColumn].Width;
            var order = LogGrid.Columns.OrderBy(col => _columnDefaults[col].Index).ToList();
            for (int i = 0; i < order.Count; i++) order[i].DisplayIndex = i;
            UpdateStatusColumn();
            QueueSave();
        }

        // ───────────────────────────── меню ─────────────────────────────

        /// <summary>Меню живёт вне дерева панели — даём ему стили и цвета темы.</summary>
        private static void PrepareMenu(ContextMenu menu)
        {
            if (menu.Tag as string == "themed") return;
            menu.Resources.MergedDictionaries.Add(new ThemeResources());
            if (menu.TryFindResource(typeof(ContextMenu)) is Style style) menu.Style = style;
            menu.Tag = "themed";
        }

        /// <summary>Кнопка «⋯»: открыть меню под кнопкой.</summary>
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null) return;
            var menu = button.ContextMenu;
            PrepareMenu(menu);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }

        private void LogGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = LogGrid.ContextMenu;
            if (menu == null) return;
            PrepareMenu(menu);
            menu.DataContext = DataContext;
            menu.Items.Clear();
            var source = e.OriginalSource as DependencyObject;
            var header = FindParent<DataGridColumnHeader>(source);
            if (header != null)
            {
                BuildColumnMenu(menu);
                return;
            }
            var cell = FindParent<DataGridCell>(source);
            var row = cell?.DataContext as ChangeRow ?? FindParent<DataGridRow>(source)?.DataContext as ChangeRow;
            if (row == null)
            {
                e.Handled = true; // пустое место таблицы — меню не нужно
                return;
            }
            if (!ReferenceEquals(_vm.SelectedRow, row)) _vm.SelectedRow = row;
            BuildRowMenu(menu, row, cell?.Column);
        }

        private void BuildRowMenu(ContextMenu menu, ChangeRow row, DataGridColumn column)
        {
            menu.Items.Add(Item("Показать в модели", _vm.ShowInModelCommand));
            if (row.IsGroupHeader)
                menu.Items.Add(Item(row.IsExpanded ? "Свернуть операцию" : "Раскрыть операцию", _vm.ToggleGroupCommand, row));
            menu.Items.Add(Item("История этого элемента", _vm.HistoryCommand));
            menu.Items.Add(Item(row.IsGroupHeader ? "Копировать ID элементов" : "Копировать ID", _vm.CopyIdCommand));
            if (row.IsExcluded) menu.Items.Add(MenuAction("Вернуть в журнал (у всех пользователей)", () => _vm.RestoreRecords(row)));

            var filters = ValueItems(row, column).ToList();
            if (filters.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var f in filters) menu.Items.Add(f);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Выделить все из списка в модели", _vm.SelectAllInModelCommand));
            menu.Items.Add(Item("Экспорт в CSV…", _vm.ExportCommand));
        }

        /// <summary>«Равно / Не равно» по значению ячейки, на которой щёлкнули, — как правила фильтров Revit.</summary>
        private IEnumerable<MenuItem> ValueItems(ChangeRow row, DataGridColumn column)
        {
            var r = row.Record;
            if (column == null || r.Kind == ChangeKind.Event) yield break;
            if (ReferenceEquals(column, ColKind))
            {
                if (row.IsMixed) yield break;
                var label = ChangeRow.KindLabel(r.Kind);
                yield return MenuAction($"Равно: Действие «{label}»", () => _vm.FilterByKind(r.Kind, false));
                yield return MenuAction($"Не равно: Действие «{label}»", () => _vm.FilterByKind(r.Kind, true));
                yield break;
            }
            if (ReferenceEquals(column, ColGroup))
            {
                if (row.IsGroupHeader) yield break;
                var group = row.GroupText;
                yield return MenuAction($"Равно: Тип объекта «{group}»", () => _vm.FilterByGroup(r.Group));
                yield return MenuAction($"Не равно: Тип объекта «{group}»", () => _vm.AddCellCondition(FilterField.Group, group, true));
                yield break;
            }
            FilterField? field = null;
            string value = null;
            if (ReferenceEquals(column, ColAuthor)) { field = FilterField.Author; value = row.AuthorText; }
            else if (!row.IsGroupHeader && (ReferenceEquals(column, ColCategory) || ReferenceEquals(column, ColObject)))
            { field = FilterField.Category; value = r.Category; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColType)) { field = FilterField.TypeName; value = r.TypeName; }
            else if (ReferenceEquals(column, ColTransaction)) { field = FilterField.Transaction; value = r.Transaction; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColLevel)) { field = FilterField.Level; value = r.Level; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColWorkset)) { field = FilterField.Workset; value = r.Workset; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColView)) { field = FilterField.View; value = r.OwnerView; }
            else if (ReferenceEquals(column, ColMachine)) { field = FilterField.Machine; value = r.Machine; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColCreator)) { field = FilterField.Creator; value = r.Creator; }
            else if (!row.IsGroupHeader && ReferenceEquals(column, ColStatus) && !row.IsExcluded && row.PreviewReason == null)
            { field = FilterField.Status; value = row.StatusLabel; }
            else if (ReferenceEquals(column, ColSource)) { field = FilterField.Source; value = ChangeRow.SourceLabel(r.Source); }
            if (field == null || string.IsNullOrWhiteSpace(value)) yield break;
            var name = RecordFilter.FieldLabel(field.Value);
            var shown = RecordFilter.Short(value, 40);
            var f = field.Value;
            var v = value;
            yield return MenuAction($"Равно: {name} «{shown}»", () => _vm.AddCellCondition(f, v, false));
            yield return MenuAction($"Не равно: {name} «{shown}»", () => _vm.AddCellCondition(f, v, true));
        }

        /// <summary>Правый щелчок по заголовку: какие столбцы показывать.</summary>
        private void BuildColumnMenu(ContextMenu menu)
        {
            foreach (var c in _columns)
            {
                if (ReferenceEquals(c.Column, ColObject)) continue;
                var col = c.Column;
                var isStatus = ReferenceEquals(col, ColStatus);
                var item = new MenuItem
                {
                    Header = c.Label,
                    IsCheckable = true,
                    IsChecked = isStatus ? _statusUserVisible : col.Visibility == Visibility.Visible,
                    StaysOpenOnClick = true
                };
                item.Click += (s, e) =>
                {
                    var show = item.IsChecked;
                    if (isStatus)
                    {
                        _statusUserVisible = show;
                        UpdateStatusColumn();
                    }
                    else
                    {
                        col.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    }
                    QueueSave();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Столбец параметра…", () =>
            {
                _vm.IsMoreOpen = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ParamCombo.Focus()));
            }, "Показать в таблице значение любого параметра, в том числе у удалённых элементов"));
            menu.Items.Add(MenuAction("Вернуть столбцы по умолчанию", ResetColumns));
        }

        private static MenuItem Item(string header, ICommand command, object parameter = null) =>
            new MenuItem { Header = header, Command = command, CommandParameter = parameter };

        private static MenuItem MenuAction(string header, Action action, string toolTip = null)
        {
            var item = new MenuItem { Header = header };
            if (toolTip != null) item.ToolTip = toolTip;
            item.Click += (s, e) => action();
            return item;
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T found) return found;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ───────────────────────────── таблица ─────────────────────────────

        /// <summary>Двойной щелчок по строке (не по заголовку): операцию раскрыть, запись — показать в модели.</summary>
        private void LogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindParent<DataGridColumnHeader>(source) != null || FindParent<ButtonBase>(source) != null) return;
            if (FindParent<DataGridRow>(source) == null) return;
            if (_vm.RowActivatedCommand.CanExecute(null)) _vm.RowActivatedCommand.Execute(null);
        }

        /// <summary>→ раскрывает операцию, ← сворачивает, Enter — как двойной щелчок.</summary>
        private void LogGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var row = _vm.SelectedRow;
            if (row == null) return;
            if (e.Key == Key.Enter)
            {
                _restoreFocus = row.IsGroupHeader; // раскрытие операции перестраивает список — фокус вернём на строку
                _vm.RowActivatedCommand.Execute(null);
                e.Handled = true;
            }
            else if (row.IsGroupHeader && ((e.Key == Key.Right && !row.IsExpanded) || (e.Key == Key.Left && row.IsExpanded)))
            {
                _restoreFocus = true; // после обновления списка вернуть фокус на ту же строку, чтобы ↑/↓ продолжали работать
                _vm.ToggleGroupCommand.Execute(row);
                e.Handled = true;
            }
        }

        private bool _restoreFocus;

        /// <summary>
        /// Показать строку в видимой части списка — только по запросу (раскрытие операции, переход из «Истории»),
        /// а не при каждом обновлении, чтобы не дёргать прокрутку. После клавиш ←/→/Enter возвращает фокус на ячейку.
        /// </summary>
        private void BringIntoView(ChangeRow row)
        {
            var focus = _restoreFocus;
            _restoreFocus = false;
            var grid = _vm.IsChangesView ? LogGrid : EventGrid;
            if (row == null || !grid.IsVisible) return;
            try
            {
                grid.ScrollIntoView(row);
                if (!focus || !(grid.IsKeyboardFocusWithin || Keyboard.FocusedElement == null)) return;
                grid.UpdateLayout();
                var column = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
                if (column == null || !(grid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)) return;
                grid.CurrentCell = new DataGridCellInfo(row, column);
                (column.GetCellContent(container)?.Parent as DataGridCell)?.Focus();
            }
            catch (Exception)
            {
                // строка ушла из списка, пока ждали отрисовки
            }
        }

        /// <summary>Двойной щелчок по ▸ — это не два переключения подряд.</summary>
        private void ExpandButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) e.Handled = true;
        }

        /// <summary>Столбец «значение параметра»: заголовок = имя параметра, скрыт, если параметр не выбран.</summary>
        private void UpdateParamColumn()
        {
            ParamColumn.Header = _vm.HasParamColumn ? _vm.ParamColumnName : "Параметр";
            ParamColumn.Visibility = _vm.HasParamColumn ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>В режиме актуализации столбец «Статус» показывает причину несоответствия — и виден всегда.</summary>
        private void UpdateStatusColumn()
        {
            if (_vm.HasPurgePreview)
            {
                if (!_statusForced) _statusUserWidth = ColStatus.Width;
                _statusForced = true;
                ColStatus.Header = "Причина";
                ColStatus.Width = new DataGridLength(300);
                ColStatus.Visibility = Visibility.Visible;
                return;
            }
            if (_statusForced) ColStatus.Width = _statusUserWidth;
            _statusForced = false;
            ColStatus.Header = "Статус";
            ColStatus.Visibility = _statusUserVisible ? Visibility.Visible : Visibility.Collapsed;
            if (_statusUserVisible) _statusUserWidth = ColStatus.Width;
        }

        /// <summary>Стрелки в заголовках столбцов, по которым идёт сортировка (на всех уровнях).</summary>
        private void UpdateSortGlyphs()
        {
            foreach (var column in LogGrid.Columns)
            {
                var level = _sortFields.TryGetValue(column, out var f) ? _vm.SortLevels.FirstOrDefault(l => l.Field == f) : null;
                column.SortDirection = level == null
                    ? (ListSortDirection?)null
                    : level.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }
        }

        /// <summary>
        /// Заголовки групп «Сортировки и группировки»: по уровню группировки таблицы на каждый уровень с «Заголовком».
        /// Число уровней берётся из самих строк — модель представления раскладывает их по группам при каждом обновлении.
        /// </summary>
        private void ApplyGrouping()
        {
            var rows = _vm.ChangeRows;
            if (!(CollectionViewSource.GetDefaultView(rows) is ICollectionView view)) return;
            var first = rows.Count > 0 ? rows[0] : null;
            var levels = first == null ? 0 : first.Group4 != null ? 4 : first.Group3 != null ? 3 : first.Group2 != null ? 2 : first.Group1 != null ? 1 : 0;
            if (view.GroupDescriptions.Count == levels) return;
            try
            {
                using (view.DeferRefresh())
                {
                    view.GroupDescriptions.Clear();
                    for (int i = 0; i < levels; i++) view.GroupDescriptions.Add(new PropertyGroupDescription("Group" + (i + 1)));
                }
            }
            catch (Exception ex)
            {
                Diag.Log("Grouping failed", ex);
            }
        }

        /// <summary>Окно плагина — поверх главного окна Revit (по центру него, не теряется за ним).</summary>
        private static void OwnByRevit(Window win)
        {
            try
            {
                var owner = App.Instance?.MainWindowHandle ?? IntPtr.Zero;
                if (owner == IntPtr.Zero) owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(win).Owner = owner;
            }
            catch (Exception) { /* окно без владельца — тоже можно */ }
        }

        /// <summary>Кнопка «Сортировка…»: окно как «Сортировка/группирование» в свойствах спецификации Revit.</summary>
        private void OpenSortDialog()
        {
            try
            {
                var dvm = new SortViewModel(_vm.SortLevels, _vm.GroupByOperation, _vm.HasParamColumn ? _vm.ParamColumnName : null);
                var win = new SortWindow(dvm);
                OwnByRevit(win);
                if (win.ShowDialog() == true) _vm.SetSort(dvm.Result, dvm.GroupByOperation);
            }
            catch (Exception ex)
            {
                Diag.Log("Sort dialog failed", ex);
                _vm.Status = "Не удалось открыть окно сортировки: " + ex.Message;
            }
        }

        /// <summary>Кнопка «Фильтр»: условия, как в фильтрах Revit.</summary>
        private void OpenFilterDialog()
        {
            try
            {
                var dvm = new FilterViewModel(_vm.CurrentFilter, _vm.ParamNames, _vm.FilterValues, _vm.FilterPreviewText);
                var win = new FilterWindow(dvm);
                OwnByRevit(win);
                if (win.ShowDialog() == true) _vm.SetFilter(dvm.Result);
            }
            catch (Exception ex)
            {
                Diag.Log("Filter dialog failed", ex);
                _vm.Status = "Не удалось открыть окно фильтра: " + ex.Message;
            }
        }

        // ───────────────────────────── раскладка ─────────────────────────────

        private void UpdateLayoutMode(double width)
        {
            if (width <= 0) return;
            var wide = _wide ?? width >= WideFrom;
            if (_wide == true && width < NarrowBelow) wide = false;
            else if (_wide == false && width >= WideFrom) wide = true;
            if (wide != _wide) ApplyLayoutMode(wide);
        }

        private void RememberDetailsSize()
        {
            var cols = MainArea.ColumnDefinitions;
            var rows = MainArea.RowDefinitions;
            if (_wide == true && cols[2].Width.IsAbsolute) _detailsWidth = cols[2].Width;
            if (_wide == false && rows[2].Height.IsAbsolute) _detailsHeight = rows[2].Height;
        }

        /// <summary>
        /// Широкая панель (по умолчанию она пристыкована снизу): подробности справа от списка.
        /// Узкая (пристыкована сбоку): подробности под списком.
        /// Размер подробностей, выбранный разделителем, запоминается для каждой раскладки.
        /// </summary>
        private void ApplyLayoutMode(bool wide)
        {
            var cols = MainArea.ColumnDefinitions;
            var rows = MainArea.RowDefinitions;
            if (_wide != null && _wide != wide) QueueSave();
            RememberDetailsSize();
            _wide = wide;

            if (wide)
            {
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[0].MinWidth = 240;
                cols[1].Width = new GridLength(5);
                cols[2].Width = _detailsWidth;
                cols[2].MinWidth = 260;
                rows[0].Height = new GridLength(1, GridUnitType.Star);
                rows[0].MinHeight = 100;
                rows[1].Height = new GridLength(0);
                rows[2].Height = new GridLength(0);
                rows[2].MinHeight = 0;
                Place(DetailsSplitter, 0, 1);
                Place(DetailsHost, 0, 2);
                DetailsSplitter.ResizeDirection = GridResizeDirection.Columns;
                DetailsHost.Margin = new Thickness(8, 0, 0, 0);
            }
            else
            {
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[0].MinWidth = 0;
                cols[1].Width = new GridLength(0);
                cols[2].Width = new GridLength(0);
                cols[2].MinWidth = 0;
                rows[0].Height = new GridLength(1, GridUnitType.Star);
                rows[0].MinHeight = 100;
                rows[1].Height = new GridLength(5);
                rows[2].Height = _detailsHeight;
                rows[2].MinHeight = 80;
                Place(DetailsSplitter, 1, 0);
                Place(DetailsHost, 2, 0);
                DetailsSplitter.ResizeDirection = GridResizeDirection.Rows;
                DetailsHost.Margin = new Thickness(0, 8, 0, 0);
            }
            ClampDetails();
        }

        /// <summary>Подробности не шире (не выше) свободного места: размер мог остаться от более широкой панели.</summary>
        private void ClampDetails()
        {
            if (_wide == true)
            {
                var col = MainArea.ColumnDefinitions[2];
                var max = Math.Max(260, MainArea.ActualWidth - 240 - 5);
                if (MainArea.ActualWidth > 0 && col.Width.IsAbsolute && col.Width.Value > max) col.Width = new GridLength(max);
            }
            else if (_wide == false)
            {
                var row = MainArea.RowDefinitions[2];
                var max = Math.Max(80, MainArea.ActualHeight - 100 - 5);
                if (MainArea.ActualHeight > 0 && row.Height.IsAbsolute && row.Height.Value > max) row.Height = new GridLength(max);
            }
        }

        private static void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }

        private void QueueFilterDock()
        {
            if (_filterDockQueued) return;
            _filterDockQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _filterDockQueued = false;
                UpdateFilterDock();
            }));
        }

        /// <summary>
        /// Фильтры справа от поиска, если им хватает места в одну строку (и поиску остаётся не меньше 200 px),
        /// иначе — строкой под поиском. Ширина считается по самим фильтрам: она зависит от имени автора и подписи «Действие».
        /// </summary>
        private void UpdateFilterDock()
        {
            if (FilterBar.ActualWidth <= 0) return;
            double need = 0;
            foreach (UIElement child in FilterGroup.Children) need += child.DesiredSize.Width;
            var right = FilterBar.ActualWidth >= need + 8 + SearchMinWidth;
            var dock = right ? Dock.Right : Dock.Bottom;
            if (DockPanel.GetDock(FilterGroup) == dock) return;
            DockPanel.SetDock(FilterGroup, dock);
            FilterGroup.Margin = right ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        }
    }
}
