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
    /// на папке — все модели внутри), сетевые папки — окном Windows. Серверы — из настроек всех версий Revit на компьютере
    /// (RSN.ini), версия Revit Server — на выбор (2023–2026; разработчик, 03.11.2026). Содержимое папок — из кэша сразу и с сервера
    /// в фоне (ServerCache; «на сервер долго заходит»). Ответ — Picked: модели (RSN://… или файлы) с версией Revit.
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

        private const string FolderSource = "Папка на диске или в сети…";
        // последние выбранные сервер и версия — до закрытия Revit
        private static string _lastHost;
        private static int _lastYear;
        // кэши серверов — на время работы Revit (с диска читаются один раз)
        private static readonly Dictionary<string, ServerCache> Caches = new Dictionary<string, ServerCache>(StringComparer.OrdinalIgnoreCase);
        private static readonly string CacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAS_v_2.0", "RAS-Clash Analysis", "Кэш Revit Server");

        private int _year;
        private readonly List<string> _hosts;
        // папка проекта на сервере: раскрыть её после загрузки корня
        private readonly string _startHost;
        private readonly List<string> _startPath = new List<string>();
        private readonly ObservableCollection<Node> _roots = new ObservableCollection<Node>();
        private string _host;
        private int _sourceIndex = -1;

        /// <summary>Выбранные модели: «RSN://сервер/папка/модель.rvt» (с версией Revit Server) или пути файлов (версия — 0).</summary>
        public List<ExportModel> Picked { get; } = new List<ExportModel>();

        /// <param name="startFolder">Папка проекта на Revit Server (RSN://сервер/папка) — окно открывается в ней; пусто — корень.</param>
        public ModelPickerWindow(int revitYear, string startFolder = null)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _year = _lastYear > 0 ? _lastYear : RevitServers.Years.Contains(revitYear) ? revitYear : RevitServers.Years.Last();
            _hosts = RevitServers.AllHosts(revitYear);
            foreach (var y in RevitServers.Years) Year.Items.Add("Revit " + y);
            Year.SelectedIndex = Array.IndexOf(RevitServers.Years, _year);
            var start = (startFolder ?? "").Trim();
            if (start.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            {
                var parts = start.Substring("RSN://".Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    _startHost = parts[0];
                    _startPath.AddRange(parts.Skip(1));
                    // сервер проекта — первым, даже если его нет в настройках Revit этой версии
                    var known = _hosts.FindIndex(h => string.Equals(h, _startHost, StringComparison.OrdinalIgnoreCase));
                    if (known >= 0) _hosts.RemoveAt(known);
                    _hosts.Insert(0, _startHost);
                    _lastHost = _startHost;
                }
            }
            // только адрес (разработчик, 02.11.2026: «слово Revit Server уберём, оставим просто адрес»)
            foreach (var h in _hosts) Source.Items.Add(h);
            Source.Items.Add(FolderSource);
            Tree.ItemsSource = _roots;
            if (_hosts.Count == 0)
                Status.Text = "В Revit на этом компьютере не настроен ни один Revit Server — выберите «" + FolderSource + "».";
            // сервер — когда окно уже показано: в конструкторе у потока ещё может не быть контекста WPF (см. UiContext)
            else
                Loaded += (s, e) =>
                    Source.SelectedIndex = Math.Max(0, _hosts.FindIndex(h => string.Equals(h, _lastHost, StringComparison.OrdinalIgnoreCase)));
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
                var index = Source.SelectedIndex;
                if (index < 0 || index == _sourceIndex) return;
                if (index == _hosts.Count)
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Модели Revit (*.rvt)|*.rvt", Multiselect = true };
                    if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
                    {
                        Picked.AddRange(dialog.FileNames.Select(f => new ExportModel { Path = f }));
                        DialogResult = true;
                        return;
                    }
                    Source.SelectedIndex = _sourceIndex;   // отмена — как было
                    return;
                }
                _sourceIndex = index;
                _host = _hosts[index];
                _lastHost = _host;
                await LoadRoot(false);
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — источник", ex);
            }
        }

        private async void Year_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                UiContext();
                if (Year.SelectedIndex < 0) return;
                var year = RevitServers.Years[Year.SelectedIndex];
                if (year == _year && _roots.Count > 0) return;
                _year = _lastYear = year;
                if (_host != null) await LoadRoot(false);
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

        private ServerCache CacheOf(string host)
        {
            if (!Caches.TryGetValue(host, out var cache)) Caches[host] = cache = ServerCache.Open(CacheDir, host);
            return cache;
        }

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
            Status.Text = "Загрузка Revit Server " + host + " (Revit " + year + ")…";
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
                into.Add(new Node(this, parent, m.Name, path, false, RevitServers.SizeText(m.ModelSize)));
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
