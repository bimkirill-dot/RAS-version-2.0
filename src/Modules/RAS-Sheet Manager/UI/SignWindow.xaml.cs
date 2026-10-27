using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using RASv2.SheetManager.Logic;
using RASv2.SheetManager.Signing;

namespace RASv2.SheetManager.UI
{
    /// <summary>
    /// «Подпись» (DECISIONS.md модуля): папка с подписями → новые очищаются (AutoCAD Core Console, в фоне, несколько сразу),
    /// очищенные не трогаются → список с картинками, фамилией (можно поправить), что будет, размерами → «Добавить».
    /// Таблица выбора и форма формулы — из самого семейства. «Актуализировать» убирает из семейства подписи, файлов которых
    /// нет в папке, и вставленные вручную. Добавляет команда (SignApp) после закрытия окна — одной группой действий.
    /// </summary>
    public partial class SignWindow : Window
    {
        private const string NewTable = "Новая таблица выбора…";
        private const string NewTableName = "Таблица выбора фамилий";
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private readonly SignSession _s;
        private readonly ObservableCollection<SignRow> _rows = new ObservableCollection<SignRow>();
        private CancellationTokenSource _cancel;
        private bool _busy;
        private bool _replanning;
        /// <summary>Таблица, для которой заполнены поля формулы (сменили таблицу — форма заново из семейства).</summary>
        private string _formTable;
        private bool _formFilling;

