using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: группировка коллизий по элементу (ClashGrouping).</summary>
    internal static class ClashGroupingTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static ClashRow Row(string name, long a, long b, string modelA = "AR.nwc", string modelB = "OV.nwc") => new ClashRow
        {
            Name = name,
            A = new ElementView { ModelFile = modelA, ElementId = a, Category = "Стены" },
            B = new ElementView { ModelFile = modelB, ElementId = b, Category = "Воздуховоды" },
        };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("один воздуховод через три стены — одна группа по воздуховоду", () =>
            {
                var rows = new[] { Row("К1", 1, 100), Row("К2", 2, 100), Row("К3", 3, 100), Row("К4", 4, 200) };
                var groups = ClashGrouping.ByElement(rows);
                Check(groups.Count == 2, "групп: " + groups.Count);
                Check(groups[0].Key == "ov#100" && groups[0].Rows.Count == 3 && groups[0].Element.Category == "Воздуховоды", "группа воздуховода");
                Check(groups[1].Key == null && groups[1].Rows.Single().Name == "К4", "одиночная — в конце");
            });
            yield return Case("коллизия — в группе более частого элемента; поровну — первого", () =>
            {
                // стена 1 — в трёх коллизиях, воздуховод 100 — в двух: К2 (1×100) идёт к стене
                var rows = new[] { Row("К1", 1, 300), Row("К2", 1, 100), Row("К3", 1, 400), Row("К4", 5, 100), Row("К5", 6, 100) };
                var groups = ClashGrouping.ByElement(rows);
                var wall = groups.Single(g => g.Key == "ar#1");
                Check(wall.Rows.Select(r => r.Name).SequenceEqual(new[] { "К1", "К2", "К3" }), "стена: " + string.Join(",", wall.Rows.Select(r => r.Name)));
                var duct = groups.Single(g => g.Key == "ov#100");
                Check(duct.Rows.Count == 2, "воздуховоду — оставшиеся две");
            });
            yield return Case("группа из одной коллизии — к одиночным; одиночные — в исходном порядке", () =>
            {
                var rows = new[] { Row("К1", 1, 100), Row("К2", 2, 200), Row("К3", 3, 300) };
                var groups = ClashGrouping.ByElement(rows);
                Check(groups.Count == 1 && groups[0].Key == null, "только одиночные");
                Check(groups[0].Rows.Select(r => r.Name).SequenceEqual(new[] { "К1", "К2", "К3" }), "порядок");
            });
            yield return Case("одинаковый номер в разных моделях — разные элементы; без номера — не группируется", () =>
            {
                var rows = new[] { Row("К1", 7, 100, modelA: "AR.nwc"), Row("К2", 7, 200, modelA: "KJ.nwc") };
                Check(ClashGrouping.ByElement(rows).All(g => g.Key == null), "AR#7 и KJ#7 — разные");
                Check(ClashGrouping.Key(new ElementView { ModelFile = "AR.nwc" }) == null, "без номера — нет ключа");
            });
        }
    }
}
