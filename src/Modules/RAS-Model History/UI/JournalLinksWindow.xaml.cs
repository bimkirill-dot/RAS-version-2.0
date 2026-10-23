using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using RASv2.ModelHistory.Storage;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// «⋯» → «Связанные журналы…» (DECISIONS.md модуля, «Связь журналов»): связанные с моделью журналы — отвязать; все журналы
    /// общей папки с поиском — связать вручную (модель переименовали или перенесли — сама полоса в панели её не найдёт).
    /// Сетевая папка бывает медленной — всё, что читает её, в фоне.
    /// </summary>
    public partial class JournalLinksWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly string _modelKey;
        private readonly IReadOnlyList<string> _own;
        private List<string> _all = new List<string>();
        private List<string> _linked = new List<string>();
        private int _detailsVersion;

        internal JournalLinksWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            _modelKey = vm.LinksModelKey;
            _own = vm.LinksOwnKeys;
            ModelText.Text = "Связаны с моделью «" + vm.ModelTitle + "»";
            CountText.Text = "Читаю список журналов…";
            vm.LinksChanged += Refresh;
            Closed += (s, e) => vm.LinksChanged -= Refresh;
            Loaded += (s, e) => Search.Focus();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Refresh();
        }

        private void Refresh()
        {
            var key = _modelKey;
            var legacy = _own.Skip(1).ToList();
            Task.Run(() =>
            {
                var linked = JournalLinkStore.LinkedKeys(key, legacy);
                var all = JournalLinkStore.AllKeys();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _linked = linked;
                    _all = all;
                    LinkedList.ItemsSource = _linked.Count == 0 ? new List<string> { "(связанных журналов нет)" } : _linked;
                    LinkedList.IsEnabled = _linked.Count > 0;
                    Apply(Search.Text);
                }));
            });
        }

        private void Apply(string text)
        {
            var t = (text ?? "").Trim();
            var skip = new HashSet<string>(_own.Concat(_linked), StringComparer.OrdinalIgnoreCase);
            var pool = _all.Where(k => !skip.Contains(k)).ToList();
            var list = t.Length == 0 ? pool
                : pool.Where(n => CultureInfo.CurrentCulture.CompareInfo.IndexOf(n, t, CompareOptions.IgnoreCase) >= 0).ToList();
            AllList.ItemsSource = list;
            CountText.Text = list.Count == pool.Count ? "Журналов: " + list.Count : "Найдено: " + list.Count + " из " + pool.Count;
            LinkButton.IsEnabled = false;
            DetailsText.Text = "";
        }

        private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Apply(Search.Text);

        private void Linked_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UnlinkButton.IsEnabled = LinkedList.SelectedItem is string k && _linked.Contains(k);
            if (UnlinkButton.IsEnabled) ShowDetails((string)LinkedList.SelectedItem);
        }

        private void All_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            LinkButton.IsEnabled = AllList.SelectedItem is string;
            if (AllList.SelectedItem is string key) ShowDetails(key);
        }

        /// <summary>Путь модели, кто писал и когда — у выбранного журнала (в фоне).</summary>
        private void ShowDetails(string key)
        {
            var version = ++_detailsVersion;
            DetailsText.Text = "…";
            Task.Run(() =>
            {
                var info = JournalLinkStore.Info(key);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _detailsVersion) return;
                    DetailsText.Text = "Модель: " + (string.IsNullOrEmpty(info.Path) ? "(путь не записан)" : info.Path) + Environment.NewLine +
                                       "Писали: " + (string.IsNullOrEmpty(info.Users) ? "—" : info.Users) +
                                       (string.IsNullOrEmpty(info.Period) ? "" : "; " + info.Period);
                }));
            });
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void All_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Link();

        private void Link_Click(object sender, RoutedEventArgs e) => Link();

        private void Link()
        {
            if (!(AllList.SelectedItem is string key)) return;
            var title = JournalLinks.NameOf(key);
            var answer = MessageBox.Show(this,
                "Связать журнал «" + key + "» с моделью «" + _vm.ModelTitle + "»?" + Environment.NewLine + Environment.NewLine +
                "Его записи будут видны вместе с журналом модели — у всех. Связь записывается в журнал (кто и когда); убрать её можно здесь же.",
                "Связать журналы", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _vm.LinkJournal(new JournalInfo { Key = key, Title = title });
        }

        private void Unlink_Click(object sender, RoutedEventArgs e)
        {
            if (!(LinkedList.SelectedItem is string key) || !_linked.Contains(key)) return;
            var answer = MessageBox.Show(this,
                "Убрать связь с журналом «" + key + "»?" + Environment.NewLine + Environment.NewLine +
                "Его записи перестанут быть видны вместе с журналом модели. Сам журнал не удаляется — связать снова можно здесь же.",
                "Отвязать журнал", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _vm.UnlinkJournal(new JournalInfo { Key = key, Title = JournalLinks.NameOf(key) });
        }
    }
}
