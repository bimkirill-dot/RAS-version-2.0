using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// «Без дублей»: одно и то же изменение записывает и сам автор («В сеансе»), и плагин коллеги при синхронизации
    /// («Синхронизация», время — момент получения). Запись синхронизации скрывается, если автор записал изменение сам.
    /// Чистая логика — общая для панели и 3D-вида.
    /// </summary>
    public static class JournalDedup
    {
        /// <summary>Ключ «элемент | автор» для живых записей.</summary>
        public static string LiveKey(ChangeRecord r) => r.ElementId + "|" + (r.User ?? "");

        /// <summary>Запись, по которой можно узнать дубль: живая, об элементе (не событие и не сводка).</summary>
        public static bool IsLiveElementRecord(ChangeRecord r) =>
            r.Source == ChangeSource.Live && r.ElementId > 0 && r.Kind != ChangeKind.Event;

        /// <param name="excluded">Убранные при актуализации: такая живая запись не скрывает то же изменение из синхронизации.</param>
        /// <param name="liveOutside">Живые записи вне загруженного периода (LogReader.LiveKeys): вчерашняя правка автора
        /// скрывает её копию, полученную коллегой сегодня утром.</param>
        /// <param name="liveDeletedOutside">Элементы, удаление которых записано «вживую» вне загруженного периода.</param>
        public static List<ChangeRecord> WithoutDuplicates(IReadOnlyCollection<ChangeRecord> records, ISet<string> excluded,
            IEnumerable<string> liveOutside = null, IEnumerable<long> liveDeletedOutside = null)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveDeleted = new HashSet<long>();
            if (liveOutside != null) live.UnionWith(liveOutside);
            if (liveDeletedOutside != null) liveDeleted.UnionWith(liveDeletedOutside);
            foreach (var r in records)
            {
                if (!IsLiveElementRecord(r)) continue;
                if (excluded != null && r.RecordId != null && excluded.Contains(r.RecordId)) continue;
                live.Add(LiveKey(r));
                if (r.Kind == ChangeKind.Deleted) liveDeleted.Add(r.ElementId);
            }

            var seenRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ChangeRecord>(records.Count);
            foreach (var r in records.OrderBy(x => x.Time))
            {
                if (r.Source == ChangeSource.Live || r.ElementId <= 0)
                {
                    result.Add(r);
                    continue;
                }
                if (!string.IsNullOrEmpty(r.User) && live.Contains(LiveKey(r))) continue;
                // удаление из синхронизации пишется без автора (Revit его не знает); если удаливший с плагином
                // записал его сам — это дубль, иначе в списке была бы вторая строка «Удалён · (неизвестно)»
                if (r.Kind == ChangeKind.Deleted && string.IsNullOrEmpty(r.User) && liveDeleted.Contains(r.ElementId)) continue;
                var key = r.ElementId + "|" + r.Kind + "|" + (r.ElementVersion ?? r.User ?? "");
                if (!seenRemote.Add(key)) continue;
                result.Add(r);
            }
            return result;
        }
    }
}
