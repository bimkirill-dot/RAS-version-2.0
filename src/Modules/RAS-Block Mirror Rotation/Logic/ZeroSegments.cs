using System.Collections.Generic;

namespace RASv2.BlockMirrorRotation.Logic
{
    /// <summary>
    /// Нулевые сегменты размерной цепочки (DECISIONS.md, 11.11.2026: «в размерных цепочках появляются нули»): какие ссылки
    /// оставить, чтобы нулей не было. Ссылки идут по порядку вдоль размера, сегмент i — между ссылками i и i + 1. Без Revit
    /// API — с тестами.
    /// </summary>
    public static class ZeroSegments
    {
        /// <summary>Есть ли нулевой сегмент (короче tolerance).</summary>
        public static bool Any(IList<double> segments, double tolerance)
        {
            foreach (var s in segments)
                if (s < tolerance) return true;
            return false;
        }

        /// <summary>Номера ссылок, которые остаются: первая — всегда, следующая — только если сегмент до неё не нулевой.</summary>
        public static List<int> Keep(IList<double> segments, double tolerance)
        {
            var keep = new List<int> { 0 };
            for (var i = 0; i < segments.Count; i++)
                if (segments[i] >= tolerance) keep.Add(i + 1);
            return keep;
        }
    }
}
