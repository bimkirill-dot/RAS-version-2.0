using System.Collections.Generic;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Фильтр «Действие» на вкладке «Изменения»: какие записи показывать и подпись на кнопке.
    /// События модели живут на своей вкладке. Без WPF, чтобы логику можно было проверить отдельно.
    /// </summary>
    public static class KindFilter
    {
        /// <summary>Подпись «Действие: …» по включённым флажкам.</summary>
        public static string Summary(bool created, bool modified, bool deleted)
        {
            if (created && modified && deleted) return "все";
            var parts = new List<string>();
            if (created) parts.Add("создание");
            if (modified) parts.Add("изменение");
            if (deleted) parts.Add("удаление");
            return parts.Count == 0 ? "ничего" : string.Join(", ", parts);
        }

        /// <summary>Множество разрешённых действий (события модели сюда не входят никогда).</summary>
        public static HashSet<ChangeKind> Allowed(bool created, bool modified, bool deleted)
        {
            var set = new HashSet<ChangeKind>();
            if (created) set.Add(ChangeKind.Created);
            if (modified) set.Add(ChangeKind.Modified);
            if (deleted) set.Add(ChangeKind.Deleted);
            return set;
        }
    }
}
