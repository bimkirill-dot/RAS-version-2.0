using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: отборы списка коллизий (ClashFacets) и раздел по имени модели.</summary>
    internal static class ClashFacetTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static ClashRow Row(string levelA, string levelB, string catA, string catB, string modelA = "3457_01_01_S8_AR01_01_M3.nwc",
            string modelB = "3457_01_01_S8_OV01_01_М3.nwc", bool inLast = true, long idA = 1) => new ClashRow
        {
            Name = "Конфликт" + idA, InLastReport = inLast,
            A = new ElementView { Level = levelA, LevelKey = ClashRules.LevelKey(levelA), Category = catA, ModelFile = modelA, ElementId = idA, Type = "Базовая стена" },
            B = new ElementView { Level = levelB, LevelKey = ClashRules.LevelKey(levelB), Category = catB, ModelFile = modelB, ElementId = idA + 1000 },
        };

        private static Dictionary<string, HashSet<string>> Sel(params (string Facet, string[] Keys)[] s) =>
            s.ToDictionary(x => x.Facet, x => new HashSet<string>(x.Keys));

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("несколько значений одного поля — «или», разные поля — «и»", () =>
            {
                var a = Row("1 Этаж", "1 Этаж", "Стены", "Трубы");
                var b = Row("2 Этаж", "2 Этаж", "Стены", "Воздуховоды");
                var c = Row("3 Этаж", "3 Этаж", "Перекрытия", "Трубы");
                var s = Sel((ClashFacets.Level, new[] { "1 этаж", "2 этаж" }));
                Check(ClashFacets.Matches(a, s) && ClashFacets.Matches(b, s) && !ClashFacets.Matches(c, s), "уровни 1 или 2");
                s = Sel((ClashFacets.Level, new[] { "1 этаж", "2 этаж" }), (ClashFacets.Category, new[] { "Трубы" }));
                Check(ClashFacets.Matches(a, s) && !ClashFacets.Matches(b, s) && !ClashFacets.Matches(c, s), "и трубы");
                Check(ClashFacets.Matches(b, s, except: ClashFacets.Category), "без учёта своего поля");
            });
            yield return Case("коллизия — по любому из двух элементов; «05 Этаж» и «5 Этаж» — одно значение", () =>
            {
                var rows = new[] { Row("5 Этаж", "05 Этаж", "Стены", "Трубы"), Row("5 Этаж", "5 Этаж", "Стены", "Трубы"), Row("05 Этаж", "06 Этаж", "Стены", "Трубы") };
                var levels = ClashFacets.Count(rows, ClashFacets.Level);
                Check(levels.Count == 2, "значений: " + levels.Count);
                Check(levels[0].Key == "5 этаж" && levels[0].Count == 3 && levels[0].Title == "5 Этаж", "5 этаж — 3 коллизии, самое частое написание");
                Check(levels[1].Count == 1, "6 этаж — 1");
            });
            yield return Case("уровни — по номеру, остальное — по числу коллизий", () =>
            {
                var rows = new[] { Row("100 Кровля", "100 Кровля", "Трубы", "Трубы"), Row("-01 Подвал", "-01 Подвал", "Стены", "Стены"),
                                   Row("2 Этаж", "2 Этаж", "Стены", "Стены") };
                var levels = ClashFacets.Count(rows, ClashFacets.Level).Select(v => v.Key).ToList();
                Check(levels.SequenceEqual(new[] { "-1 подвал", "2 этаж", "100 кровля" }), "порядок: " + string.Join(", ", levels));
                Check(ClashFacets.Count(rows, ClashFacets.Category)[0].Title == "Стены", "чаще — выше");
            });
            yield return Case("отчёт: есть / нет в последнем", () =>
            {
                var rows = new[] { Row("1", "1", "Стены", "Стены", inLast: true), Row("1", "1", "Стены", "Стены", inLast: false) };
                var s = Sel((ClashFacets.Report, new[] { ClashFacets.NotInLastReport }));
                Check(!ClashFacets.Matches(rows[0], s) && ClashFacets.Matches(rows[1], s), "только пропавшие");
                Check(ClashFacets.Count(rows, ClashFacets.Report)[0].Key == ClashFacets.InLastReport, "«есть» — первым");
            });
            yield return Case("тип элемента — точно по категории, семейству и типу, а не по части названия", () =>
            {
                var a = Row("1 Этаж", "1 Этаж", "Стены", "Перекрытия");
                a.A.Family = "Базовая стена"; a.A.Type = "Бетон_200";
                var b = Row("1 Этаж", "1 Этаж", "Стены", "Перекрытия", idA: 2);
                b.A.Family = "Базовая стена"; b.A.Type = "Бетон_200_утепл";
                var key = ClashFacets.ElementTypeKey(a.A);
                var sel = Sel((ClashFacets.ElementType, new[] { key }));
                Check(ClashFacets.Matches(a, sel) && !ClashFacets.Matches(b, sel), "Бетон_200 ≠ Бетон_200_утепл");
                Check(ClashFacets.ElementTypeTitle(key) == "Бетон_200", "подпись — тип");
                Check(ClashFacets.ElementTypeKey(new ElementView { Category = "Стены" }) == null, "без семейства и типа — нет ключа");
            });
            yield return Case("поиск: номер элемента, тип, модель — без учёта регистра", () =>
            {
                var r = Row("1", "1", "Стены", "Трубы", idA: 5556166);
                Check(ClashFacets.MatchesSearch(r, "5556166") && ClashFacets.MatchesSearch(r, "базовая") && ClashFacets.MatchesSearch(r, "ar01"), "находит");
                Check(!ClashFacets.MatchesSearch(r, "нет такого") && ClashFacets.MatchesSearch(r, "  "), "не находит / пусто");
                r.CommentsText = "Согласовано с ОВ: переносим воздуховод";
                Check(ClashFacets.MatchesSearch(r, "согласовано") && ClashFacets.MatchesSearch(r, "ов:"), "находит по комментарию");
            });
            yield return Case("подпись на кнопке: два значения и «+N»", () =>
            {
                var values = new[] { new FacetValue { Key = "a", Title = "1 Этаж" }, new FacetValue { Key = "b", Title = "2 Этаж" },
                                     new FacetValue { Key = "c", Title = "3 Этаж" } };
                Check(ClashFacets.Summary(new[] { "a" }, values) == "1 Этаж", "одно");
                Check(ClashFacets.Summary(new[] { "a", "b", "c" }, values) == "1 Этаж, 2 Этаж +1", "три");
            });
        }
    }
}
