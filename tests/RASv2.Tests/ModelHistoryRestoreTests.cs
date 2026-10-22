using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ModelHistory.Tracking;

namespace RASv2.Tests
{
    /// <summary>RAS-Model History: хранилище заменено копией, связь журналов.</summary>
    internal static class ModelHistoryRestoreTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(5));

        private static string V(Guid g, int saves) => g.ToString("N") + ":" + saves;

        private static ChangeRecord Ev(int minute, ModelEventType type, string session, string user = "ivanov", string sv = null, DateTimeOffset? lostFrom = null) =>
            new ChangeRecord
            {
                RecordId = "e" + minute, Kind = ChangeKind.Event, EventType = type, Session = session, User = user, Time = T0.AddMinutes(minute),
                StorageVersion = sv, LostFrom = lostFrom, ElementId = -1,
            };

        private static ChangeRecord Edit(int minute, string session, long id) =>
            new ChangeRecord { RecordId = "r" + minute + "_" + id, Kind = ChangeKind.Modified, Source = ChangeSource.Live, Session = session, User = "ivanov", Time = T0.AddMinutes(minute), ElementId = id };

        public static IEnumerable<KeyValuePair<string, Action>> Restore()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
            // синхронизации: a (10), b (20), c (30, Петров), d (40); хранилище заменили копией на состоянии b — a и b в нём есть
            var journal = new List<ChangeRecord>
            {
                Ev(10, ModelEventType.Commit, "s1", sv: V(a, 1)),
                Ev(20, ModelEventType.Commit, "s1", sv: V(b, 2)),
                Ev(30, ModelEventType.Commit, "s2", user: "petrov", sv: V(c, 3)),
                Ev(35, ModelEventType.Reload, "s1", sv: V(c, 3)),
                Ev(40, ModelEventType.Commit, "s1", sv: V(d, 4)),
            };
            Func<Guid, bool> kept = g => g == a || g == b;

            yield return Case("замена: пропавшие синхронизации — после последней, что осталась в хранилище", () =>
            {
                var v = StorageReplacement.Judge(journal, kept);
                Check(v.Replaced && !v.AlreadyRecorded, "замена подтверждена");
                Check(v.KeptUntil?.RecordId == "e20", "состояние копии — синхронизация 20: " + v.KeptUntil?.RecordId);
                Check(string.Join(",", v.Lost.Select(r => r.RecordId)) == "e30,e40", "пропали 30 и 40 (обновление 35 — не синхронизация): " + string.Join(",", v.Lost.Select(r => r.RecordId)));
                Check(v.LostFrom == T0.AddMinutes(30), "пропало с 30");
                var text = StorageReplacement.EventText(v);
                Check(text.Contains("пропали синхронизации: 2") && text.Contains("petrov") && text.Contains("ivanov"), "текст: " + text);
            });
            yield return Case("замена: все версии на месте или версий в журнале нет — тревоги нет", () =>
            {
                Check(!StorageReplacement.Judge(journal, g => true).Replaced, "все версии есть");
                var old = journal.Select(r => new ChangeRecord { RecordId = r.RecordId, Kind = r.Kind, EventType = r.EventType, Time = r.Time }).ToList();
                Check(!StorageReplacement.Judge(old, g => false).Replaced, "журнал без версий (до 22.10.2026) — не подтвердить");
            });
            yield return Case("замена: копия старше всех записанных синхронизаций — пропало всё записанное", () =>
            {
                var v = StorageReplacement.Judge(journal, g => false);
                Check(v.Replaced && v.KeptUntil == null && v.Lost.Count == 4 && v.LostFrom == T0.AddMinutes(10), "все 4: " + v.Lost.Count);
                Check(StorageReplacement.EventText(v).Contains("копия старше"), StorageReplacement.EventText(v));
                Check(!StorageReplacement.Judge(journal, g => false, requireKept: true).Replaced,
                    "связали журнал другого хранилища, и ни одной его версии нет — пропажу не доказать, события нет");
                Check(StorageReplacement.Judge(journal, kept, requireKept: true).Replaced, "версия из связанного журнала нашлась — замена подтверждена");
            });
            yield return Case("замена: уже записана другим — второй раз не пишется", () =>
            {
                var withEvent = journal.Concat(new[] { Ev(50, ModelEventType.StorageReplaced, "s3", sv: V(Guid.NewGuid(), 3), lostFrom: T0.AddMinutes(30)) }).ToList();
                var e = withEvent.Last();
                var recorded = StorageReplacement.GuidOf(e.StorageVersion).Value;
                Check(StorageReplacement.Judge(withEvent, g => kept(g) || g == recorded).AlreadyRecorded, "событие с версией нынешнего хранилища — записано");
                Check(!StorageReplacement.Judge(withEvent, kept).AlreadyRecorded, "версии события в хранилище нет (заменили ещё раз) — новая замена");
            });
            yield return Case("версия хранилища: разбор «guid:N:сохранений»", () =>
            {
                Check(StorageReplacement.GuidOf(V(a, 7)) == a && StorageReplacement.SavesOf(V(a, 7)) == 7, "разобрано");
                Check(StorageReplacement.GuidOf(null) == null && StorageReplacement.GuidOf("мусор") == null, "не разобрать — null");
            });
            yield return Case("статус: синхронизировано в пропавшую синхронизацию — «потеряно при восстановлении»", () =>
            {
                var records = new List<ChangeRecord>
                {
                    Edit(5, "s1", 101), Ev(10, ModelEventType.Commit, "s1"),   // осталось в копии
                    Edit(25, "s1", 102), Ev(30, ModelEventType.Commit, "s1"),  // пропало
                    Ev(45, ModelEventType.StorageReplaced, "s9", lostFrom: T0.AddMinutes(30)),
                    Edit(50, "s1", 103), Ev(55, ModelEventType.Commit, "s1"),  // после замены — в новом хранилище
                    Edit(60, "s1", 104),                                        // ещё не синхронизировано
                };
                var st = SyncStatusCalculator.Compute(records);
                SyncStatus Of(long id) => st[records.First(r => r.ElementId == id)];
                Check(Of(101) == SyncStatus.InCentral, "до копии — в хранилище");
                Check(Of(102) == SyncStatus.LostOnRestore, "пропавшая синхронизация — потеряно: " + Of(102));
                Check(Of(103) == SyncStatus.InCentral, "после замены — в хранилище: " + Of(103));
                Check(Of(104) == SyncStatus.NotSynced, "не синхронизировано");
                Check(SyncStatusCalculator.IsVoid(SyncStatus.LostOnRestore) && SyncStatusCalculator.Text(SyncStatus.LostOnRestore, true).StartsWith("✗"), "✗ и «изменения нет»");
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Links()
        {
            yield return Case("связь журналов: место модели — без имени сервера и регистра", () =>
            {
                Check(JournalLinks.Place("RSN://10.10.1.88/2025_Arena/S10/Дом.rvt") == JournalLinks.Place(@"rsn://server2\2025_arena\s10\ДОМ.RVT"), "один путь — разные серверы");
                Check(JournalLinks.Place("RSN://10.10.1.88/A/Дом.rvt") != JournalLinks.Place("RSN://10.10.1.88/B/Дом.rvt"), "другая папка — другая модель");
                Check(JournalLinks.Place("") == "" && JournalLinks.NameOf("Дом_S1_210e4b26") == "Дом_S1", "пусто; имя в ключе");
            });
            yield return Case("связь журналов: кандидаты — то же место, другой ключ, не читается и не отклонён", () =>
            {
                var journals = new Dictionary<string, string>
                {
                    ["Дом_new"] = "RSN://88/П/Дом.rvt",      // сама модель
                    ["Дом_old1"] = "RSN://77/П/Дом.rvt",     // прежняя история (другой сервер)
                    ["Дом_old2"] = "RSN://88/П/Дом.rvt",     // отклонена
                    ["Дом_linked"] = "RSN://88/П/Дом.rvt",   // уже связана
                    ["Дом_other"] = "RSN://88/Другое/Дом.rvt",
                };
                var links = new JournalLinksFile();
                JournalLinks.Unlink(links, new JournalLinkEntry { Key = "Дом_old2" }, reject: true);
                var c = JournalLinks.Candidates("RSN://88/П/Дом.rvt", journals, new[] { "Дом_new", "Дом_linked" }, links);
                Check(string.Join(",", c) == "Дом_old1", "кандидат: " + string.Join(",", c));
            });
            yield return Case("связь журналов: читать — свой, прежние ключи, связанные и их связи, без кругов", () =>
            {
                var files = new Dictionary<string, JournalLinksFile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["c"] = new JournalLinksFile { Linked = { new JournalLinkEntry { Key = "b" } } },
                    ["b"] = new JournalLinksFile { Linked = { new JournalLinkEntry { Key = "a" }, new JournalLinkEntry { Key = "c" } } },
                };
                var order = JournalLinks.ReadOrder("c", new[] { "c_legacy" }, k => files.TryGetValue(k, out var f) ? f : null);
                Check(string.Join(",", order) == "c,c_legacy,b,a", "порядок: " + string.Join(",", order));
            });
            yield return Case("связь журналов: links.json — запись и чтение, испорченный — пустой", () =>
            {
                var file = new JournalLinksFile();
                JournalLinks.Link(file, new JournalLinkEntry { Key = "Дом_old", Title = "Дом", By = "Полетаев", At = T0 });
                JournalLinks.Link(file, new JournalLinkEntry { Key = "дом_OLD" });
                var back = JournalLinks.Parse(JournalLinks.Serialize(file));
                Check(back.Linked.Count == 1 && back.Linked[0].Title == "Дом" && back.Linked[0].At == T0, "одна связь, без повтора");
                JournalLinks.Unlink(back, new JournalLinkEntry { Key = "Дом_old" }, reject: false);
                Check(back.Linked.Count == 0 && back.Rejected.Count == 0, "отвязано, не отклонено");
                Check(JournalLinks.Parse("{ не json").Linked.Count == 0 && JournalLinks.Parse(null).Rejected.Count == 0, "испорченный и пустой — без связей");
            });
            yield return Case("связь журналов: кто писал и за какие месяцы — по именам файлов", () =>
            {
                var s = JournalLinks.Summarize(new[] { "kekilbay_bim@WIN-1_2026-09.jsonl", "ardaktym@PC_2026-10.jsonl", "kekilbay_bim@WIN-1_2026-10.jsonl", "model.json" });
                Check(string.Join(",", s.Users) == "kekilbay_bim,ardaktym" && JournalLinks.Period(s) == "09.2026 – 10.2026", string.Join(",", s.Users) + " " + JournalLinks.Period(s));
            });
        }
    }
}
