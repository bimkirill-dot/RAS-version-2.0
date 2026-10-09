using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>Группа коллизий одного элемента: «Воздуховод 10223416 — 10 коллизий».</summary>
    public sealed class ElementGroup
    {
        /// <summary>«файл модели#номер»; null — коллизии, у которых общего элемента с другими нет (по одной).</summary>
        public string Key { get; set; }
        public ElementView Element { get; set; }
        public List<ClashRow> Rows { get; } = new List<ClashRow>();
    }

    /// <summary>
    /// Группировка по элементу (DECISIONS.md модуля): один воздуховод через десять стен — десять коллизий, а решение одно.
    /// Коллизия — в группе того из двух своих элементов, который чаще встречается в списке (поровну — первого);
    /// группа из одной коллизии — к одиночным. Группы — по числу коллизий, одиночные — в конце. Без базы — с тестами.
    /// </summary>
    public static class ClashGrouping
    {
        public static List<ElementGroup> ByElement(IReadOnlyList<ClashRow> rows)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
                foreach (var key in Keys(r).Distinct())
                    counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

            var groups = new Dictionary<string, ElementGroup>(StringComparer.Ordinal);
            var singles = new ElementGroup();
            foreach (var r in rows)
            {
                var best = Candidates(r).Where(x => x.Key != null)
                    .OrderByDescending(x => counts[x.Key]).FirstOrDefault();
                if (best.Key == null || counts[best.Key] < 2)
                {
                    singles.Rows.Add(r);
                    continue;
                }
                if (!groups.TryGetValue(best.Key, out var g)) groups[best.Key] = g = new ElementGroup { Key = best.Key, Element = best.Element };
                g.Rows.Add(r);
            }
            // элемент мог «отдать» свои коллизии более частому соседу — группа из одной не нужна
            foreach (var g in groups.Values.Where(g => g.Rows.Count < 2).ToList())
            {
                singles.Rows.AddRange(g.Rows);
                groups.Remove(g.Key);
            }
            var result = groups.Values.OrderByDescending(g => g.Rows.Count)
                .ThenBy(g => g.Element?.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (singles.Rows.Count > 0)
            {
                // одиночные — в порядке исходного списка
                var order = rows.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
                singles.Rows.Sort((a, b) => order[a].CompareTo(order[b]));
                result.Add(singles);
            }
            return result;
        }

        /// <summary>Элемент — «файл модели#номер»; без номера — не участвует.</summary>
        public static string Key(ElementView e) =>
            e?.ElementId == null ? null : ClashRules.ModelName(e.ModelFile).ToLowerInvariant() + "#" + e.ElementId.Value;

        private static IEnumerable<string> Keys(ClashRow r) => Candidates(r).Where(x => x.Key != null).Select(x => x.Key);

        /// <summary>Элементы коллизии по порядку (A раньше B — при равенстве выигрывает A).</summary>
        private static IEnumerable<(string Key, ElementView Element)> Candidates(ClashRow r)
        {
            yield return (Key(r.A), r.A);
            yield return (Key(r.B), r.B);
        }
    }
}
