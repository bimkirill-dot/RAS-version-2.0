using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Хранилище заменено копией (DECISIONS.md модуля, «Хранилище заменено копией»): версии хранилища, которую модуль помнил,
    /// в истории модели больше нет. Что пропало — по синхронизациям журнала: у каждой с 22.10.2026 записана версия хранилища
    /// («sv»); синхронизации, чьих версий в хранилище нет, пропали вместе с изменениями, которые они принесли. Есть ли версия
    /// в хранилище, решает Revit (Document.IsValidVersionGUID) — здесь только правила, без Revit API; проверяются тестами.
    /// </summary>
    public static class StorageReplacement
    {
        public sealed class Verdict
        {
            /// <summary>Замена подтверждена журналом: есть синхронизации, чьих версий в хранилище нет.</summary>
            public bool Replaced;
            /// <summary>Эту замену уже записали (событие с версией, которая есть в нынешнем хранилище).</summary>
            public bool AlreadyRecorded;
            /// <summary>Последняя синхронизация, которая осталась в хранилище, — состояние копии; null — раньше всех записанных.</summary>
            public ChangeRecord KeptUntil;
            /// <summary>Пропавшие синхронизации — по времени.</summary>
            public List<ChangeRecord> Lost = new List<ChangeRecord>();
            /// <summary>Время первой пропавшей синхронизации — в событие («rt»).</summary>
            public DateTimeOffset? LostFrom => Lost.Count == 0 ? (DateTimeOffset?)null : Lost[0].Time;
            /// <summary>Время последней пропавшей синхронизации — в событие («ru»).</summary>
            public DateTimeOffset? LostUntil => Lost.Count == 0 ? (DateTimeOffset?)null : Lost[Lost.Count - 1].Time;
        }

        /// <summary>Версия хранилища из «VersionGUID:N:NumberOfSaves»; не разобрать — null.</summary>
        public static Guid? GuidOf(string storageVersion)
        {
            if (string.IsNullOrWhiteSpace(storageVersion)) return null;
            var colon = storageVersion.IndexOf(':');
            var text = colon >= 0 ? storageVersion.Substring(0, colon) : storageVersion;
            return Guid.TryParseExact(text.Trim(), "N", out var g) ? g : (Guid?)null;
        }

        /// <summary>Номер сохранения из «VersionGUID:N:NumberOfSaves»; нет — null.</summary>
        public static int? SavesOf(string storageVersion)
        {
            var colon = storageVersion?.IndexOf(':') ?? -1;
            return colon >= 0 && int.TryParse(storageVersion.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        }

        /// <summary>Версия хранилища из записи журнала: номер версии, число сохранений, когда записана, какая запись.</summary>
        private struct Point
        {
            public Guid Version;
            public int Saves;
            public ChangeRecord Record;
        }

        /// <summary>
        /// Что говорит журнал о нынешнем хранилище. isValid — есть ли версия в истории открытой модели (Revit).
        /// <para>
        /// Как Revit сообщает версию (журнал тестовой модели, 23.10.2026): при открытии и обновлении — номер и число сохранений
        /// нынешней версии хранилища; сразу после синхронизации — ещё прежний номер, но уже новое число сохранений. Поэтому запись
        /// синхронизации «G:N» — это версия «G:N−1» (до неё), а версию, которую синхронизация создала, видно только в следующей
        /// записи с N сохранений (открытие, обновление, следующая синхронизация).
        /// </para>
        /// <para>
        /// Пропавшие версии — записанные версии, которых в хранилище нет; новое хранилище появляется после всех них. Что осталось —
        /// версии, которые есть в хранилище и записаны не позже последней пропавшей; синхронизации с числом сохранений больше
        /// оставшегося — пропали. Одна лишь незнакомая версия без записей «sv» (журнал до 22.10.2026) — не повод: без ложной
        /// тревоги. requireKept — нужна хоть одна оставшаяся версия (связали журнал другого хранилища: ни одной версии из него —
        /// может, у хранилища просто нет прежней истории, и пропажу не доказать).
        /// </para>
        /// </summary>
        public static Verdict Judge(IEnumerable<ChangeRecord> records, Func<Guid, bool> isValid, bool requireKept = false)
        {
            var verdict = new Verdict();
            var points = new List<Point>();
            var commits = new List<(ChangeRecord Record, int Saves)>();
            foreach (var r in records)
            {
                if (r.Kind != ChangeKind.Event) continue;
                var g = GuidOf(r.StorageVersion);
                var n = SavesOf(r.StorageVersion);
                if (!g.HasValue || !n.HasValue) continue;
                if (r.EventType == ModelEventType.StorageReplaced)
                {
                    if (isValid(g.Value)) verdict.AlreadyRecorded = true;
                    continue;
                }
                if (r.EventType == ModelEventType.Commit)
                {
                    commits.Add((r, n.Value));
                    points.Add(new Point { Version = g.Value, Saves = n.Value - 1, Record = r }); // версия до синхронизации
                }
                else points.Add(new Point { Version = g.Value, Saves = n.Value, Record = r });
            }
            var valid = new Dictionary<Guid, bool>();
            bool Valid(Guid g)
            {
                if (!valid.TryGetValue(g, out var v)) valid[g] = v = isValid(g);
                return v;
            }
            var missing = points.Where(p => !Valid(p.Version)).ToList();
            if (missing.Count == 0) return verdict; // все записанные версии на месте — пропажи не видно
            var lastMissing = missing.Max(p => p.Record.Time);
            var kept = points.Where(p => Valid(p.Version) && p.Record.Time <= lastMissing).ToList();
            int? keptSaves = kept.Count > 0 ? kept.Max(p => p.Saves) : (int?)null;
            // состояние копии — синхронизация, создавшая оставшуюся версию (а нет её в журнале — запись, где версия видна)
            verdict.KeptUntil = keptSaves.HasValue
                ? commits.Where(c => c.Saves == keptSaves.Value).Select(c => c.Record).OrderBy(r => r.Time).FirstOrDefault()
                  ?? kept.Where(p => p.Saves == keptSaves.Value).Select(p => p.Record).OrderBy(r => r.Time).First()
                : null;
            verdict.Lost = commits
                .Where(c => c.Record.Time <= lastMissing && (!keptSaves.HasValue || c.Saves > keptSaves.Value))
                .Select(c => c.Record).OrderBy(r => r.Time).ToList();
            verdict.Replaced = verdict.Lost.Count > 0 && (!requireKept || keptSaves.HasValue);
            return verdict;
        }

        /// <summary>Текст события: на какое состояние вернули хранилище и сколько синхронизаций пропало.</summary>
        public static string EventText(Verdict v)
        {
            var text = "Хранилище заменено копией";
            text += v.KeptUntil != null
                ? " — состояние на " + v.KeptUntil.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                : " — копия старше всех записанных синхронизаций";
            var authors = v.Lost.Select(r => r.User).Where(u => !string.IsNullOrEmpty(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            text += "; пропали синхронизации: " + v.Lost.Count;
            if (authors.Count > 0) text += " (" + string.Join(", ", authors) + ")";
            return text;
        }
    }
}
