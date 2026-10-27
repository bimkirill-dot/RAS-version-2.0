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
    /// Таблица выбора и форма формулы — из самого семейства. «Актуализировать» проверяет семейство и исправляет отмеченное
    /// (окно SignAuditWindow). У таблицы ГИП текст для штампа («Фамилия И.») складывается из инициала (справочник на сервере).
    /// Добавляет команда (SignApp) после закрытия окна — одной группой действий.
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
        /// <summary>Столбец текста для штампа выбранной таблицы (у ГИП — «Фамилия И.»); -1 — нет.</summary>
        private int _display = -1;

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

        /// <summary>
        /// Семейство с выбранной таблицей (строки таблицы, параметры, вставленные DWG) — и новый план. Форма формулы — как в
        /// формулах семейства на эту таблицу (у семейства ГИП — своя), формул нет — из настроек («Формула»).
        /// </summary>
        private void ReadFamily()
        {
            if (_s.Family == null) return;
            var table = ChosenTable();
            if (string.IsNullOrEmpty(table)) return;
            try
            {
                var rows = _s.Family.ReadTable(table, "Фамилия", "Да/Нет", out _, out _);
                var f = _s.Family.FormFor(table, _s.Tables, rows);
                TableBox.ToolTip = "Формула: " + f.Text + (f.FromFamily ? " — как в формулах семейства" : f.Gip ? " — из настроек для семейства ГИП (формул на эту таблицу нет)" : " — из настроек (формул на эту таблицу нет)");
                _s.Table = table;
                _s.State = _s.Family.State(table, f, out _s.SourceEncoding, out _s.Backup);
                _s.TableExisted = _s.Backup != null; // таблица есть в семействе — её выгрузили
                // таблица ГИП: инициалы, уже вписанные в неё, — в справочник (дальше подставляются сами)
                _display = SignAudit.DisplayColumn(_s.State.Rows);
                if (SignInitials.Learn(_s.Initials, _s.State.Rows, _display) > 0) _s.InitialsChanged = true;
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
        /// Проверить семейство (SignAudit: люди без файла в папке и вставленные вручную, строки таблицы без людей и дубли, один
        /// номер у двух фамилий, формула не как таблица, параметр без подписи, две вставки, вставки ни к кому не привязанные,
        /// типы без вставок, подпись старее очищенного файла, текст для штампа ГИП; очищенные файлы без исходного) → окно со
        /// списком → отмеченное исправить одной группой действий (одно Ctrl+Z). Подписи без DWG и устаревшие — отмечаются в
        /// списке, их добавит «Добавить».
        /// </summary>
        private void Actualize_Click(object sender, RoutedEventArgs e)
        {
            if (_s.State == null || _busy) return;
            var folder = _rows.Where(r => r.File.NameProblem == null).Select(r => new SignSource
            {
                Name = (r.Name ?? "").Trim(),
                CleanFile = r.File.Clean,
                CleanTimeUtc = r.File.Clean != null && File.Exists(r.File.Clean) ? File.GetLastWriteTimeUtc(r.File.Clean) : (DateTime?)null,
            }).ToList();
            List<SignIssue> issues;
            try { issues = _s.Family.Audit(_s.State, folder, _s.Initials); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                ShowError("Не удалось проверить семейство: " + ex.Message);
                return;
            }
            foreach (var f in OrphanCleanFiles())
                issues.Add(new SignIssue { Kind = SignIssueKind.CleanOrphan, Name = Path.GetFileName(f), Value = f, Fixable = true, Text = "очищенный файл, исходного в папке нет — удалить из «clean»" });
            _s.Log?.Invoke("Актуализация «" + _s.Family.Title + "», таблица «" + _s.Table + "»: в папке подписей — " + folder.Count + ", находок — " + issues.Count +
                           (issues.Count > 0 ? ": " + string.Join("; ", issues.Take(30).Select(x => (x.Name.Length > 0 ? x.Name + " — " : "") + x.Text)) : ""));

            // без DWG и старее очищенного файла — отметить в списке: добавит «Добавить»
            var outdated = new HashSet<string>(issues.Where(i => i.Kind == SignIssueKind.OutdatedDwg).Select(i => i.Name));
            foreach (var r in _rows.Where(r => outdated.Contains((r.Name ?? "").Trim()))) r.Refreshed = true;
            if (outdated.Count > 0)
            {
                ShowAdded.IsChecked = true;
                Replan();
            }

            if (issues.Count == 0)
            {
                MessageBox.Show(this, "Всё актуально: в семействе «" + _s.Family.Title + "» исправлять нечего.", "Подпись", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var w = new SignAuditWindow("Семейство «" + _s.Family.Title + "», таблица выбора «" + _s.Table + "»: находок — " + issues.Count, issues,
                "Убранные подписи удаляются целиком: DWG, параметр и строка таблицы выбора.") { Owner = this };
            if (w.ShowDialog() != true || w.Result == null || w.Result.Count == 0) return;

            var chosen = w.Result;
            var inFamily = chosen.Where(i => i.Kind != SignIssueKind.CleanOrphan).ToList();
            if (inFamily.Count > 0)
            {
                var error = _s.Family.Fix(_s.State, _s.SourceEncoding, inFamily);
                if (error != null)
                {
                    ReadFamily();
                    MessageBox.Show(this, "Не удалось исправить: " + error + Environment.NewLine + "Семейство не изменено.", "Подпись",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            // вписанные инициалы — в справочник
            foreach (var d in inFamily.Where(i => i.Kind == SignIssueKind.Display && i.Value != null))
            {
                var ini = SignAudit.Initial(d.Name, d.Value);
                if (ini != null && (!_s.Initials.TryGetValue(d.Name, out var was) || was != ini)) { _s.Initials[d.Name] = ini; _s.InitialsChanged = true; }
            }
            var deleted = 0;
            foreach (var f in chosen.Where(i => i.Kind == SignIssueKind.CleanOrphan))
            {
                try { File.Delete(f.Value); deleted++; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* занят или нет прав — останется */ }
            }
            // семейство поменялось: таблицы, точка, план
            _s.Tables = _s.Family.TableChoices();
            _s.Anchor = _s.Family.Anchor(out _s.AnchorSpread);
            SetPoint();
            ReadFamily();
            StatusText.Text = "Актуализация: исправлено в семействе — " + inFamily.Count + (deleted > 0 ? ", очищенных файлов удалено — " + deleted : "") +
                              (inFamily.Count > 0 ? " (Ctrl+Z в Revit вернёт семейство)." : ".");
            _s.Log?.Invoke("Актуализация «" + _s.Family.Title + "»: исправлено — " + inFamily.Count + ", файлов удалено — " + deleted);
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
                    _rows[i].SetExtras(_s.State.Rows, _display, _s.Initials);
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
            var noInitial = chosen.Where(r => r.NoInitial).Select(r => r.Name).ToList();
            if (noInitial.Count > 0 && MessageBox.Show(this, "Не вписан инициал для штампа: " + string.Join(", ", noInitial.Take(10)) + (noInitial.Count > 10 ? " и ещё " + (noInitial.Count - 10) : "") +
                    "." + Environment.NewLine + "В штампе у них будет пусто. Добавить так?", "Подпись", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            // вписанные инициалы — в справочник
            foreach (var r in chosen)
                foreach (var x in r.Extras.Where(x => x.Editable && x.Initial && !string.IsNullOrWhiteSpace(x.Value)))
                {
                    var ini = SignAudit.Initial(r.Name.Trim(), SignAudit.Display(r.Name.Trim(), x.Value));
                    if (ini != null && (!_s.Initials.TryGetValue(r.Name.Trim(), out var was) || was != ini)) { _s.Initials[r.Name.Trim()] = ini; _s.InitialsChanged = true; }
                }
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
