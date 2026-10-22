using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Дошло ли изменение до файла-хранилища (или, для одиночного файла, сохранено ли оно).</summary>
    public enum SyncStatus
    {
        /// <summary>Нет сведений (записи старых версий плагина, события модели).</summary>
        Unknown,
        /// <summary>В хранилище: после изменения была синхронизация (или сохранение одиночного файла).</summary>
        InCentral,
        /// <summary>Ещё не синхронизировано: сеанс идёт (или Revit закрылся аварийно и копию ещё не открывали).</summary>
        NotSynced,
        /// <summary>Модель закрыта без синхронизации, но изменение сохранено в локальной копии — может дойти позже.</summary>
        SavedLocally,
        /// <summary>Модель закрыта без сохранения и синхронизации — изменения не случилось.</summary>
        Discarded,
        /// <summary>Изменение было в локальной копии, но её заменили новой — в хранилище оно не попало.</summary>
        LostLocal,
        /// <summary>Изменение было в хранилище, но хранилище заменили копией, сделанной раньше, — изменение пропало.</summary>
        LostOnRestore
    }

    /// <summary>
    /// Определяет статус записей по событиям модели того же сеанса: синхронизация, сохранение локальной копии,
    /// закрытие, продолжение работы в той же копии или её замена. Чистая логика — без Revit API.
    /// </summary>
    public static class SyncStatusCalculator
    {
        private sealed class SessionInfo
        {
            public readonly List<DateTimeOffset> Commits = new List<DateTimeOffset>();
            public readonly List<DateTimeOffset> Saves = new List<DateTimeOffset>();
            public DateTimeOffset? Closed;
            /// <summary>Несинхронизированное из этого сеанса подхватил другой сеанс (та же локальная копия).</summary>
            public string AdoptedBy;
            public DateTimeOffset AdoptedAt;
            /// <summary>Несинхронизированное из этого сеанса пропало (копия заменена).</summary>
            public DateTimeOffset? LostAt;
            /// <summary>Несинхронизированное из этого сеанса попало в хранилище синхронизацией другого сеанса.</summary>
            public DateTimeOffset? CommittedElsewhere;
        }

        /// <summary>Замена хранилища копией: что попало в хранилище с LostFrom до At — пропало.</summary>
        private struct Replacement
        {
            public DateTimeOffset LostFrom, At;
        }

        public static Dictionary<ChangeRecord, SyncStatus> Compute(IEnumerable<ChangeRecord> records)
        {
            var list = records as IList<ChangeRecord> ?? records.ToList();
            var sessions = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
            var replacements = list.Where(r => r.Kind == ChangeKind.Event && r.EventType == ModelEventType.StorageReplaced && r.LostFrom.HasValue)
                .Select(r => new Replacement { LostFrom = r.LostFrom.Value, At = r.Time }).ToList();
            SessionInfo Get(string id)
            {
                if (!sessions.TryGetValue(id, out var si))
                {
                    si = new SessionInfo();
                    sessions[id] = si;
                }
                return si;
            }

            foreach (var e in list.Where(r => r.Kind == ChangeKind.Event && r.Session != null).OrderBy(r => r.Time))
            {
                var si = Get(e.Session);
                switch (e.EventType)
                {
                    case ModelEventType.Commit:
                        si.Commits.Add(e.Time);
                        if (e.Sessions != null)
                        {
                            foreach (var other in e.Sessions)
                            {
                                var o = Get(other);
                                if (o.CommittedElsewhere == null) o.CommittedElsewhere = e.Time;
                            }
                        }
                        break;
                    case ModelEventType.SaveLocal:
                        si.Saves.Add(e.Time);
                        break;
                    case ModelEventType.Close:
                        if (si.Closed == null) si.Closed = e.Time;
                        break;
                    case ModelEventType.Resume:
                        if (e.Sessions != null)
                        {
                            foreach (var other in e.Sessions)
                            {
                                var o = Get(other);
                                o.AdoptedBy = e.Session; // последнее подхватившее важнее (события идут по времени)
                                o.AdoptedAt = e.Time;
                            }
                        }
                        break;
                    case ModelEventType.Lost:
                        if (e.Sessions != null)
                        {
                            foreach (var other in e.Sessions) Get(other).LostAt = e.Time;
                        }
                        break;
                }
            }

            var result = new Dictionary<ChangeRecord, SyncStatus>(list.Count);
            foreach (var r in list)
            {
                if (r.Kind == ChangeKind.Event) continue;
                if (r.Source != ChangeSource.Live)
                {
                    result[r] = SyncStatus.InCentral; // получено из хранилища
                    continue;
                }
                if (r.Session == null)
                {
                    result[r] = SyncStatus.Unknown;
                    continue;
                }
                var status = StatusOf(r, sessions, out var storedAt);
                // в хранилище попало, но хранилище потом заменили копией, сделанной до этого, — пропало
                if (status == SyncStatus.InCentral && storedAt.HasValue &&
                    replacements.Any(x => storedAt.Value >= x.LostFrom && storedAt.Value <= x.At))
                    status = SyncStatus.LostOnRestore;
                result[r] = status;
            }
            return result;
        }

        /// <param name="storedAt">Для «в хранилище» — когда попало в хранилище (синхронизация); иначе — null.</param>
        private static SyncStatus StatusOf(ChangeRecord r, Dictionary<string, SessionInfo> sessions, out DateTimeOffset? storedAt)
        {
            storedAt = null;
            if (!sessions.TryGetValue(r.Session, out var si)) return SyncStatus.NotSynced;
            var own = si.Commits.Where(c => c >= r.Time).ToList();
            if (own.Count > 0)
            {
                storedAt = own.Min();
                return SyncStatus.InCentral;
            }

            // было ли изменение сохранено в локальную копию до её закрытия
            var saved = si.Saves.Any(sv => sv >= r.Time);
            var cur = si;
            var from = r.Time;
            for (int hop = 0; hop < 20; hop++)
            {
                if (cur.CommittedElsewhere.HasValue && cur.CommittedElsewhere.Value >= from)
                {
                    if (saved) storedAt = cur.CommittedElsewhere;
                    return saved ? SyncStatus.InCentral : SyncStatus.Discarded;
                }
                if (cur.LostAt.HasValue && cur.LostAt.Value >= from)
                    return saved ? SyncStatus.LostLocal : SyncStatus.Discarded;
                if (cur.AdoptedBy != null && cur.AdoptedAt >= from && sessions.TryGetValue(cur.AdoptedBy, out var next))
                {
                    if (!saved) return SyncStatus.Discarded; // не было сохранено — в подхваченной копии его нет
                    var adoptedAt = cur.AdoptedAt;
                    var commits = next.Commits.Where(c => c >= adoptedAt).ToList();
                    if (commits.Count > 0)
                    {
                        storedAt = commits.Min();
                        return SyncStatus.InCentral;
                    }
                    from = cur.AdoptedAt;
                    cur = next;
                    // в продолжившем сеансе изменения уже лежат в файле копии
                    continue;
                }
                if (cur.Closed.HasValue) return saved ? SyncStatus.SavedLocally : SyncStatus.Discarded;
                return SyncStatus.NotSynced;
            }
            return SyncStatus.NotSynced;
        }

        public static string Text(SyncStatus status, bool workshared)
        {
            switch (status)
            {
                case SyncStatus.InCentral: return workshared ? "✓ в хранилище" : "✓ сохранено";
                case SyncStatus.NotSynced: return workshared ? "… не синхронизировано" : "… не сохранено";
                case SyncStatus.SavedLocally: return "… только в локальной копии";
                case SyncStatus.Discarded: return workshared ? "✗ не попало в хранилище" : "✗ закрыто без сохранения";
                case SyncStatus.LostLocal: return "✗ не попало в хранилище";
                case SyncStatus.LostOnRestore: return "✗ потеряно при восстановлении";
                default: return "";
            }
        }

        public static string Explain(SyncStatus status, bool workshared)
        {
            switch (status)
            {
                case SyncStatus.InCentral:
                    return workshared ? "Изменение синхронизировано с файлом-хранилищем." : "Изменение сохранено в файл.";
                case SyncStatus.NotSynced:
                    return workshared
                        ? "Изменение ещё не синхронизировано: оно есть только у автора (сеанс идёт или Revit закрылся аварийно)."
                        : "Изменение ещё не сохранено.";
                case SyncStatus.SavedLocally:
                    return "Модель закрыта без синхронизации. Изменение сохранено в локальной копии автора и попадёт в хранилище, если он синхронизируется из этой же копии.";
                case SyncStatus.Discarded:
                    return workshared
                        ? "Модель закрыта без синхронизации и без сохранения — изменение не попало ни в хранилище, ни в локальную копию. Фактически его не было."
                        : "Модель закрыта без сохранения — изменения фактически не было.";
                case SyncStatus.LostLocal:
                    return "Изменение оставалось только в локальной копии, но автор открыл модель через новую локальную копию — в хранилище оно не попало.";
                case SyncStatus.LostOnRestore:
                    return "Изменение было синхронизировано, но потом хранилище заменили копией, сделанной раньше, — в модели его больше нет (событие «Хранилище заменено копией»).";
                default:
                    return "";
            }
        }

        /// <summary>Изменения фактически не было (не попало в модель).</summary>
        public static bool IsVoid(SyncStatus s) => s == SyncStatus.Discarded || s == SyncStatus.LostLocal || s == SyncStatus.LostOnRestore;
    }
}
