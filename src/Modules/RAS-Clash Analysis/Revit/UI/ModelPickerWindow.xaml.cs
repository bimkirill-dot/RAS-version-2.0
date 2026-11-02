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
    /// на папке — все модели внутри), сетевые папки — окном Windows. Серверы — из настроек Revit своей версии (RSN.ini). Ответ —
    /// Picked: пути моделей (RSN://… или файлы).
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
        // последний выбранный сервер — до закрытия Revit
        private static string _lastHost;

        private readonly int _year;
        private readonly List<string> _hosts;
        // папка проекта на сервере: раскрыть её после загрузки корня
        private readonly string _startHost;
        private readonly List<string> _startPath = new List<string>();
        private readonly ObservableCollection<Node> _roots = new ObservableCollection<Node>();
        private string _host;
        private int _sourceIndex = -1;

        /// <summary>Выбранные модели: «RSN://сервер/папка/модель.rvt» или пути файлов.</summary>
        public List<string> Picked { get; } = new List<string>();

        /// <param name="startFolder">Папка проекта на Revit Server (RSN://сервер/папка) — окно открывается в ней; пусто — корень.</param>
        public ModelPickerWindow(int revitYear, string startFolder = null)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _year = revitYear;
            _hosts = RevitServers.Hosts(revitYear);
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
                Status.Text = "В Revit " + revitYear + " не настроен ни один Revit Server — выберите «" + FolderSource + "».";
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
                        Picked.AddRange(dialog.FileNames);
                        DialogResult = true;
                        return;
                    }
                    Source.SelectedIndex = _sourceIndex;   // отмена — как было
                    return;
                }
                _sourceIndex = index;
                _host = _hosts[index];
                _lastHost = _host;
                await LoadRoot();
            }
            catch (Exception ex)
            {
                Fail("Задачи выгрузки: выбор моделей — источник", ex);
            }
        }

        private async Task LoadRoot()
        {
            var host = _host;
            _roots.Clear();
            UpdateCount();
            Status.Text = "Загрузка Revit Server " + host + "…";
            try
            {
                var contents = await RevitServerClient.ContentsAsync(host, _year, new List<string>());
                if (host != _host) return;   // пока грузилось, выбрали другой сервер
                Fill(_roots, null, new List<string>(), contents);
                Status.Text = _roots.Count == 0 ? "На сервере пусто." : "";
                if (string.Equals(host, _startHost, StringComparison.OrdinalIgnoreCase)) await OpenStart(host);
            }
            catch (Exception ex)
            {
                if (host != _host) return;
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: Revit Server " + host + " (Revit " + _year + ") не ответил", ex);
                Status.Text = "Revit Server " + host + " не отвечает для Revit " + _year + ".";
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
            try
            {
                var contents = await RevitServerClient.ContentsAsync(host, _year, node.Path);
                if (host != _host) return;
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

        private async Task LoadDeep(Node folder)
        {
            if (!folder.Loaded) await LoadChildren(folder);
            foreach (var child in folder.Children.Where(c => c.IsFolder).ToList())
                if (child.IsChecked) await LoadDeep(child);
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
            Picked.AddRange(CheckedModels(_roots).Select(m => RevitServers.RsnPath(_host, m.Path, m.Name)));
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
