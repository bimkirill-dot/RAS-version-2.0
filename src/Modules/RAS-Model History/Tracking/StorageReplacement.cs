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

        /// <summary>
        /// Что говорит журнал о нынешнем хранилище. isValid — есть ли версия в истории открытой модели (Revit). Замена
        /// подтверждается, только если есть пропавшие синхронизации: одна лишь незнакомая версия (без записей «sv» —
        /// журнал до 22.10.2026) событие не пишет — так не будет ложной тревоги. requireKept — нужна хоть одна синхронизация, оставшаяся
        /// в хранилище (связали журнал другого хранилища: ни одной версии из него — может, у хранилища просто нет прежней истории,
        /// и пропажу не доказать).
        /// </summary>
        public static Verdict Judge(IEnumerable<ChangeRecord> records, Func<Guid, bool> isValid, bool requireKept = false)
        {
            var verdict = new Verdict();
            var versioned = new List<(ChangeRecord Record, Guid Version)>();
            foreach (var r in records)
            {
                if (r.Kind != ChangeKind.Event) continue;
                var g = GuidOf(r.StorageVersion);
                if (!g.HasValue) continue;
                if (r.EventType == ModelEventType.StorageReplaced)
                {
                    if (isValid(g.Value)) verdict.AlreadyRecorded = true;
                }
                else if (r.EventType == ModelEventType.Commit || r.EventType == ModelEventType.Reload)
                    versioned.Add((r, g.Value));
            }
            var valid = new Dictionary<Guid, bool>();
            bool Valid(Guid g)
            {
                if (!valid.TryGetValue(g, out var v)) valid[g] = v = isValid(g);
                return v;
            }
            var kept = versioned.Where(x => Valid(x.Version)).OrderBy(x => x.Record.Time).LastOrDefault().Record;
            verdict.KeptUntil = kept;
            verdict.Lost = versioned
                .Where(x => !Valid(x.Version) && (kept == null || x.Record.Time > kept.Time) && x.Record.EventType == ModelEventType.Commit)
                .Select(x => x.Record).OrderBy(r => r.Time).ToList();
            verdict.Replaced = verdict.Lost.Count > 0 && (!requireKept || kept != null);
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
