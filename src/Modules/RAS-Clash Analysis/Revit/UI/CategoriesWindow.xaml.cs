using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «Категории элементов»: галочки категорий модели (и подкатегорий) для исходного файла и для связанного; поиск, «Показать
    /// отмеченные», «Выбрать все» / «Снять выделение» / «Обратить выбор» у каждого списка, импорт и экспорт (CategoryFile),
    /// «вернуть по умолчанию». Скрытые категории, которых нет в этой модели, сохраняются как были. Подкатегория — одна галочка на
    /// оба списка (Revit скрывает её сразу везде). Кнопки, поля, кисти — общие образцы ядра.
    /// </summary>
    public partial class CategoriesWindow : Window
    {
        /// <summary>Строка списка — общая для обоих списков: в левом — галочка Host, в правом — Link.</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private bool _host = true, _link = true, _expanded;

            public string Key { get; set; }
            public string Name { get; set; }
            public Row Parent { get; set; }
            public List<Row> Children { get; } = new List<Row>();
            public bool IsSub => Parent != null;
            public bool HasChildren => Children.Count > 0;
            public string Tip => IsSub
                ? "Подкатегорию Revit скрывает сразу и в исходном файле, и в связанном"
                : "Галочка стоит — категория видна в виде коллизии, нет — скрыта. Элементы самой коллизии видны всегда";

            public bool Host
            {
                get => _host;
                set
                {
                    if (_host == value) return;
                    _host = value;
                    Changed();
                    if (IsSub) Link = value;
                }
            }

            public bool Link
            {
                get => _link;
                set
                {
                    if (_link == value) return;
                    _link = value;
                    Changed();
                    if (IsSub) Host = value;
                }
            }

            public bool Expanded
            {
                get => _expanded;
                set
                {
                    if (_expanded == value) return;
                    _expanded = value;
                    Changed();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<string, Row> _byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
        private readonly ListCollectionView _hostView, _linkView;
        // скрытые категории, которых нет в открытой модели (из другой модели, из файла коллеги), — не теряются
        private List<string> _otherHost = new List<string>(), _otherLink = new List<string>();
        // много галочек сразу — счётчики один раз в конце
        private bool _bulk;

        public List<string> HiddenHost { get; private set; }
        public List<string> HiddenLink { get; private set; }

        public CategoriesWindow(IEnumerable<CategoryItem> items, IEnumerable<string> hiddenHost, IEnumerable<string> hiddenLink)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            foreach (var item in items)
            {
                if (_byKey.ContainsKey(item.Key)) continue;
                var sub = !string.IsNullOrEmpty(item.Parent);
                var parent = sub && _byKey.TryGetValue(item.Parent, out var p) ? p : null;
                if (sub && parent == null) continue;
                var row = new Row { Key = item.Key, Name = item.Name, Parent = parent };
                parent?.Children.Add(row);
                row.PropertyChanged += Row_Changed;
                _rows.Add(row);
                _byKey[row.Key] = row;
            }
            Set(hiddenHost, hiddenLink);
            _hostView = new ListCollectionView(_rows) { Filter = o => Show((Row)o, true) };
            _linkView = new ListCollectionView(_rows) { Filter = o => Show((Row)o, false) };
            HostList.ItemsSource = _hostView;
            LinkList.ItemsSource = _linkView;
            Counts();
            Loaded += (s, e) => Search.Focus();
        }

        /// <summary>Галочки по спискам скрытых; подкатегория скрыта хоть в одном — снята в обоих.</summary>
        private void Set(IEnumerable<string> hiddenHost, IEnumerable<string> hiddenLink)
        {
            var host = new HashSet<string>(hiddenHost ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var link = new HashSet<string>(hiddenLink ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            _bulk = true;
            foreach (var row in _rows)
            {
                var sub = row.IsSub && (host.Contains(row.Key) || link.Contains(row.Key));
                row.Host = !(sub || host.Contains(row.Key));
                row.Link = !(sub || link.Contains(row.Key));
            }
            _bulk = false;
            _otherHost = host.Where(k => !_byKey.ContainsKey(k)).ToList();
            _otherLink = link.Where(k => !_byKey.ContainsKey(k)).ToList();
        }

        private List<string> Hidden(bool host) =>
            CategoryChoice.Clean(_rows.Where(r => !(host ? r.Host : r.Link)).Select(r => r.Key).Concat(host ? _otherHost : _otherLink));

        /// <summary>
        /// Строка в списке: поиск — категория, если нашлась она или её подкатегория; подкатегория — если нашлась она (или
        /// нашлась категория и она раскрыта). Без поиска подкатегории — у раскрытой категории. «Показать отмеченные» — только с
        /// галочкой в этом списке.
        /// </summary>
        private bool Show(Row row, bool host)
        {
            var q = (Search.Text ?? "").Trim();
            bool Match(Row r) => q.Length == 0 || r.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
            if (!row.IsSub)
            {
                if (q.Length > 0 && !Match(row) && !row.Children.Any(Match)) return false;
            }
            else if (q.Length > 0 ? !(Match(row) || (Match(row.Parent) && row.Parent.Expanded)) : !row.Parent.Expanded) return false;
            return OnlyChecked.IsChecked != true || (host ? row.Host : row.Link);
        }

        private void Refresh()
        {
            _hostView?.Refresh();
            _linkView?.Refresh();
        }

        private void Counts()
        {
            HostCount.Text = Count(Hidden(true).Count);
            LinkCount.Text = Count(Hidden(false).Count);
        }

        private static string Count(int hidden) => hidden == 0 ? "Видно всё" : "Скрыто: " + hidden;

        private void Row_Changed(object sender, PropertyChangedEventArgs e)
        {
            // галочки «Показать отмеченные» не перестраивают сразу — строка не пропадает из-под мыши
            if (e.PropertyName == nameof(Row.Expanded)) Refresh();
            else if (!_bulk) Counts();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (SearchHint == null || _hostView == null) return; // во время InitializeComponent
            SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        private IEnumerable<Row> Shown(object sender, out bool host)
        {
            host = (string)((FrameworkElement)sender).Tag == "host";
            return (host ? _hostView : _linkView).Cast<Row>().ToList();
        }

        private void All_Click(object sender, RoutedEventArgs e) => Mark(sender, _ => true);
        private void None_Click(object sender, RoutedEventArgs e) => Mark(sender, _ => false);
        private void Invert_Click(object sender, RoutedEventArgs e) => Mark(sender, v => !v);

        private void Mark(object sender, Func<bool, bool> value)
        {
            var rows = Shown(sender, out var host);
            _bulk = true;
            foreach (var row in rows)
                if (host) row.Host = value(row.Host);
                else row.Link = value(row.Link);
            _bulk = false;
            Refresh();
            Counts();
        }

        private void Defaults_Click(object sender, RoutedEventArgs e)
        {
            Set(null, null);
            Refresh();
            Counts();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Импорт категорий элементов", Filter = FileFilter };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var file = CategoryFile.Load(dialog.FileName);
                Set(file.HiddenHost, file.HiddenLink);
                Refresh();
                Counts();
                HideError();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                ShowError("Не удалось импортировать: " + ex.Message);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Title = "Экспорт категорий элементов", Filter = FileFilter, FileName = "Категории вида.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                CategoryFile.Save(dialog.FileName, Hidden(true), Hidden(false));
                HideError();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError("Не удалось экспортировать: " + ex.Message);
            }
        }

        private const string FileFilter = "Категории вида (*.json)|*.json|Все файлы|*.*";

        private void ShowError(string text)
        {
            Error.Text = text;
            Error.Visibility = Visibility.Visible;
        }

        private void HideError() => Error.Visibility = Visibility.Collapsed;

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            HiddenHost = Hidden(true);
            HiddenLink = Hidden(false);
            DialogResult = true;
        }
    }
}
