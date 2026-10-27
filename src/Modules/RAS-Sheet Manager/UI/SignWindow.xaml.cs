using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    /// Добавляет команда (SignApp) после закрытия окна — одной группой действий.
    /// </summary>
    public partial class SignWindow : Window
    {
        private const string NewTable = "Новая таблица выбора…";
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private readonly SignSession _s;
        private readonly ObservableCollection<SignRow> _rows = new ObservableCollection<SignRow>();
        private CancellationTokenSource _cancel;
        private bool _busy;
        private bool _replanning;

        public SignWindow(SignSession session)
        {
            InitializeComponent();
            RASv2.Core.WindowTheme.Apply(this);
            _s = session;
            FamilyName.Text = session.Family.Title;
            List.ItemsSource = _rows;
            CollectionViewSource.GetDefaultView(_rows).Filter = o => ShowAdded.IsChecked == true || !((SignRow)o).IsAdded;
            ScaleBox.Text = session.Personal.ScaleFor(session.Family.Title).ToString("0.###", Ru);
            FillTables();
            if (session.Anchor != null)
            {
                AnchorText.Text = string.Format(Ru, "Центр подписи — как у остальных подписей: X = {0:0.0}, Y = {1:0.0} мм", session.Anchor.X * 304.8, session.Anchor.Y * 304.8) +
                                  (session.AnchorSpread > 0 ? "  (у " + session.AnchorSpread + " подписей точка другая)" : "");
            }
            else
            {
                AnchorText.Visibility = Visibility.Collapsed;
                PointChoice.Visibility = Visibility.Visible;
            }
            var folder = !string.IsNullOrEmpty(session.Personal.LastFolder) && Directory.Exists(session.Personal.LastFolder)
                ? session.Personal.LastFolder
                : session.Settings.Folder;
            FolderBox.Text = folder ?? "";
            Loaded += (o, e) => { if (Directory.Exists(FolderBox.Text)) Process(); else Counts(); };
            Closing += (o, e) => _cancel?.Cancel();
        }

        // ───────────────────────── таблица выбора ─────────────────────────

        private void FillTables()
        {
            var items = _s.Tables.Select(t => t.Name + (t.Formulas > 0 ? "  — формул: " + t.Formulas : "  — формулы на неё не ссылаются")).ToList();
            items.Add(NewTable);
            TableBox.ItemsSource = items;
            TableBox.SelectedIndex = 0;
            if (_s.Tables.Count == 0) NewTableBox.Text = _s.Settings.TableFor(_s.Family.Title);
        }

        private string ChosenTable() =>
            TableBox.SelectedIndex >= 0 && TableBox.SelectedIndex < _s.Tables.Count ? _s.Tables[TableBox.SelectedIndex].Name : SignNames.Clean((NewTableBox.Text ?? "").Trim()).Trim('"', '/', '*', '?');

        private void Table_Changed(object sender, SelectionChangedEventArgs e)
        {
            var isNew = TableBox.SelectedIndex >= _s.Tables.Count;
            NewTableBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            if (isNew && string.IsNullOrWhiteSpace(NewTableBox.Text))
            {
                var name = _s.Settings.TableFor(_s.Family.Title);
                NewTableBox.Text = _s.Tables.Any(t => t.Name == name) ? name + "_2" : name;
            }
            ReadFamily();
        }

        private void NewTable_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ReadFamily();

        /// <summary>Семейство с выбранной таблицей (строки таблицы, параметры, вставленные DWG) — и новый план.</summary>
        private void ReadFamily()
        {
            if (_s.Family == null) return;
            var table = ChosenTable();
            if (string.IsNullOrEmpty(table)) return;
            try
            {
                _s.Table = table;
                _s.State = _s.Family.State(table, _s.Tables, out _s.SourceEncoding, out _s.Backup);
                _s.TableExisted = _s.Backup != null; // таблица есть в семействе — её выгрузили
                var unused = _s.Family.UnusedTables(table);
                RemoveUnused.Visibility = unused.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RemoveUnused.Content = "Удалить таблицы выбора, на которые не ссылается ни одна формула: " + string.Join(", ", unused);
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
            var ink = (Brush)FindResource("Accent");
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
            NamingBanner.Visibility = wrong > 0 ? Visibility.Visible : Visibility.Collapsed;
            NamingText.Text = "Названы не по регламенту: " + wrong + " — такие подписи не будут добавлены. Переименуйте файлы и откройте папку заново. ";
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

        // ───────────────────────── «Обновить» и регламент ─────────────────────────

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

        private void Regulation_Click(object sender, RoutedEventArgs e) => OpenRegulation();

        /// <summary>Регламент: ссылка или файл из настроек; нет — окно с регламентом в плагине.</summary>
        private void OpenRegulation()
        {
            var link = _s.Settings.Regulation;
            if (!string.IsNullOrWhiteSpace(link))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
                    return;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException || ex is InvalidOperationException)
                {
                    MessageBox.Show(this, "Регламент не открылся (" + link + "): " + ex.Message + Environment.NewLine + "Показан регламент из плагина.",
                        "Подпись", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            new SignRegulationWindow(_s.Settings) { Owner = this }.ShowDialog();
        }

        // ───────────────────────── план ─────────────────────────

        /// <summary>План для всех строк (фамилию поправили, отметку поменяли): статусы, номера, счётчики.</summary>
        private void Replan()
        {
            if (_replanning || _s.State == null) return;
            _replanning = true;
            try
            {
                var items = SignPlan.Make(_rows.Select(r => (r.Name, Path.GetFileName(r.File.Clean), r.Note)), _s.State);
                for (var i = 0; i < _rows.Count; i++) _rows[i].SetPlan(items[i]);
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

        private void Counts()
        {
            var chosen = _rows.Count(r => r.Checked);
            var added = _rows.Count(r => r.IsAdded);
            ShowAdded.Content = "Показать уже добавленные (" + added + ")";
            ShowAdded.Visibility = added > 0 ? Visibility.Visible : Visibility.Collapsed;
            AddButton.Content = chosen > 0 ? "Добавить (" + chosen + ")" : "Добавить";
            AddButton.IsEnabled = chosen > 0 && !_busy && _s.State != null;
            RefreshButton.IsEnabled = !_busy && _rows.Any(r => r.File.NameProblem == null);
            var can = _rows.Where(r => r.CanCheck && (ShowAdded.IsChecked == true || !r.IsAdded)).ToList();
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
            foreach (var r in _rows.Where(r => r.CanCheck && (ShowAdded.IsChecked == true || !r.IsAdded))) r.Checked = on;
            _replanning = false;
            Replan();
        }

        // ───────────────────────── настройки и «Добавить» ─────────────────────────

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SignSettingsWindow(_s.Settings, _s.SettingsOffline, AcadConsole.Find(_s.Settings.Acad)) { Owner = this };
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
            _s.PickPoint = _s.Anchor == null && PointPick.IsChecked == true;
            _s.RemoveUnused = RemoveUnused.Visibility == Visibility.Visible && RemoveUnused.IsChecked == true;
            _s.Jobs = chosen.Select(r => new SignJob { Item = r.Item, CleanPath = r.File.Clean }).ToList();
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
