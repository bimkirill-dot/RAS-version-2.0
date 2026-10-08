using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.UI;

namespace RASv2.Tests
{
    /// <summary>
    /// RAS-Model History: удалённые элементы — «удалён вместе с …» (DeletionCascade), сведения из прежних записей журнала
    /// (UnknownElementResolver), кэш сведений между сеансами (ElementInfoCache); «Модель запомнена: N %» (WarmupProgress).
    /// </summary>
    internal static class ModelHistoryElementTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(5));

        public static IEnumerable<KeyValuePair<string, Action>> Deleted()
        {
            // стена 1 → дверь 2 (основа — стена) → марка двери 3; размер 4 — к стене; балка 5 удалена сама; марка 6 — у балки 7, которую не удаляли
            var parents = new Dictionary<long, long[]>
            {
                [2] = new long[] { 100, 1 },  // тип двери 100 не удаляли, основа — стена
                [3] = new long[] { 2 },
                [4] = new long[] { 1, 9 },
                [6] = new long[] { 7 }
            };
            IEnumerable<long> P(long id) => parents.TryGetValue(id, out var p) ? p : null;

            yield return Case("удалён вместе с: корень — самый верхний удалённый родитель", () =>
            {
                var roots = DeletionCascade.Roots(new[] { 1L, 2, 3, 4, 5, 6 }, P);
                Check(roots[2] == 1 && roots[3] == 1 && roots[4] == 1, "марка двери и размер — вместе со стеной: " +
                      string.Join(", ", roots.Select(x => x.Key + "→" + x.Value)));
                Check(!roots.ContainsKey(1) && !roots.ContainsKey(5), "стена и балка удалены сами");
                Check(!roots.ContainsKey(6), "марка, у которой родителя не удаляли, — удалена сама");
            });
            yield return Case("удалён вместе с: родитель не удалён — элемент удалён сам", () =>
            {
                var roots = DeletionCascade.Roots(new[] { 2L, 3 }, P);
                Check(!roots.ContainsKey(2) && roots[3] == 2, "дверь сама, марка — с дверью");
            });
            yield return Case("удалён вместе с: кольцо в данных не зацикливает", () =>
            {
                var loop = new Dictionary<long, long[]> { [1] = new long[] { 2 }, [2] = new long[] { 1 } };
                var roots = DeletionCascade.Roots(new[] { 1L, 2 }, id => loop[id]);
                Check(roots.Count == 2, "кольцо: " + roots.Count);
            });
        }

        private static ChangeRecord Rec(string id, int minutes, ChangeKind kind, long element, string name, string category = "Стены") =>
            new ChangeRecord { RecordId = id, Time = T0.AddMinutes(minutes), Kind = kind, ElementId = element, Name = name, Category = category, TypeName = "Тип " + name };

        public static IEnumerable<KeyValuePair<string, Action>> Unknown()
        {
            yield return Case("«нет сведений»: имя, категория и тип — из прежней записи об элементе", () =>
            {
                var created = Rec("a", 0, ChangeKind.Created, 10, "Стена 200");
                var renamed = Rec("b", 5, ChangeKind.Modified, 10, "Стена 250");
                var deleted = Rec("c", 9, ChangeKind.Deleted, 10, ChangeRecord.UnknownElementName, "");
                new UnknownElementResolver().Add(new[] { created, renamed, deleted });
                Check(deleted.Name == "Стена 250" && deleted.Category == "Стены" && deleted.TypeName == "Тип Стена 250" && deleted.InfoFromJournal,
                    "взято: " + deleted.Name + " / " + deleted.Category);
            });
            yield return Case("«нет сведений»: прежняя запись пришла позже — сведения подставляются тогда", () =>
            {
                var resolver = new UnknownElementResolver();
                var deleted = Rec("c", 9, ChangeKind.Deleted, 10, ChangeRecord.UnknownElementName, "");
                resolver.Add(new[] { deleted });
                Check(!deleted.InfoFromJournal, "подставлено без сведений");
                resolver.Add(new[] { Rec("a", 0, ChangeKind.Created, 10, "Дверь") });
                Check(deleted.Name == "Дверь" && deleted.InfoFromJournal, "не подставлено после прихода записи");
            });
            yield return Case("«нет сведений»: других записей об элементе нет — остаётся как есть", () =>
            {
                var deleted = Rec("c", 9, ChangeKind.Deleted, 11, ChangeRecord.UnknownElementName, "");
                new UnknownElementResolver().Add(new[] { Rec("a", 0, ChangeKind.Created, 10, "Дверь"), deleted });
                Check(deleted.Name == ChangeRecord.UnknownElementName && !deleted.InfoFromJournal, "выдуманы сведения");
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Cache()
        {
            yield return Case("кэш сведений об элементах: записано — прочитано так же", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "rasv2-eic-" + Guid.NewGuid().ToString("N"), "elements.dat");
                try
                {
                    var items = new List<ElementInfo>
                    {
                        new ElementInfo { Id = 1, UniqueId = "u1", Category = "Стены", ClassName = "Wall", Group = ObjectGroup.Model, Name = "Стена", TypeName = "Базовая стена : 200", Level = "Этаж 1", Workset = "04_Стены", Creator = "Иванов" },
                        new ElementInfo { Id = 2, Category = "Марки дверей", ClassName = "IndependentTag", Group = ObjectGroup.Annotation, Name = "Марка", OwnerView = "План 1", Parents = new long[] { 3, 1 }, IsNoise = false },
                        new ElementInfo { Id = 5, Category = "", ClassName = "SketchPlane", Name = "x", IsNoise = true }
                    };
                    ElementInfoCache.Save(path, items);
                    var back = ElementInfoCache.Load(path);
                    Check(back.Count == 3, "элементов: " + back.Count);
                    var a = back[1];
                    Check(a.UniqueId == "u1" && a.Category == "Стены" && a.TypeName == "Базовая стена : 200" && a.Level == "Этаж 1" &&
                          a.Workset == "04_Стены" && a.Creator == "Иванов" && a.Group == ObjectGroup.Model && a.Parents == null, "стена");
                    var b = back[2];
                    Check(b.UniqueId == null && b.OwnerView == "План 1" && b.Parents.SequenceEqual(new long[] { 3, 1 }) && b.Group == ObjectGroup.Annotation, "марка");
                    Check(back[5].IsNoise, "служебный");
                    Check(ElementInfoCache.Load(path + ".нет").Count == 0, "нет файла — пусто");
                }
                finally
                {
                    try { Directory.Delete(Path.GetDirectoryName(path), true); } catch (Exception) { /* временная папка */ }
                }
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Warmup()
        {
            yield return Case("«Модель запомнена»: шаг сведений — до 20 %, параметры — остальное, 100 % — только по окончании", () =>
            {
                Check(WarmupProgress.Percent(1, 0, 1000, false) == 0, "начало");
                Check(WarmupProgress.Percent(1, 500, 1000, false) == 10, "середина первого шага");
                Check(WarmupProgress.Percent(2, 0, 1000, false) == 20, "начало второго шага");
                Check(WarmupProgress.Percent(2, 500, 1000, false) == 60, "середина второго шага");
                Check(WarmupProgress.Percent(2, 1000, 1000, false) == 99, "без отметки «готово» не 100");
                Check(WarmupProgress.Percent(2, 10, 1000, true) == 100, "готово");
                Check(WarmupProgress.Percent(0, 0, 0, false) == 0, "ещё не начиналась");
                Check(WarmupProgress.Text(60, false) == "Модель запомнена: 60 %" && WarmupProgress.Text(100, true) == "✓ Модель запомнена", "подписи");
            });
        }
    }
}
