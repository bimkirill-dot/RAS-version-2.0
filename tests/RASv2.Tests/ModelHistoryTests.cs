using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ModelHistory.Storage;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.UI;

namespace RASv2.Tests
{
    /// <summary>
    /// Логика модуля RAS-Model History без Revit: фильтр, пары «правка + отмена», актуализация, «Без дублей»,
    /// итог за период, перенос личных папок. Пока модуль работает только в Revit 2026, проверки собираются только под 2026.
    /// </summary>
    internal static class ModelHistoryTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(5));

        private static ChangeRecord R(string id, int minutes, ChangeKind kind, long element, string tx,
            OperationKind op = OperationKind.Commit, string session = "s1", string user = "Kirill_BIM",
            ChangeSource source = ChangeSource.Live, string uid = null, string by = null) =>
            new ChangeRecord
            {
                RecordId = id, Time = T0.AddMinutes(minutes), Kind = kind, ElementId = element, Transaction = tx, Operation = op,
                Session = session, User = user, Machine = "PC", Source = source, UniqueId = uid, RecordedBy = by
            };

        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        public static IEnumerable<KeyValuePair<string, Action>> Filter()
        {
            yield return Case("числа сравниваются как числа", () =>
            {
                Check(RecordFilter.Compare("3833", FilterOperator.Greater, "3000"), "3833 > 3000");
                Check(RecordFilter.Compare("7325,31 см³", FilterOperator.Less, "8000"), "7325,31 см³ < 8000");
                Check(RecordFilter.Compare("1 200 мм", FilterOperator.Equals, "1200"), "1 200 мм = 1200");
                Check(RecordFilter.Compare("-150", FilterOperator.Less, "0"), "-150 < 0");
            });
            yield return Case("«12_А500» и «2А» — текст, а не числа", () =>
            {
                Check(!RecordFilter.Compare("12_А500", FilterOperator.Equals, "12"), "12_А500 ≠ 12");
                Check(!RecordFilter.Compare("2А", FilterOperator.Equals, "2"), "2А ≠ 2");
            });
            yield return Case("текст — без учёта регистра", () =>
            {
                Check(RecordFilter.Compare("Стены", FilterOperator.Equals, "стены"), "равно");
                Check(RecordFilter.Compare("Несущая арматура", FilterOperator.Contains, "арм"), "содержит");
                Check(RecordFilter.Compare("Несущая арматура", FilterOperator.NotContains, "стен"), "не содержит");
                Check(RecordFilter.Compare("Несущая арматура", FilterOperator.BeginsWith, "несущ"), "начинается с");
                Check(RecordFilter.Compare("Несущая арматура", FilterOperator.EndsWith, "ТУРА"), "заканчивается на");
                Check(RecordFilter.Compare("Кровля", FilterOperator.Greater, "Антресоль"), "больше (текст)");
            });
            yield return Case("нет параметра и пустое значение", () =>
            {
                Check(!RecordFilter.Compare(null, FilterOperator.NotEquals, "x"), "нет параметра: «не равно» не подходит");
                Check(RecordFilter.Compare(null, FilterOperator.HasNoValue, null), "нет параметра: «не имеет значения»");
                Check(RecordFilter.Compare("", FilterOperator.NotEquals, "x"), "пусто: «не равно x»");
                Check(!RecordFilter.Compare("", FilterOperator.HasValue, null), "пусто: «имеет значение» не подходит");
            });

            var rebar = new ChangeRecord
            {
                RecordId = "r1", Kind = ChangeKind.Modified, Category = "Несущая арматура", Level = "Этаж 2", ElementId = 10,
                Params = new List<ParamChange> { new ParamChange { Name = "Длина стержня", Old = "3833", New = "3897" } }
            };
            var wall = new ChangeRecord { RecordId = "r2", Kind = ChangeKind.Modified, Category = "Стены", Level = "Этаж 1", ElementId = 11 };
            var deleted = new ChangeRecord
            {
                RecordId = "r3", Kind = ChangeKind.Deleted, Category = "Стены", ElementId = 12,
                State = new List<ParamValue> { new ParamValue { Name = "Марка", Value = "К-1" } }
            };
            var all = new[] { rebar, wall, deleted };
            Func<ChangeRecord, FilterField, string> field = (r, f) =>
                f == FilterField.Category ? r.Category : f == FilterField.Level ? r.Level : "";
            Func<ChangeRecord, string, string> param = (r, n) =>
                r.Params?.FirstOrDefault(p => p.Name == n)?.New ?? r.State?.FirstOrDefault(p => p.Name == n)?.Value ??
                (r.ElementId == 11 && n == "Марка" ? "К-2" : null); // «из модели»
            string Ids(RecordFilter f) => string.Join(",", all.Where(r => f.Keep(r, field, param)).Select(r => r.RecordId));
            var byWalls = new[]
            {
                new FilterCondition(FilterField.Category, FilterOperator.Equals, "Стены"),
                new FilterCondition(FilterField.Level, FilterOperator.Equals, "Этаж 1")
            };

            yield return Case("«И» и «ИЛИ»", () =>
            {
                Check(Ids(new RecordFilter(byWalls, false)) == "r2", "И: " + Ids(new RecordFilter(byWalls, false)));
                Check(Ids(new RecordFilter(byWalls, true)) == "r2,r3", "ИЛИ: " + Ids(new RecordFilter(byWalls, true)));
            });
            yield return Case("«Изменён параметр» равно / не равно", () =>
            {
                var eq = new RecordFilter(new[] { new FilterCondition(FilterField.ChangedParam, FilterOperator.Equals, "длина стержня") }, false);
                var ne = new RecordFilter(new[] { new FilterCondition(FilterField.ChangedParam, FilterOperator.NotEquals, "Длина стержня") }, false);
                Check(Ids(eq) == "r1", "равно: " + Ids(eq));
                Check(Ids(ne) == "r2,r3", "не равно: " + Ids(ne));
            });
            yield return Case("«Было» и «Стало»", () =>
            {
                var after = new RecordFilter(new[] { new FilterCondition(FilterField.After, FilterOperator.Greater, "3850", "Длина стержня") }, false);
                var before = new RecordFilter(new[] { new FilterCondition(FilterField.Before, FilterOperator.Greater, "3850", "Длина стержня") }, false);
                Check(Ids(after) == "r1", "стало: " + Ids(after));
                Check(Ids(before) == "", "было: " + Ids(before));
                Check(after.Conditions[0].Text == "Стало (Длина стержня) больше «3850»", "текст значка: " + after.Conditions[0].Text);
            });
            yield return Case("параметр элемента — из журнала и из модели", () =>
            {
                var mark = new RecordFilter(new[] { new FilterCondition(FilterField.Param, FilterOperator.BeginsWith, "К-", "Марка") }, false);
                Check(Ids(mark) == "r2,r3", "марка: " + Ids(mark));
                Check(mark.NeedsModel && !new RecordFilter(byWalls, false).NeedsModel, "NeedsModel");
            });
            yield return Case("незаполненное условие не действует", () =>
            {
                var blank = new RecordFilter(new[] { new FilterCondition(FilterField.Category, FilterOperator.Equals, "") }, false);
                Check(blank.IsEmpty && Ids(blank) == "r1,r2,r3", "пустое условие скрыло записи");
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Undo()
        {
            var a1 = R("a1", 1, ChangeKind.Modified, 100, "Перенести");
            var a2 = R("a2", 2, ChangeKind.Modified, 100, "Перенести", OperationKind.Undo);
            var b1 = R("b1", 3, ChangeKind.Created, 200, "Стена");
            var b2 = R("b2", 4, ChangeKind.Deleted, 200, "Стена", OperationKind.Undo);
            var c1 = R("c1", 5, ChangeKind.Modified, 300, "X");
            var c2 = R("c2", 6, ChangeKind.Modified, 300, "X", OperationKind.Undo);
            var c3 = R("c3", 7, ChangeKind.Modified, 300, "X", OperationKind.Redo);
            var d1 = R("d1", 8, ChangeKind.Modified, 400, "Y", OperationKind.Undo);
            var e1 = R("e1", 9, ChangeKind.Modified, 500, "Z");
            var e2 = R("e2", 10, ChangeKind.Modified, 500, "Z", OperationKind.Undo, session: "s2");
            var f1 = R("f1", 11, ChangeKind.Modified, 600, "W");
            var f2 = R("f2", 12, ChangeKind.Modified, 600, "W");
            var f3 = R("f3", 13, ChangeKind.Modified, 600, "W", OperationKind.Undo);
            var g1 = R("g1", 14, ChangeKind.Modified, 700, "V");
            var g2 = R("g2", 15, ChangeKind.Modified, 700, "U", OperationKind.Undo);
            var hidden = UndoPairs.Find(new[] { a1, a2, b1, b2, c1, c2, c3, d1, e1, e2, f1, f2, f3, g1, g2 });
            string H(params ChangeRecord[] rs) => string.Join(",", rs.Where(hidden.Contains).Select(r => r.RecordId));

            yield return Case("правка + отмена скрыты", () => Check(H(a1, a2) == "a1,a2", H(a1, a2)));
            yield return Case("создание + отмена (удаление) скрыты", () => Check(H(b1, b2) == "b1,b2", H(b1, b2)));
            yield return Case("повтор (Ctrl+Y) остаётся виден", () => Check(H(c1, c2, c3) == "c1,c2", H(c1, c2, c3)));
            yield return Case("отмена без правки в журнале видна", () => Check(H(d1) == "", H(d1)));
            yield return Case("отмена в другом сеансе — не пара", () => Check(H(e1, e2) == "", H(e1, e2)));
            yield return Case("отменяется последняя из правок одной командой", () => Check(H(f1, f2, f3) == "f2,f3", H(f1, f2, f3)));
            yield return Case("отмена другой команды — не пара", () => Check(H(g1, g2) == "", H(g1, g2)));
        }

        public static IEnumerable<KeyValuePair<string, Action>> Journal()
        {
            yield return Case("актуализация убирает все «✗» без сверки с моделью", () =>
            {
                var open = new ChangeRecord { RecordId = "o", Time = T0, Kind = ChangeKind.Event, EventType = ModelEventType.Open, Session = "s9", User = "Kirill_BIM", Machine = "PC" };
                var l1 = R("L1", 5, ChangeKind.Modified, 900, "Перенести", session: "s9");
                var l2 = R("L2", 6, ChangeKind.Modified, 900, "Перенести", session: "s9");
                var close = new ChangeRecord { RecordId = "c", Time = T0.AddMinutes(10), Kind = ChangeKind.Event, EventType = ModelEventType.Close, Session = "s9", User = "Kirill_BIM", Machine = "PC" };
                var journal = new List<ChangeRecord> { open, l1, l2, close };
                var statuses = SyncStatusCalculator.Compute(journal);
                Check(SyncStatusCalculator.IsVoid(statuses[l1]), "закрыто без синхронизации — не «✗»: " + statuses[l1]);
                var ctx = JournalAudit.BuildContext(journal, statuses, T0.AddHours(1));
                var lost = JournalAudit.VoidFindings(journal, ctx, new HashSet<string>(), true);
                Check(lost.Count == 2 && lost.All(f => f.Reason.StartsWith("не попало в хранилище")), "найдено «✗»: " + lost.Count);
                var again = JournalAudit.VoidFindings(journal, ctx, new HashSet<string> { "L1" }, true);
                Check(again.Count == 1 && again[0].Record.RecordId == "L2", "уже убранные повторились");
                Check(JournalAudit.BuildChecks(journal, ctx, new HashSet<string>()).Count == 0, "«✗» попали в сверку с моделью");
            });
            yield return Case("«возвращено в журнал» не конец работы автора", () =>
            {
                var legacy = new ChangeRecord { RecordId = "old", Time = T0, Kind = ChangeKind.Modified, ElementId = 1, User = "Kirill_BIM", Machine = "PC", Source = ChangeSource.Live };
                var restore = new ChangeRecord { RecordId = "rs", Time = T0.AddMinutes(5), Kind = ChangeKind.Event, EventType = ModelEventType.Restore, User = "Kirill_BIM", Machine = "PC", Excludes = new List<string>() };
                var withRestore = new List<ChangeRecord> { legacy, restore };
                var ctx = JournalAudit.BuildContext(withRestore, SyncStatusCalculator.Compute(withRestore), T0.AddHours(1));
                Check(!ctx.LegacyEligible.Contains(legacy), "старая запись стала проверяемой после «возвращено в журнал»");
                var commit = new ChangeRecord { RecordId = "cm", Time = T0.AddMinutes(5), Kind = ChangeKind.Event, EventType = ModelEventType.Commit, User = "Kirill_BIM", Machine = "PC" };
                var withCommit = new List<ChangeRecord> { legacy, commit };
                var ctx2 = JournalAudit.BuildContext(withCommit, SyncStatusCalculator.Compute(withCommit), T0.AddHours(1));
                Check(ctx2.LegacyEligible.Contains(legacy), "после синхронизации старая запись не проверяется");
            });
            yield return Case("«Без дублей»: вчерашняя правка автора скрывает её копию из синхронизации", () =>
            {
                var liveYesterday = R("a", -15 * 60, ChangeKind.Modified, 1, "Перенести", user: "Иванов");
                var syncToday = R("b", 0, ChangeKind.Modified, 1, "", source: ChangeSource.Sync, user: "Иванов", by: "Петров");
                var liveDel = R("c", -60, ChangeKind.Deleted, 2, "Удалить", user: "Иванов");
                var syncDel = R("d", 0, ChangeKind.Deleted, 2, "", source: ChangeSource.Sync, user: "", by: "Петров");
                var syncDelOther = R("e", 0, ChangeKind.Deleted, 3, "", source: ChangeSource.Sync, user: "", by: "Петров");
                var inPeriod = new List<ChangeRecord> { syncToday, liveDel, syncDel, syncDelOther };
                var plain = JournalDedup.WithoutDuplicates(inPeriod, null).Select(r => r.RecordId).ToList();
                var withBefore = JournalDedup.WithoutDuplicates(inPeriod, null, new[] { JournalDedup.LiveKey(liveYesterday) })
                    .Select(r => r.RecordId).ToList();
                Check(plain.Contains("b"), "без записей до периода дубль остаётся");
                Check(!withBefore.Contains("b"), "дубль из синхронизации не скрыт");
                Check(!withBefore.Contains("d"), "удаление без автора не скрыто, хотя удаливший записал его сам");
                Check(withBefore.Contains("e"), "удаление без «живой» записи скрыто");
            });
            yield return Case("итог за период: перенумерованный элемент один, отмена удаления — не создание", () =>
            {
                var created = R("f", 60, ChangeKind.Created, 10, "Стена", user: "Иванов", uid: "U1");
                var renumbered = R("g", 120, ChangeKind.Modified, 20, "Перенести", user: "Иванов", uid: "U1");
                var undoDelete = R("h", 180, ChangeKind.Created, 30, "Удалить", OperationKind.Undo, user: "Петров", uid: "U2");
                var diff = PeriodDiff.Compute(new[] { created, renumbered, undoDelete }, T0, null, null);
                Check(diff.Elements.Count(e => e.Value.UniqueId == "U1") == 1 && diff.Elements["U1"].Change == NetChange.Created &&
                      diff.Elements["U1"].Id == 20, "перенумерованный элемент");
                Check(diff.Elements["U2"].Change != NetChange.Created, "отмена удаления посчитана созданием");
            });
        }

        /// <summary>Перенос личных папок с прежних мест (LegacyFolders): ничего не теряется и не перезаписывается.</summary>
        public static IEnumerable<KeyValuePair<string, Action>> Folders()
        {
            yield return Case("новой папки нет — прежняя переносится целиком", () =>
            {
                using (var t = new TempDir())
                {
                    t.Write(@"old\ui.json", "вид");
                    t.Write(@"old\Spool\m1\Иванов@PC_2026-10.jsonl", "запись");
                    LegacyFolders.Merge(t.Path("old"), t.Path(@"RAS_v_2.0\RAS-Model History"));
                    Check(!Directory.Exists(t.Path("old")), "прежняя папка осталась");
                    Check(t.Read(@"RAS_v_2.0\RAS-Model History\ui.json") == "вид", "вид панели не перенесён");
                    Check(t.Read(@"RAS_v_2.0\RAS-Model History\Spool\m1\Иванов@PC_2026-10.jsonl") == "запись", "буфер не перенесён");
                }
            });
            yield return Case("новая папка есть — недостающее переносится, своё не перезаписывается", () =>
            {
                using (var t = new TempDir())
                {
                    t.Write(@"new\ui.json", "новый вид");
                    t.Write(@"new\Cache\m1\a.snap", "новый снимок");
                    t.Write(@"old\ui.json", "прежний вид");
                    t.Write(@"old\settings.json", "тема");
                    t.Write(@"old\Cache\m1\a.snap", "прежний снимок");
                    t.Write(@"old\Cache\m2\b.snap", "снимок другой модели");
                    LegacyFolders.Merge(t.Path("old"), t.Path("new"));
                    Check(t.Read(@"new\ui.json") == "новый вид" && t.Read(@"new\Cache\m1\a.snap") == "новый снимок", "перезаписано");
                    Check(t.Read(@"new\settings.json") == "тема" && t.Read(@"new\Cache\m2\b.snap") == "снимок другой модели",
                        "недостающее не перенесено");
                    Check(t.Read(@"old\ui.json") == "прежний вид", "прежний файл, который не перенесли, удалён");
                }
            });
            yield return Case("неотправленные записи с тем же именем не теряются — переносятся к досылке", () =>
            {
                using (var t = new TempDir())
                {
                    t.Write(@"new\Spool\m1\Иванов@PC_2026-10.jsonl", "новая");
                    t.Write(@"old\Spool\m1\Иванов@PC_2026-10.jsonl", "прежняя");
                    t.Write(@"new\Spool\m1\Иванов@PC_2026-09.jsonl.ab12cd.sending", "своя недосланная");
                    t.Write(@"old\Spool\m1\Иванов@PC_2026-09.jsonl.ab12cd.sending", "недосланная");
                    LegacyFolders.Merge(t.Path("old"), t.Path("new"));
                    var dir = t.Path(@"new\Spool\m1");
                    var oct = Directory.GetFiles(dir, "Иванов@PC_2026-10.jsonl.legacy*.sending");
                    var sep = Directory.GetFiles(dir, "Иванов@PC_2026-09.jsonl.legacy*.sending");
                    Check(t.Read(@"new\Spool\m1\Иванов@PC_2026-10.jsonl") == "новая", "новый буфер перезаписан");
                    Check(oct.Length == 1 && File.ReadAllText(oct[0]) == "прежняя", "прежние записи октября потеряны");
                    Check(sep.Length == 1 && File.ReadAllText(sep[0]) == "недосланная", "прежние недосланные записи потеряны");
                    Check(!Directory.Exists(t.Path("old")), "в прежней папке что-то осталось");
                }
            });
            yield return Case("сначала более новая прежняя папка, затем самая старая", () =>
            {
                using (var t = new TempDir())
                {
                    t.Write(@"RAS-Model History\ui.json", "вид 2");
                    t.Write(@"SpyPlugin\ui.json", "вид 1");
                    t.Write(@"SpyPlugin\state.json", "синхронизации");
                    LegacyFolders.Merge(t.Path("RAS-Model History"), t.Path("new"));
                    LegacyFolders.Merge(t.Path("SpyPlugin"), t.Path("new"));
                    Check(t.Read(@"new\ui.json") == "вид 2" && t.Read(@"new\state.json") == "синхронизации", "порядок переноса");
                }
            });
            yield return Case("прежней папки нет — ничего не происходит", () =>
            {
                using (var t = new TempDir())
                {
                    LegacyFolders.Merge(t.Path("нет"), t.Path("new"));
                    Check(!Directory.Exists(t.Path("new")), "создана пустая новая папка");
                }
            });
        }

        /// <summary>Временная папка для проверок переноса.</summary>
        private sealed class TempDir : IDisposable
        {
            private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rasv2-mh-" + Guid.NewGuid().ToString("N"));

            public string Path(string relative) => System.IO.Path.Combine(_root, relative);

            public void Write(string relative, string text)
            {
                var file = Path(relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllText(file, text);
            }

            public string Read(string relative) => File.Exists(Path(relative)) ? File.ReadAllText(Path(relative)) : null;

            public void Dispose()
            {
                try { Directory.Delete(_root, true); }
                catch (Exception) { /* временная папка — не страшно */ }
            }
        }
    }
}
