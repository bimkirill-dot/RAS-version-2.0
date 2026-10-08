using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// «Удалён вместе с …»: Revit удаляет вместе с элементом всё, что от него зависит (марки, размеры, проёмы, вложенные
    /// семейства, арматуру, экземпляры удалённого типа). Для каждого удалённого ищется корень — самый верхний удалённый
    /// в той же операции родитель: удалили стену — у марки двери в ней корень стена, а не дверь. Без Revit API:
    /// проверяется тестами.
    /// </summary>
    public static class DeletionCascade
    {
        /// <summary>Удалённый → его корень среди удалённых. Удалённых самих по себе (без удалённого родителя) в ответе нет.</summary>
        /// <param name="parentsOf">От чего элемент зависит (основа, тип, уровень, вид-владелец…).</param>
        public static Dictionary<long, long> Roots(ICollection<long> deleted, Func<long, IEnumerable<long>> parentsOf)
        {
            var set = deleted as HashSet<long> ?? new HashSet<long>(deleted);
            var parent = new Dictionary<long, long>();
            foreach (var d in set)
            {
                var p = (parentsOf(d) ?? Enumerable.Empty<long>()).FirstOrDefault(x => x != d && set.Contains(x));
                if (p != 0) parent[d] = p;
            }
            var roots = new Dictionary<long, long>();
            foreach (var d in parent.Keys)
            {
                var cur = d;
                var seen = new HashSet<long> { d };
                // вверх по удалённым родителям; кольцо (бывает в данных Revit) — останавливаемся
                while (parent.TryGetValue(cur, out var up) && seen.Add(up)) cur = up;
                if (cur != d) roots[d] = cur;
            }
            return roots;
        }
    }
}
