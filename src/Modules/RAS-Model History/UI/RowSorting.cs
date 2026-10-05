using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Поле, по которому можно сортировать и группировать (как «Сортировка/группирование» в спецификации Revit).</summary>
    public enum SortField
    {
        Time,
        Kind,
        Author,
        Name,
        Category,
        Group,
        TypeName,
        Id,
        Transaction,
        Level,
        Workset,
        View,
        Source,
        Status,
        Machine,
        Creator,
        /// <summary>Значение параметра из «Столбца параметра».</summary>
        Param
    }

    /// <summary>Уровень сортировки: «Сортировать по: Автор, по возрастанию, с заголовком».</summary>
    public sealed class SortLevel
    {
        public SortLevel(SortField field, bool descending, bool header)
        {
            Field = field;
            Descending = descending;
            Header = header;
        }

        public SortField Field { get; }
        public bool Descending { get; }
        /// <summary>Строка-заголовок над каждой группой одинаковых значений (как «Заголовок» в Revit).</summary>
        public bool Header { get; }

        public SortLevel With(bool? descending = null, bool? header = null) =>
            new SortLevel(Field, descending ?? Descending, header ?? Header);
    }

    /// <summary>Заголовок группы в таблице: «Автор: Иванов (124)». Свёрнутость хранится по ключу и переживает обновление списка.</summary>
    public sealed class RowGroup : INotifyPropertyChanged
    {
        private readonly ISet<string> _collapsed;

        public RowGroup(string key, string title, int level, ISet<string> collapsed)
        {
            Key = key;
            Title = title;
            Level = level;
            _collapsed = collapsed;
        }

        public string Key { get; }
        public string Title { get; }
        /// <summary>0 — верхний уровень; вложенные заголовки сдвигаются вправо.</summary>
        public int Level { get; }

        public bool IsExpanded
        {
            get => _collapsed == null || !_collapsed.Contains(Key);
            set
            {
                if (_collapsed == null || value == IsExpanded) return;
                if (value) _collapsed.Remove(Key);
                else _collapsed.Add(Key);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // группы одной и той же строки сравниваются по ключу: так их объединяет группировка WPF
        public override bool Equals(object obj) => obj is RowGroup g && string.Equals(g.Key, Key, StringComparison.Ordinal);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key ?? "");
        public override string ToString() => Title;
    }

    /// <summary>
    /// Сортировка строк списка по нескольким уровням и разбивка на группы с заголовками — как в спецификации Revit.
    /// Операция Revit (заголовок + её записи) сортируется как одно целое, записи внутри — по тем же уровням.
    /// Без WPF и Revit: ключи и подписи даёт вызывающий код, поэтому логику можно проверить отдельно.
    /// </summary>
    public static class RowSorting
    {
        public const int MaxLevels = 4;

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly StringComparer Text = StringComparer.Create(Ru, true);

        public static string Label(SortField f)
        {
            switch (f)
            {
                case SortField.Time: return "Время";
                case SortField.Kind: return "Действие";
                case SortField.Author: return "Автор";
                case SortField.Name: return "Имя";
                case SortField.Category: return "Категория";
                case SortField.Group: return "Тип объекта";
                case SortField.TypeName: return "Тип / семейство";
                case SortField.Id: return "ID";
                case SortField.Transaction: return "Команда Revit";
                case SortField.Level: return "Уровень";
                case SortField.Workset: return "Рабочий набор";
                case SortField.View: return "Вид";
                case SortField.Source: return "Источник";
                case SortField.Status: return "Статус";
                case SortField.Machine: return "Компьютер";
                case SortField.Creator: return "Создатель элемента";
                case SortField.Param: return "Столбец параметра";
                default: return f.ToString();
            }
        }

        /// <summary>Сортировка по умолчанию: новые сверху.</summary>
        public static List<SortLevel> Default() => new List<SortLevel> { new SortLevel(SortField.Time, true, false) };

        public static bool IsDefault(IReadOnlyList<SortLevel> levels) =>
            levels == null || levels.Count == 0 ||
            (levels.Count == 1 && levels[0].Field == SortField.Time && levels[0].Descending && !levels[0].Header);

        /// <summary>«время ↓» или «автор ↑, время ↓».</summary>
        public static string Summary(IReadOnlyList<SortLevel> levels, string paramName = null)
        {
            if (levels == null || levels.Count == 0) return "время ↓";
            return string.Join(", ", levels.Select(l =>
                (l.Field == SortField.Param && !string.IsNullOrEmpty(paramName) ? "«" + paramName + "»" : Label(l.Field).ToLower(Ru)) +
                (l.Descending ? " ↓" : " ↑")));
        }

        /// <summary>Сравнение ключей: пустое — первым, текст — по-русски без учёта регистра, остальное — как есть.</summary>
        public static int CompareKeys(object a, object b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a is string sa && b is string sb) return Text.Compare(sa, sb);
            if (a is IComparable ca && a.GetType() == b.GetType()) return ca.CompareTo(b);
            return Text.Compare(a.ToString(), b.ToString());
        }

        private sealed class Unit
        {
            public RowEntry Head;
            public List<RowEntry> Children;
            public IReadOnlyList<ChangeRecord> Members;
            public object[] Keys;
            public int Index;
        }

        private static List<Unit> Units(IList<RowEntry> entries)
        {
            var units = new List<Unit>();
            Unit current = null;
            foreach (var e in entries)
            {
                if (e.IsChild && current != null && current.Head.IsHeader && current.Head.Key == e.Key)
                {
                    current.Children.Add(e);
                    continue;
                }
                current = new Unit
                {
                    Head = e,
                    Children = new List<RowEntry>(),
                    Members = e.IsHeader && e.Members != null ? (IReadOnlyList<ChangeRecord>)e.Members : new[] { e.Record },
                    Index = units.Count
                };
                units.Add(current);
            }
            return units;
        }

        /// <summary>
        /// Отсортировать строки по уровням. <paramref name="key"/> — ключ уровня для записей строки
        /// (для операции — все её записи, для обычной строки — одна).
        /// Сортировка устойчивая: при равных ключах сохраняется исходный порядок.
        /// </summary>
        public static List<RowEntry> Sort(IList<RowEntry> entries, IReadOnlyList<SortLevel> levels,
            Func<IReadOnlyList<ChangeRecord>, SortField, object> key)
        {
            if (levels == null || levels.Count == 0) return entries.ToList();
            var units = Units(entries);
            foreach (var u in units) u.Keys = levels.Select(l => key(u.Members, l.Field)).ToArray();
            Comparison<Unit> cmp = (x, y) =>
            {
                for (int i = 0; i < levels.Count; i++)
                {
                    var c = CompareKeys(x.Keys[i], y.Keys[i]);
                    if (c != 0) return levels[i].Descending ? -c : c;
                }
                return x.Index.CompareTo(y.Index);
            };
            units.Sort(cmp);

            var result = new List<RowEntry>(entries.Count);
            foreach (var u in units)
            {
                result.Add(u.Head);
                if (u.Children.Count == 0) continue;
                var children = u.Children
                    .Select((c, i) => new Unit { Head = c, Members = new[] { c.Record }, Index = i, Children = new List<RowEntry>() })
                    .ToList();
                foreach (var c in children) c.Keys = levels.Select(l => key(c.Members, l.Field)).ToArray();
                children.Sort(cmp);
                result.AddRange(children.Select(c => c.Head));
            }
            return result;
        }

        /// <summary>
        /// Разбить отсортированные строки на группы с заголовками — как в Revit: новая группа начинается там,
        /// где меняется значение уровня с «Заголовком» или любого уровня выше него. Поэтому порядок групп
        /// всегда совпадает с порядком сортировки, даже если выше заголовка стоит уровень без заголовка.
        /// <paramref name="title"/> — значение для подписи («Иванов», «28.09.2026»), <paramref name="key"/> — ключ сортировки.
        /// Для каждой строки — её группы сверху вниз: ключ (не зависит от числа записей, чтобы свёрнутость
        /// переживала обновление списка) и подпись «Автор: Иванов (124)». null — заголовков нет.
        /// </summary>
        public static List<(string Key, string Title)[]> Groups(IList<RowEntry> sorted, IReadOnlyList<SortLevel> levels,
            Func<IReadOnlyList<ChangeRecord>, SortField, string> title, Func<IReadOnlyList<ChangeRecord>, SortField, object> key)
        {
            if (levels == null || !levels.Any(l => l.Header)) return null;
            var headerCount = levels.Count(l => l.Header);
            var units = Units(sorted);
            var prevPath = new string[headerCount];
            var prevRun = new string[headerCount];
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var perUnit = new List<(string Key, string Value)[]>(units.Count);
            foreach (var u in units)
            {
                var info = new (string Key, string Value)[headerCount];
                var prefix = "";
                var h = 0;
                var changed = false;
                for (int j = 0; j < levels.Count && h < headerCount; j++)
                {
                    var l = levels[j];
                    if (!l.Header)
                    {
                        prefix += "\u001f" + Convert.ToString(key(u.Members, l.Field), CultureInfo.InvariantCulture);
                        continue;
                    }
                    var v = title(u.Members, l.Field);
                    if (string.IsNullOrWhiteSpace(v)) v = "(пусто)";
                    var path = prefix + "\u001e" + (int)l.Field + "\u001d" + v.ToLower(Ru);
                    if (changed || path != prevPath[h])
                    {
                        changed = true; // со сменой группы начинаются и новые вложенные группы
                        occurrences.TryGetValue(path, out var occ);
                        occurrences[path] = occ + 1;
                        prevPath[h] = path;
                        prevRun[h] = path + "#" + occ;
                    }
                    var run = prevRun[h];
                    counts.TryGetValue(run, out var n);
                    counts[run] = n + u.Members.Count;
                    info[h] = (run, v);
                    prefix = run; // вложенные группы — внутри этой
                    h++;
                }
                perUnit.Add(info);
            }

            var headerLevels = levels.Where(l => l.Header).ToList();
            var result = new List<(string Key, string Title)[]>(sorted.Count);
            for (int ui = 0; ui < units.Count; ui++)
            {
                var info = new (string Key, string Title)[headerCount];
                for (int i = 0; i < headerCount; i++)
                {
                    var (k, v) = perUnit[ui][i];
                    info[i] = (k, $"{Label(headerLevels[i].Field)}: {v} ({counts[k]})");
                }
                var rows = 1 + units[ui].Children.Count;
                for (int r = 0; r < rows; r++) result.Add(info);
            }
            return result;
        }
    }
}