        public SignWindow(SignSession session)
        {
            // кисти и образцы ядра — до разметки: на них ссылаются стили окна
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _s = session;
            FamilyName.Text = session.Family.Title;
            List.ItemsSource = _rows;
            CollectionViewSource.GetDefaultView(_rows).Filter = o => Visible((SignRow)o);
            ScaleBox.Text = session.Personal.ScaleFor(session.Family.Title).ToString("0.###", Ru);
            SetPoint();
            FillTables();
            ColumnBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((o, e) => FormChanged()));
            KeyBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((o, e) => FormChanged()));
            DefaultBox.TextChanged += (o, e) => FormChanged();
            var folder = !string.IsNullOrEmpty(session.Personal.LastFolder) && Directory.Exists(session.Personal.LastFolder)
                ? session.Personal.LastFolder
                : session.Settings.Folder;
            FolderBox.Text = folder ?? "";
            Loaded += (o, e) => { if (Directory.Exists(FolderBox.Text)) Process(); else Counts(); };
            Closing += (o, e) => _cancel?.Cancel();
        }

        /// <summary>Центр подписи: как у остальных (если подписи есть), в начало координат, указать на виде — выбрать можно всегда.</summary>
        private void SetPoint()
        {
            if (_s.Anchor != null)
            {
                PointSame.Visibility = Visibility.Visible;
                PointSame.Content = string.Format(Ru, "как у остальных подписей ({0:0.0}; {1:0.0} мм)", _s.Anchor.X * 304.8, _s.Anchor.Y * 304.8);
                PointSame.ToolTip = _s.AnchorSpread > 0 ? "У " + _s.AnchorSpread + " подписей точка другая — взята самая частая" : null;
                PointSame.IsChecked = true;
            }
            else
            {
                PointSame.Visibility = Visibility.Collapsed;
                PointOrigin.IsChecked = true;
            }
        }

        // ───────────────────────── таблица выбора и формула ─────────────────────────

        private void FillTables()
        {
            var items = _s.Tables.Select(t => t.Name + (t.Formulas > 0 ? "  — формул: " + t.Formulas : "  — формулы на неё не ссылаются")).ToList();
            items.Add(NewTable);
            TableBox.ItemsSource = items;
            TableBox.SelectedIndex = 0;
        }

        private string ChosenTable() =>
            TableBox.SelectedIndex >= 0 && TableBox.SelectedIndex < _s.Tables.Count
                ? _s.Tables[TableBox.SelectedIndex].Name
                : SignNames.Clean((NewTableBox.Text ?? "").Trim()).Trim('"', '/', '*', '?');

        private void Table_Changed(object sender, SelectionChangedEventArgs e)
        {
            var isNew = TableBox.SelectedIndex >= _s.Tables.Count;
            NewTableBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            if (isNew && string.IsNullOrWhiteSpace(NewTableBox.Text))
                NewTableBox.Text = _s.Tables.Any(t => t.Name == NewTableName) ? NewTableName + "_2" : NewTableName;
            ReadFamily();
        }

        private void NewTable_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ReadFamily();

        private void FormChanged()
        {
            if (_formFilling) return;
            ReadFamily();
        }

        /// <summary>
        /// Семейство с выбранной таблицей и формой формулы (строки таблицы, параметры, вставленные DWG) — и новый план. Сменили
        /// таблицу — форма формулы заново из семейства (у семейства ГИП номер — из своего столбца).
        /// </summary>
        private void ReadFamily()
        {
            if (_s.Family == null || ColumnBox == null) return;
            var table = ChosenTable();
            if (string.IsNullOrEmpty(table)) return;
            try
            {
                if (table != _formTable)
                {
                    var rows = _s.Family.ReadTable(table, "Фамилия", "Да/Нет", out _, out _);
                    var form = _s.Family.FormFor(table, _s.Tables, rows);
                    _formFilling = true;
                    ColumnBox.ItemsSource = (rows?.NumberColumns() ?? new List<string>()).Union(new[] { form.Column }).Distinct().ToList();
                    ColumnBox.Text = form.Column;
                    KeyBox.ItemsSource = _s.Family.TextParams().Union(new[] { form.Key }).Distinct().ToList();
                    KeyBox.Text = form.Key;
                    DefaultBox.Text = form.Default;
                    _formFilling = false;
                    _formTable = table;
                    var c = _s.Tables.FirstOrDefault(t => t.Name == table);
                    FormNote.Text = c != null && c.Formulas > 0 ? "как в " + c.Formulas + " формулах" : "формул нет — проверьте";
                }
                var f = new SignForm
                {
                    Column = (ColumnBox.Text ?? "").Trim(),
                    Key = (KeyBox.Text ?? "").Trim(),
                    Default = (DefaultBox.Text ?? "").Trim().Replace(',', '.'),
                };
                if (f.Column.Length == 0 || f.Key.Length == 0 || f.Default.Length == 0) { ShowError("Заполните формулу: столбец, параметр и значение, если фамилии нет."); return; }
                _s.Table = table;
                _s.State = _s.Family.State(table, f, out _s.SourceEncoding, out _s.Backup);
                _s.TableExisted = _s.Backup != null; // таблица есть в семействе — её выгрузили
                var unused = _s.Family.UnusedTables(table);
                RemoveUnused.Visibility = unused.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RemoveUnused.Content = "Удалить таблицы выбора, на которые не ссылается ни одна формула: " + string.Join(", ", unused);
                var extras = _s.State.Rows.ExtraColumns().Select(_s.State.Rows.ColumnName).ToList();
                ExtraHeader.Text = string.Join(" · ", extras);
                ExtraHeader.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                HideError();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                ShowError("Не удалось прочитать таблицу выбора «" + table + "»: " + ex.Message);
            }
            Replan();
        }

        // ───────────────────────── папка и очистка ─────────────────────────

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка с исходными подписями (DWG)", ShowNewFolderButton = false })
            {
                if (Directory.Exists(FolderBox.Text)) dialog.SelectedPath = FolderBox.Text;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                FolderBox.Text = dialog.SelectedPath;
            }
            Process();
        }

        private void Folder_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Process();
        }

        private void Folder_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!string.Equals(FolderBox.Text.Trim(), _s.Folder, StringComparison.OrdinalIgnoreCase)) Process();
        }

        /// <summary>
        /// Очистить новые подписи папки и прочитать очищенные — в фоне; потом список. reclean — исходные, которые очистить заново
        /// («Обновить»). Имя файла не по регламенту — подпись не чистится и не добавляется.
        /// </summary>
        private async void Process(ISet<string> reclean = null)
        {
            var folder = FolderBox.Text.Trim().Trim('"');
            if (string.Equals(folder, _s.Folder, StringComparison.OrdinalIgnoreCase) && _busy) return;
            _cancel?.Cancel();
            _s.Folder = folder;
            _rows.Clear();
            if (!Directory.Exists(folder))
            {
                StatusText.Text = folder.Length == 0 ? "Укажите папку с подписями." : "Папки нет: " + folder;
                Counts();
                return;
            }
            var acad = AcadConsole.Find(_s.Settings.Acad);
            var options = _s.Settings.Clean;
            var settings = _s.Settings;
            var cancel = _cancel = new CancellationTokenSource();
            _busy = true;
            Counts();
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 0;
            StatusText.Text = "Читаю папку…";
            var parallel = Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2));
            List<SignFile> files;
            try
            {
                files = await Task.Run(() => SignFolder.Process(folder, options, acad, _s.WorkRoot, _s.PreviewCache, parallel,
                    (done, total) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (cancel.IsCancellationRequested) return;
                        Progress.Maximum = Math.Max(1, total);
                        Progress.Value = done;
                        StatusText.Text = "Очистка и чтение подписей: " + done + " из " + total;
                    })), cancel.Token, reclean, name => settings.CheckName(name).Problem));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is AggregateException)
            {
                if (cancel.IsCancellationRequested) return;
                _busy = false;
                Progress.Visibility = Visibility.Collapsed;
                StatusText.Text = "Папка не читается: " + (ex.InnerException ?? ex).Message;
                Counts();
                return;
            }
            if (cancel.IsCancellationRequested) return;
            _busy = false;
            Progress.Visibility = Visibility.Collapsed;
            var boxW = Math.Max(1, files.Where(f => f.Lines != null).Select(f => f.WidthMm).DefaultIfEmpty(1).Max());
            var boxH = Math.Max(1, files.Where(f => f.Lines != null).Select(f => f.HeightMm).DefaultIfEmpty(1).Max());
            var ink = new SolidColorBrush(Color.FromRgb(SignSettings.SignColor[0], SignSettings.SignColor[1], SignSettings.SignColor[2]));
            ink.Freeze();
            foreach (var f in files)
            {
                var name = SignNames.FromFile(Path.GetFileName(f.Source), _s.Settings.FileWord, out var note);
                var refreshed = reclean != null && reclean.Contains(f.Source) && f.CleanedNow;
                _rows.Add(new SignRow(f, name, note, Replan) { Preview = SignRow.Draw(f.Lines, boxW, boxH, ink), Refreshed = refreshed });
            }
            _s.CleanedNow = files.Count(f => f.CleanedNow);
            var errors = files.Count(f => f.Error != null);
            var wrong = files.Count(f => f.NameProblem != null);
            var outdated = files.Count(f => f.Outdated);
            StatusText.Text = "Подписей в папке: " + files.Count +
                              (_s.CleanedNow > 0 ? (reclean != null ? " · очищено заново: " : " · очищено сейчас: ") + _s.CleanedNow : "") +
                              (errors > 0 ? " · не очищено: " + errors : "") +
                              (wrong > 0 ? " · не по регламенту: " + wrong : "") +
                              (outdated > 0 ? " · по прежним настройкам: " + outdated : "") +
                              (acad == null && files.Any(f => !File.Exists(f.Clean)) ? " · AutoCAD не найден — новые подписи не очистить (путь — в настройках)" : "");
            // разработчик, 27.10.2026: заметное, «чтобы страшно было» — на случай, если подписи будут вставлять вручную
            NamingBanner.Visibility = wrong > 0 ? Visibility.Visible : Visibility.Collapsed;
            NamingText.Text = "!!! Названы не по регламенту: " + wrong + ". Такие подписи в семейство не добавляются, а уже добавленные — " +
                              "вместе с вставленными вручную — удаляются из семейства при актуализации !!!";
            if (_s.State == null) ReadFamily();
            else Replan();
            // заново очищенные, уже стоящие в семействе, отмечены для замены — показать их
            if (_rows.Any(r => r.Refreshed && r.IsAdded)) ShowAdded.IsChecked = true;
            if (wrong > 0 && reclean == null)
            {
                var list = string.Join(Environment.NewLine, files.Where(f => f.NameProblem != null).Take(8)
                    .Select(f => "• " + Path.GetFileName(f.Source) + " — " + f.NameProblem)) + (wrong > 8 ? Environment.NewLine + "… и ещё " + (wrong - 8) : "");
                var answer = MessageBox.Show(this,
                    "Названы не по регламенту: " + wrong + Environment.NewLine + Environment.NewLine + list + Environment.NewLine + Environment.NewLine +
                    "Такие подписи не будут добавлены в семейство. Открыть регламент наименования подписей?",
                    "Подпись", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) OpenRegulation();
            }
        }

        // ───────────────────────── «Обновить» ─────────────────────────

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            var menu = RefreshButton.ContextMenu;
            RefreshAll.Header = "Все подписи в папке (" + _rows.Count(r => r.File.NameProblem == null) + ")";
            var chosen = _rows.Count(r => r.Checked);
            RefreshChecked.Header = "Отмеченные (" + chosen + ")";
            RefreshChecked.IsEnabled = chosen > 0;
            menu.PlacementTarget = RefreshButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RefreshAll_Click(object sender, RoutedEventArgs e) => Reclean(_rows.Where(r => r.File.NameProblem == null).ToList());

        private void RefreshChecked_Click(object sender, RoutedEventArgs e) => Reclean(_rows.Where(r => r.Checked && r.File.NameProblem == null).ToList());

        /// <summary>Очистить заново по текущим настройкам (настройки поменялись): очищенные файлы заменяются.</summary>
        private void Reclean(List<SignRow> rows)
        {
            if (rows.Count == 0) return;
            if (AcadConsole.Find(_s.Settings.Acad) == null)
            {
                MessageBox.Show(this, "AutoCAD не найден — очистить заново нельзя. Путь к AutoCAD — в настройках (шестерёнка).", "Подпись",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var answer = MessageBox.Show(this,
                "Очистить заново по текущим настройкам: " + rows.Count + " " + Plural(rows.Count) + "?" + Environment.NewLine + Environment.NewLine +
                "Очищенные файлы (…_clean.dwg в папке «clean») будут заменены. Подписи, которые уже есть в семействе, отметятся — " +
                "«Добавить» заменит их DWG.",
                "Подпись", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _s.Folder = null; // та же папка — обработать заново
            Process(new HashSet<string>(rows.Select(r => r.File.Source), StringComparer.OrdinalIgnoreCase));
        }

        private static string Plural(int n) =>
            n % 10 == 1 && n % 100 != 11 ? "подпись" : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20) ? "подписи" : "подписей";

        // ───────────────────────── «Актуализировать» ─────────────────────────

        /// <summary>
        /// Подписи семейства, файлов которых нет в папке (или вставленные вручную, не очищенные), — убрать отовсюду: из семейства
        /// (DWG, параметр), из таблицы выбора, и очищенные файлы без исходного — из папки «clean». Перед удалением — список.
        /// </summary>
        private void Actualize_Click(object sender, RoutedEventArgs e)
        {
            if (_s.State == null || _busy) return;
            var people = _rows.Where(r => r.File.NameProblem == null).Select(r => r.Name).ToList();
            var stale = _s.Family.Stale(_s.State, people);
            var orphans = OrphanCleanFiles();
            if (stale.Count == 0 && orphans.Count == 0)
            {
                MessageBox.Show(this, "Всё актуально: подписей, которых нет в папке, и вставленных вручную в семействе нет.", "Подпись",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var lines = stale.Take(15).Select(x => "• " + x.Name + (x.Number != null ? " (№" + x.Number + ")" : "") + " — " + x.Reason).ToList();
            if (stale.Count > 15) lines.Add("… и ещё " + (stale.Count - 15));
            var text = (stale.Count > 0
                           ? "Убрать из семейства «" + _s.Family.Title + "» неактуальные подписи: " + stale.Count + Environment.NewLine + Environment.NewLine +
                             string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
                             "Удалятся их параметры, DWG подписей и строки таблицы выбора «" + _s.Table + "»." + Environment.NewLine
                           : "") +
                       (orphans.Count > 0 ? "Из папки «clean» удалятся очищенные файлы без исходного: " + orphans.Count + "." + Environment.NewLine : "") +
                       Environment.NewLine + "Удалить?";
            if (MessageBox.Show(this, text, "Подпись — актуализация", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (stale.Count > 0)
            {
                var error = _s.Family.Remove(_s.State, _s.SourceEncoding, stale);
                if (error != null)
                {
                    MessageBox.Show(this, "Не удалось убрать подписи: " + error + Environment.NewLine + "Ничего не изменено.", "Подпись",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            var deleted = 0;
            foreach (var f in orphans)
            {
                try { File.Delete(f); deleted++; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* занят или нет прав — останется */ }
            }
            // семейство поменялось: таблицы, точка, план
            _s.Tables = _s.Family.TableChoices();
            _s.Anchor = _s.Family.Anchor(out _s.AnchorSpread);
            SetPoint();
            _formTable = null;
            ReadFamily();
            StatusText.Text = "Актуализация: убрано из семейства — " + stale.Count + ", очищенных файлов удалено — " + deleted + " (Ctrl+Z в Revit вернёт семейство).";
        }

        /// <summary>Очищенные файлы в «clean», исходного для которых в папке больше нет.</summary>
        private List<string> OrphanCleanFiles()
        {
            var clean = Path.Combine(_s.Folder ?? "", SignFolder.CleanDir);
            if (!Directory.Exists(clean)) return new List<string>();
            var sources = new HashSet<string>(SignFolder.Sources(_s.Folder).Select(SignFolder.CleanPathFor), StringComparer.OrdinalIgnoreCase);
            return Directory.GetFiles(clean, "*" + SignFolder.Suffix + ".dwg").Where(f => !sources.Contains(f)).ToList();
        }

        // ───────────────────────── регламент, план ─────────────────────────

        private void Regulation_Click(object sender, RoutedEventArgs e) => OpenRegulation();

        /// <summary>Регламент — в настройках (шестерёнка), открыть сразу на нём.</summary>
        private void OpenRegulation() => ShowSettings(true);

        /// <summary>План для всех строк (фамилию поправили, отметку поменяли): статусы, номера, доп. столбцы, счётчики.</summary>
        private void Replan()
        {
            if (_replanning || _s.State == null) return;
            _replanning = true;
            try
            {
                var items = SignPlan.Make(_rows.Select(r => (r.Name, Path.GetFileName(r.File.Clean), r.Note)), _s.State);
                for (var i = 0; i < _rows.Count; i++)
                {
                    _rows[i].SetPlan(items[i]);
                    _rows[i].SetExtras(_s.State.Rows);
                }
                SignPlan.Number(_rows.Where(r => r.Checked).Select(r => r.Item), _s.State);
                foreach (var r in _rows) r.RefreshNumber();
                CollectionViewSource.GetDefaultView(_rows).Refresh();
            }
            finally
            {
                _replanning = false;
            }
            Counts();
        }

        /// <summary>
        /// Строка в списке: уже добавленные — только с галочкой «Показывать уже добавленные»; поиск — по фамилии и имени файла
        /// (найденные показываются и уже добавленные — искали именно их).
        /// </summary>
        private bool Visible(SignRow r)
        {
            var q = (SearchBox?.Text ?? "").Trim();
            if (q.Length == 0) return ShowAdded.IsChecked == true || !r.IsAdded;
            return (r.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   Path.GetFileName(r.File.Source).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            CollectionViewSource.GetDefaultView(_rows).Refresh();
            Counts();
        }

        private void Counts()
        {
            var chosen = _rows.Count(r => r.Checked);
            var added = _rows.Count(r => r.IsAdded);
            ShowAdded.Content = "Показывать уже добавленные (" + added + ")";
            ShowAdded.IsEnabled = added > 0;
            AddButton.Content = chosen > 0 ? "Добавить (" + chosen + ")" : "Добавить";
            AddButton.IsEnabled = chosen > 0 && !_busy && _s.State != null;
            RefreshButton.IsEnabled = !_busy && _rows.Any(r => r.File.NameProblem == null);
            ActualizeButton.IsEnabled = !_busy && _s.State != null && _s.Folder != null && Directory.Exists(_s.Folder);
            var can = _rows.Where(r => r.CanCheck && Visible(r)).ToList();
            AllBox.IsChecked = can.Count > 0 && can.All(r => r.Checked) ? true : can.Any(r => r.Checked) ? (bool?)null : false;
        }

        private void ShowAdded_Changed(object sender, RoutedEventArgs e)
        {
            CollectionViewSource.GetDefaultView(_rows).Refresh();
            Counts();
        }

        private void All_Click(object sender, RoutedEventArgs e)
        {
            var on = AllBox.IsChecked == true;
            _replanning = true;
            foreach (var r in _rows.Where(r => r.CanCheck && Visible(r))) r.Checked = on;
            _replanning = false;
            Replan();
        }

        // ───────────────────────── настройки и «Добавить» ─────────────────────────

        private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(false);

        private void ShowSettings(bool regulation)
        {
            var w = new SignSettingsWindow(_s.Settings, _s.SettingsOffline, AcadConsole.Find(_s.Settings.Acad), regulation) { Owner = this };
            if (w.ShowDialog() != true) return;
            var error = _s.SaveSettings(w.Result);
            if (error != null)
            {
                MessageBox.Show(this, "Настройки не сохранены: " + error, "Подпись", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _s.Settings = w.Result;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var text = (ScaleBox.Text ?? "").Trim().Replace('.', ',');
            if (!double.TryParse(text, NumberStyles.Float, Ru, out var scale) || scale <= 0 || scale > 50)
            {
                ShowError("Масштаб — положительное число, например «1» или «1,5».");
                ScaleBox.Focus();
                return;
            }
            if (string.IsNullOrEmpty(_s.Table))
            {
                ShowError("Укажите имя новой таблицы выбора.");
                NewTableBox.Focus();
                return;
            }
            // фамилия могла остаться в поле ввода без потери фокуса
            if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Replan();
            var chosen = _rows.Where(r => r.Checked && r.CanCheck).ToList();
            if (chosen.Count == 0) return;
            _s.Scale = scale;
            _s.Point = PointPick.IsChecked == true ? SignPoint.Pick : PointSame.IsChecked == true && _s.Anchor != null ? SignPoint.Same : SignPoint.Origin;
            _s.RemoveUnused = RemoveUnused.Visibility == Visibility.Visible && RemoveUnused.IsChecked == true;
            _s.Jobs = chosen.Select(r => new SignJob { Item = r.Item, CleanPath = r.File.Clean, Extra = r.ExtraValues() }).ToList();
            DialogResult = true;
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
