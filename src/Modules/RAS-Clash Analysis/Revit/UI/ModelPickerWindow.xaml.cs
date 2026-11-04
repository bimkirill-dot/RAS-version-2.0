using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RASv2.ClashAnalysis.Export;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Добавить модели…» строки «Задач выгрузки»: Revit Server — деревом с галочками (папки подгружаются при раскрытии, галочка
    /// на папке — все модели внутри), сетевые папки — окном Windows. «Откуда» — пары «сервер · Revit год» из настроек (RSN.ini)
    /// версий Revit, установленных на компьютере (разработчик, 03.11.2026: «предлагает серверы тех Revit, которых нет»; модели 2023
    /// — на одних серверах, 2026 — на других); не отвечает — так и написано. Содержимое папок — из кэша сразу и с сервера в фоне
    /// (ServerCache). Ответ — Picked: модели (RSN://… или файлы) с версией Revit.
    /// </summary>
    public partial class ModelPickerWindow : Window
    {
        /// <summary>Папка или модель в дереве; заглушка («загрузка…», «не загрузилось») — без галочки.</summary>
        public sealed class Node : INotifyPropertyChanged
        {
            private readonly ModelPickerWindow _window;
            private bool _expanded;
            private bool _checked;

            public Node(ModelPickerWindow window, Node parent, string name, List<string> path, bool isFolder, string size = "",
                bool placeholder = false)
            {
                _window = window;
                Parent = parent;
                Name = name;
                Path = path;
                IsFolder = isFolder;
                Size = size;
                IsPlaceholder = placeholder;
            }

            public Node Parent { get; }
            public string Name { get; }
            /// <summary>У папки — её путь, у модели — путь её папки.</summary>
            public List<string> Path { get; }
            public bool IsFolder { get; }
            public bool IsPlaceholder { get; }
            public string Size { get; }
            public bool Loaded { get; set; }

            /// <summary>Когда изменена модель: «03.11.2026 14:20»; пока не пришло — пусто.</summary>
            public string Date
            {
                get => _date;
                set
                {
                    _date = value;
                    Notify();
                }
            }

            private string _date = "";
            public ObservableCollection<Node> Children { get; } = new ObservableCollection<Node>();

            public string Glyph => IsPlaceholder ? "" : IsFolder ? "\uF12B" : "\uE7C3";
            public Visibility CheckVisibility => IsPlaceholder ? Visibility.Collapsed : Visibility.Visible;

            public bool IsExpanded
            {
                get => _expanded;
                set
                {
                    if (_expanded == value) return;
                    _expanded = value;
                    Notify();
                    if (value) _window.Expand(this);
                }
            }

            public bool IsChecked
            {
                get => _checked;
                set
                {
                    if (_checked == value) return;
                    _checked = value;
                    Notify();
                    _window.Checked(this, value);
                }
            }

            /// <summary>Галочка без отклика окна — когда её ставит само окно (вниз по папке, вверх при снятии).</summary>
            internal void SetChecked(bool value)
            {
                if (_checked == value) return;
                _checked = value;
                Notify(nameof(IsChecked));
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// Строка списка с подписью Text: «— не отвечает» меняется свойством, без Items.Refresh — тот сбрасывал выбор на первую
        /// строку, и окно уходило на 10.10.1.88 · Revit 2023 (04.11.2026).
        /// </summary>
        private abstract class ListItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            private bool _down;
            public bool Down
            {
                get => _down;
                set
                {
                    if (_down == value) return;
                    _down = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }
            public abstract string Text { get; }
            public override string ToString() => Text;
        }

        /// <summary>Сервер в «Сервер» (или «Папка на диске или в сети…»); не ответил ни для одной версии — так и написано.</summary>
        private sealed class HostItem : ListItem
        {
            public string Host;
            public override string Text => Host == null ? FolderSource : Host + (Down ? " — не отвечает" : "");
        }

        /// <summary>Версия в «Версия»; у выбранного сервера не ответила — так и написано.</summary>
        private sealed class YearItem : ListItem
        {
            public int Year;
            public override string Text => "Revit " + Year + (Down ? " — не отвечает" : "");
        }

        private const string FolderSource = "Папка на диске или в сети…";
        // последний выбранный сервер и версия — до закрытия Revit
        private static string _lastHost;
        private static int _lastYear;

        private int _year;
        private readonly List<HostItem> _hosts;
        private readonly List<YearItem> _years;
        // ответы серверов: «сервер|год» → ответил; нет ключа — ещё проверяется
        private readonly Dictionary<string, bool> _alive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // папка проекта на сервере: раскрыть её после загрузки корня
        private readonly string _startHost;
        private readonly List<string> _startPath = new List<string>();
        private readonly ObservableCollection<Node> _roots = new ObservableCollection<Node>();
        private string _host;
        private int _hostIndex = -1;
        // списки меняет сам код — без загрузки; человек выбрал сам — окно больше не переключает за него
        private bool _choosing;
        private bool _userChose;

        /// <summary>Выбранные модели: «RSN://сервер/папка/модель.rvt» (с версией Revit Server) или пути файлов (версия — 0).</summary>
        public List<ExportModel> Picked { get; } = new List<ExportModel>();

        /// <param name="startFolder">Папка проекта на Revit Server (RSN://сервер/папка) — окно открывается в ней; пусто — корень.</param>
        public ModelPickerWindow(int revitYear, string startFolder = null)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            var years = RevitServers.InstalledYears(revitYear);
            _years = years.Select(y => new YearItem { Year = y }).ToList();
            var hosts = RevitServers.AllHosts(years);
            var start = (startFolder ?? "").Trim();
            if (start.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            {
                var parts = start.Substring("RSN://".Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    _startHost = parts[0];
                    _startPath.AddRange(parts.Skip(1));
                    // сервер проекта — первым, даже если его нет в настройках Revit
                    hosts.RemoveAll(h => Same(h, _startHost));
                    hosts.Insert(0, _startHost);
                }
            }
            _hosts = hosts.Select(h => new HostItem { Host = h }).ToList();
            foreach (var h in _hosts) Source.Items.Add(h);
            Source.Items.Add(new HostItem());   // папка на диске или в сети
            foreach (var y in _years) Version.Items.Add(y);
            Tree.ItemsSource = _roots;
            if (_hosts.Count == 0)
            {
                Status.Text = "В Revit на этом компьютере не настроен ни один Revit Server — выберите «" + FolderSource + "».";
                Version.IsEnabled = false;
            }
            // выбор — когда окно уже показано: в конструкторе у потока ещё может не быть контекста WPF (см. UiContext)
            else
                Loaded += async (s, e) =>
                {
                    try
                    {
                        UiContext();
                        var year = _lastHost != null && _lastYear > 0 ? _lastYear : revitYear;
                        // сервер не задан — тот, что уже отвечал для этой версии (кэш): 10.10.1.88 первый в настройках Revit 2023,
                        // а службы 2023 на нём нет (04.11.2026)
                        var host = RevitServers.StartHost(hosts, _lastHost ?? _startHost, year,
                            (h, y) => ServerCache.Of(h).TryGet(y, new List<string>(), out _, out _));
                        Probe();
                        await Choose(host, year);
                    }
                    catch (Exception ex)
                    {
                        Fail("Задачи выгрузки: выбор моделей — открытие", ex);
                    }
                };
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string PairKey(string host, int year) => host + "|" + year;

        /// <summary>
        /// Какие серверы отвечают для каких версий — все пары одновременно, в фоне; не ответила — «— не отвечает» у версии (для
        /// выбранного сервера) и у сервера (ни одна версия). Выбранная при открытии пара не ответила, а человек ещё ничего не
        /// выбирал — окно переходит на сервер, который отвечает для этой версии, и пишет об этом.
        /// </summary>
        private async void Probe()
        {
            try
            {
                UiContext();
                // по сведениям о сервере — мгновенно; содержимое корня у большого хранилища — минуту и дольше
                await Task.WhenAll(_hosts.SelectMany(h => _years.Select(y => (h.Host, y.Year))).Select(async p =>
                {
                    try
                    {
                        await RevitServerClient.PingAsync(p.Host, p.Year);
                        _alive[PairKey(p.Host, p.Year)] = true;
                    }
                    catch (Exception)
                    {
                        _alive[PairKey(p.Host, p.Year)] = false;
                    }
                    Mark();
                }));
                if (_userChose || _host == null || _roots.Count > 0 || !_alive.TryGetValue(PairKey(_host, _year), out var ok) || ok) return;
                var other = _hosts.FirstOrDefault(h => _alive.TryGetValue(PairKey(h.Host, _year), out var a) && a);
                if (other == null) return;
                var was = _host;
                await Choose(other.Host, _year);
                if (_roots.Count > 0) Status.Text = $"{was} не отвечает для Revit {_year} — открыт {other.Host}.";
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: проверка серверов Revit Server", ex);
            }
        }

        /// <summary>Пометки «— не отвечает»: у версий — для выбранного сервера, у серверов — если не ответила ни одна версия.</summary>
        private void Mark()
        {
            foreach (var h in _hosts)
                h.Down = _years.All(y => _alive.TryGetValue(PairKey(h.Host, y.Year), out var a) && !a);
            foreach (var y in _years)
                y.Down = _host != null && _alive.TryGetValue(PairKey(_host, y.Year), out var a) && !a;
        }

        /// <summary>Выбрать сервер и версию из кода (списки — без своих событий) и загрузить.</summary>
        private async Task Choose(string host, int year)
        {
            _choosing = true;
            try
            {
                Source.SelectedIndex = Math.Max(0, _hosts.FindIndex(h => Same(h.Host, host)));
                Version.SelectedIndex = Math.Max(0, _years.FindIndex(y => y.Year == year));
            }
            finally
            {
                _choosing = false;
            }
            await Open();
        }

        /// <summary>Открыть выбранную пару «сервер · версия»: та же, что открыта, — ничего.</summary>
        private async Task Open()
        {
            var hostIndex = Source.SelectedIndex;
            if (hostIndex < 0 || hostIndex >= _hosts.Count || Version.SelectedIndex < 0) return;
            var host = _hosts[hostIndex].Host;
            var year = _years[Version.SelectedIndex].Year;
            if (Same(host, _host) && year == _year) return;
            _hostIndex = hostIndex;
            _host = host;
            _year = year;
            (_lastHost, _lastYear) = (_host, _year);
            Mark();
            await LoadRoot(false);
        }

        /// <summary>
        /// Продолжение после ответа сервера — в потоке окна: без контекста WPF (Revit, конструктор окна) оно ушло бы в чужой
        /// поток и тронуло дерево оттуда — проверка вне Revit упала с переполнением стека, в Revit упал бы Revit.
        /// </summary>
        private void UiContext()
        {
            if (!(SynchronizationContext.Current is DispatcherSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
        }

        private async void Source_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                UiContext();
                if (_choosing) return;
                var index = Source.SelectedIndex;
                if (index < 0 || index == _hostIndex) return;
                if (index == _hosts.Count)
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Модели Revit (*.rvt)|*.rvt", Multiselect = true };
                    if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
                    {
                        Picked.AddRange(dialog.FileNames.Select(f => new ExportModel { Path = f }));
                        DialogResult = true;
                        return;
                    }
                    _choosing = true;
                    Source.SelectedIndex = _hostIndex;   // отмена — как было
                    _choosing = false;
                    return;
                }
                _userChose = true;
                // у нового сервера эта версия не отвечает, а другая отвечает — взять её
                var host = _hosts[index].Host;
                var year = Version.SelectedIndex >= 0 ? _years[Version.SelectedIndex].Year : _year;
                if (_alive.TryGetValue(PairKey(host, year), out var ok) && !ok)
                {
                    var other = _years.FirstOrDefault(y => _alive.TryGetValue(PairKey(host, y.Year), out var a) && a);
                    if (other != null)
                    {
                        await Choose(host, other.Year);
                        return;
                    }
                }
                await Open();
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — сервер", ex);
            }
        }

        private async void Version_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                UiContext();
                if (_choosing) return;
                _userChose = true;
                await Open();
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — версия", ex);
            }
        }

        // «Обновить» — всё заново с сервера, мимо кэша
        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UiContext();
                if (_host != null) await LoadRoot(true);
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — обновить", ex);
            }
        }

        private static ServerCache CacheOf(string host) => ServerCache.Of(host);

        /// <summary>
        /// Содержимое папки: из кэша — сразу, а с сервера — в фоне (пришло другое — fresh); в кэше нет или force — с сервера. Ответ
        /// сервера — в кэш.
        /// </summary>
        private async Task<ServerContents> Contents(string host, int year, List<string> path, bool force, Action<ServerContents> fresh)
        {
            var cache = CacheOf(host);
            if (!force && cache.TryGet(year, path, out var saved, out _))
            {
                RefreshLater(host, year, path, saved, fresh);
                return saved;
            }
            var contents = await RevitServerClient.ContentsAsync(host, year, path);
            cache.Put(year, path, contents, DateTime.Now);
            return contents;
        }

        private async void RefreshLater(string host, int year, List<string> path, ServerContents saved, Action<ServerContents> fresh)
        {
            try
            {
                UiContext();
                var contents = await RevitServerClient.ContentsAsync(host, year, path);
                RevitServers.KeepDates(saved, contents);
                CacheOf(host).Put(year, path, contents, DateTime.Now);
                if (host == _host && year == _year && !RevitServers.Same(saved, contents)) fresh(contents);
            }
            catch (Exception ex)
            {
                // сервер не ответил — остаётся сохранённое; в журнал — для разбора
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: Revit Server " + host + " (Revit " + year + ") — кэш не обновлён", ex);
            }
        }

        private async Task LoadRoot(bool force)
        {
            var host = _host;
            var year = _year;
            _roots.Clear();
            UpdateCount();
            Status.Text = "Загрузка Revit Server " + host + " (Revit " + year + ")… Сервер считает размеры папок — у большого хранилища до 2–3 минут; потом — из кэша сразу.";
            try
            {
                var contents = await Contents(host, year, new List<string>(), force, c =>
                {
                    // на сервере поменялось: корень — заново, если ничего ещё не отмечено
                    if (CheckedModels(_roots).Any()) return;
                    _roots.Clear();
                    Fill(_roots, null, new List<string>(), c);
                });
                if (host != _host || year != _year) return;   // пока грузилось, выбрали другой сервер или версию
                Fill(_roots, null, new List<string>(), contents);
                Status.Text = _roots.Count == 0 ? "На сервере пусто." : "";
                if (string.Equals(host, _startHost, StringComparison.OrdinalIgnoreCase)) await OpenStart(host);
            }
            catch (Exception ex)
            {
                if (host != _host || year != _year) return;
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: Revit Server " + host + " (Revit " + year + ") не ответил", ex);
                Status.Text = "Revit Server " + host + " не отвечает для Revit " + year + ".";
            }
        }

        /// <summary>Раскрыть папку проекта: по частям пути, каждая — после загрузки предыдущей.</summary>
        private async Task OpenStart(string host)
        {
            IEnumerable<Node> level = _roots;
            foreach (var name in _startPath)
            {
                var folder = level.FirstOrDefault(n => n.IsFolder && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
                if (folder == null || host != _host) return;
                if (!folder.Loaded) await LoadChildren(folder);
                folder.IsExpanded = true;
                level = folder.Children;
            }
        }

        private void Fill(ObservableCollection<Node> into, Node parent, List<string> path, ServerContents contents)
        {
            foreach (var f in contents.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var folder = new Node(this, parent, f.Name, new List<string>(path) { f.Name }, true);
                if (f.HasContents) folder.Children.Add(new Node(this, folder, "загрузка…", path, false, placeholder: true));
                else folder.Loaded = true;
                into.Add(folder);
            }
            foreach (var m in contents.Models.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                into.Add(new Node(this, parent, m.Name, path, false, RevitServers.SizeText(m.ModelSize)) { Date = DateText(m.Modified) });
            LoadDates(into, path, contents);
        }

        private static string DateText(DateTime? d) => d == null ? "" : "изм. " + d.Value.ToString("dd.MM.yyyy HH:mm");

        // даты моделей — по одной на запрос (в ответе о папке их нет): одновременно до шести, полученные — в кэш
        private static readonly SemaphoreSlim DateGate = new SemaphoreSlim(6);

        private async void LoadDates(ObservableCollection<Node> nodes, List<string> path, ServerContents contents)
        {
            var host = _host;
            var year = _year;
            var missing = contents.Models.Where(m => m.Modified == null).ToList();
            if (missing.Count == 0) return;
            try
            {
                UiContext();
                await Task.WhenAll(missing.Select(async m =>
                {
                    await DateGate.WaitAsync();
                    try
                    {
                        m.Modified = (await RevitServerClient.ModelInfoAsync(host, year, path, m.Name)).DateModified;
                    }
                    catch (Exception)
                    {
                        // дата не пришла — модель всё равно можно выбрать
                    }
                    finally
                    {
                        DateGate.Release();
                    }
                    var node = nodes.FirstOrDefault(n => !n.IsFolder && n.Name == m.Name);
                    if (node != null) node.Date = DateText(m.Modified);
                }));
                CacheOf(host).Put(year, path, contents, DateTime.Now);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: даты моделей Revit Server не получены", ex);
            }
        }

        internal async void Expand(Node node)
        {
            try
            {
                UiContext();
                if (node.IsFolder && !node.Loaded) await LoadChildren(node);
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — папка", ex);
            }
        }

        private async Task LoadChildren(Node node)
        {
            node.Loaded = true;
            var host = _host;
            var year = _year;
            try
            {
                var contents = await Contents(host, year, node.Path, false, c =>
                {
                    // на сервере поменялось: папка — заново, если в ней ничего не отмечено
                    if (node.IsChecked || CheckedModels(node.Children).Any()) return;
                    node.Children.Clear();
                    Fill(node.Children, node, node.Path, c);
                });
                if (host != _host || year != _year) return;
                node.Children.Clear();
                Fill(node.Children, node, node.Path, contents);
                if (node.IsChecked) SetDeep(node, true);   // папка уже отмечена — и её содержимое
            }
            catch (Exception ex)
            {
                if (host != _host) return;
                node.Loaded = false;
                node.Children.Clear();
                node.Children.Add(new Node(this, node, "не загрузилось — сверните и раскройте снова", node.Path, false, placeholder: true));
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: папка Revit Server не загрузилась — " + string.Join("/", node.Path), ex);
            }
            UpdateCount();
        }

        internal async void Checked(Node node, bool value)
        {
            try
            {
                UiContext();
                if (!node.IsFolder)
                {
                    // сняли модель — с папок над ней галочка тоже снимается (они больше не «все»)
                    if (!value)
                        for (var p = node.Parent; p != null; p = p.Parent) p.SetChecked(false);
                    UpdateCount();
                    return;
                }
                if (!value)
                    for (var p = node.Parent; p != null; p = p.Parent) p.SetChecked(false);
                SetDeep(node, value);
                UpdateCount();
                if (value)
                {
                    // отмечена папка — все модели внутри: подгрузить вложенные папки
                    Status.Text = "Загрузка «" + node.Name + "»…";
                    await LoadDeep(node);
                    if (Status.Text.StartsWith("Загрузка «")) Status.Text = "";
                }
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — галочка", ex);
            }
        }

        // вложенные папки — одновременно, а не по одной (быстрее на больших проектах)
        private async Task LoadDeep(Node folder)
        {
            if (!folder.Loaded) await LoadChildren(folder);
            await Task.WhenAll(folder.Children.Where(c => c.IsFolder && c.IsChecked).ToList().Select(LoadDeep));
        }

        private static void SetDeep(Node folder, bool value)
        {
            foreach (var child in folder.Children.Where(c => !c.IsPlaceholder))
            {
                child.SetChecked(value);
                if (child.IsFolder) SetDeep(child, value);
            }
        }

        private IEnumerable<Node> CheckedModels(IEnumerable<Node> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsPlaceholder) continue;
                if (!n.IsFolder && n.IsChecked) yield return n;
                foreach (var m in CheckedModels(n.Children)) yield return m;
            }
        }

        private void UpdateCount()
        {
            var count = CheckedModels(_roots).Count();
            AddButton.IsEnabled = count > 0;
            AddButton.Content = count > 0 ? "Добавить (" + count + ")" : "Добавить";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            Picked.AddRange(CheckedModels(_roots).Select(m => new ExportModel { Path = RevitServers.RsnPath(_host, m.Path, m.Name), Year = _year }));
            DialogResult = true;
        }

        /// <summary>Ошибка в обработчике окна — в журнал и строкой внизу: из async void она уронила бы Revit.</summary>
        private void Fail(string what, Exception ex)
        {
            RASv2.Core.Log.Write("ClashAnalysis", what, ex);
            Status.Text = "Не получилось — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).";
        }
    }
}
