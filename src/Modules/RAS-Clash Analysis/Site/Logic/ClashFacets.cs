using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>Значение отбора: ключ, как показывать, сколько коллизий.</summary>
    public sealed class FacetValue
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Отборы списка коллизий — как у баз данных и аналогов (ACC, Revizto, GitHub): у каждого поля список значений
    /// с флажками и числом коллизий; несколько значений одного поля — «или», разные поля — «и».
    /// Числа у значений поля считаются с учётом всех остальных отборов, кроме самого этого поля. Без базы — с тестами.
    /// </summary>
    public static class ClashFacets
    {
        public const string Level = "level";
        public const string Category = "category";
        public const string Report = "report";
        public const string State = "state";

        public const string InLastReport = "in";
        public const string NotInLastReport = "out";

        /// <summary>Поля на странице; «Отчёт» убран (решение разработчика 09.10.2026) — проверки его остались.</summary>
        public static readonly string[] All = { Level, State };

        public static string Title(string facet) => facet switch
        {
            Level => "Уровень",
            Category => "Категория",
            Report => "Отчёт",
            State => "Статус",
            _ => facet,
        };

        /// <summary>Значения поля у коллизии (у каждого из двух элементов — своё): ключ и как показывать.</summary>
        public static IEnumerable<(string Key, string Title)> ValuesOf(ClashRow c, string facet)
        {
            switch (facet)
            {
                case Level:
                    foreach (var e in Elements(c))
                        if (!string.IsNullOrEmpty(e.LevelKey)) yield return (e.LevelKey, e.Level);
                    break;
                case Category:
                    foreach (var e in Elements(c))
                        if (!string.IsNullOrEmpty(e.Category)) yield return (e.Category, e.Category);
                    break;
                case Report:
                    yield return c.InLastReport ? (InLastReport, "Есть в последнем отчёте") : (NotInLastReport, "Нет в последнем отчёте");
                    break;
                case State:
                    yield return (((int)c.State).ToString(), ClashRules.Title(c.State));
                    break;
            }
        }

        /// <summary>Коллизия проходит отборы; except — поле, которое не учитывать (для чисел у его значений).</summary>
        public static bool Matches(ClashRow c, IReadOnlyDictionary<string, HashSet<string>> selected, string except = null)
        {
            foreach (var pair in selected)
            {
                if (pair.Key == except || pair.Value == null || pair.Value.Count == 0) continue;
                if (!ValuesOf(c, pair.Key).Any(v => pair.Value.Contains(v.Key))) return false;
            }
            return true;
        }

        /// <summary>Значения поля с числом коллизий; порядок — уровни по номеру, остальное — по числу.</summary>
        public static List<FacetValue> Count(IEnumerable<ClashRow> rows, string facet)
        {
            var counts = new Dictionary<string, (int Count, Dictionary<string, int> Titles)>(StringComparer.Ordinal);
            foreach (var c in rows)
                foreach (var g in ValuesOf(c, facet).GroupBy(v => v.Key, StringComparer.Ordinal))
                {
                    counts.TryGetValue(g.Key, out var x);
                    x.Titles ??= new Dictionary<string, int>();
                    x.Count++;
                    foreach (var v in g) x.Titles[v.Title ?? g.Key] = x.Titles.TryGetValue(v.Title ?? g.Key, out var n) ? n + 1 : 1;
                    counts[g.Key] = x;
                }
            var list = counts.Select(kv => new FacetValue
            {
                Key = kv.Key,
                // написаний одного уровня бывает несколько («5 Этаж», «05 Этаж») — показываем самое частое
                Title = kv.Value.Titles.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key,
                Count = kv.Value.Count,
            });
            if (facet == State)
                return list.OrderBy(v => StateOrder(v.Key)).ToList();
            return facet == Level
                ? list.OrderBy(v => LevelOrder(v.Key)).ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
                : facet == Report
                    ? list.OrderBy(v => v.Key == InLastReport ? 0 : 1).ToList()
                    : list.OrderByDescending(v => v.Count).ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Поиск: номер элемента, тип, имя, семейство, категория, модель, имя коллизии — без учёта регистра.</summary>
        public static bool MatchesSearch(ClashRow c, string search)
        {
            var s = search?.Trim();
            if (string.IsNullOrEmpty(s)) return true;
            bool Has(string v) => v != null && v.IndexOf(s, StringComparison.CurrentCultureIgnoreCase) >= 0;
            return Has(c.Name) || Has(c.CommentsText) || Elements(c).Any(e => Has(e.ElementId?.ToString()) || Has(e.Name) || Has(e.Type) || Has(e.Family)
                                                       || Has(e.ModelFile) || Has(e.Category));
        }

        /// <summary>Подпись выбранного на кнопке поля: «2 Этаж», «2 Этаж, 3 Этаж», «2 Этаж, 3 Этаж +2».</summary>
        public static string Summary(IReadOnlyCollection<string> selectedKeys, IEnumerable<FacetValue> values)
        {
            var titles = values.Where(v => selectedKeys.Contains(v.Key)).Select(v => v.Title).ToList();
            // выбранное могло пропасть из списка (другие отборы) — всё равно считается
            var missing = selectedKeys.Count - titles.Count;
            titles.AddRange(Enumerable.Repeat("…", Math.Max(missing, 0)));
            return titles.Count <= 2 ? string.Join(", ", titles) : string.Join(", ", titles.Take(2)) + " +" + (titles.Count - 2);
        }

        private static IEnumerable<ElementView> Elements(ClashRow c)
        {
            if (c.A != null) yield return c.A;
            if (c.B != null) yield return c.B;
        }

        /// <summary>Статусы по ходу работы: активная, исправлена, допущена.</summary>
        private static int StateOrder(string key) => key == "0" ? 0 : key == "1" ? 1 : 2;

        private static double LevelOrder(string key)
        {
            var end = 0;
            while (end < key.Length && (char.IsDigit(key[end]) || (end == 0 && key[end] == '-'))) end++;
            return double.TryParse(key.Substring(0, end), out var v) ? v : double.MaxValue;
        }
    }
}
