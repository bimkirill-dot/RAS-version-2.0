using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Navisworks.Logic
{
    /// <summary>
    /// Что выяснилось о видах в одной проверке Clash Detective — чтобы не перебирать все направления у каждой коллизии
    /// (разработчик, 28.10.2026: «давай пробуем»). Коллизии проверки похожи, лучшее направление у них повторяется (у «КЖ
    /// Горизонтальные / АР Перегородки» почти у всех — чуть снизу). Первые FullFirst коллизий — полный перебор; дальше сначала
    /// Top направлений, которые в проверке чаще побеждали; вид открыт не хуже обычного для проверки (медиана открытости минус
    /// Tolerance) — остальные не смотрятся, иначе — полный перебор. Порог 90 % (ViewPick.Good) здесь не годится: у трубы сквозь
    /// стену часть внутри стены не видна ни с какой стороны, лучший вид открыт на 75–85 % (журнал 28.10.2026). Пересчёт по
    /// журналу на 410 коллизиях: картинок — 30 %, открытость в среднем меньше на 1 пункт. Без Navisworks — проверяется тестами.
    /// </summary>
    public sealed class ViewMemory
    {
        public const int FullFirst = 3, Top = 3;
        public const double Tolerance = 0.03;

        private readonly Dictionary<int, int> _wins = new Dictionary<int, int>();
        private readonly List<double> _opens = new List<double>();

        /// <summary>
        /// Ещё учимся — полный перебор: коллизий с подобранным видом меньше FullFirst или обычный вид проверки почти ничего не
        /// открывает (планка не выше нуля — годился бы любой вид).
        /// </summary>
        public bool Learning => _opens.Count < FullFirst || Bar <= 0;

        /// <summary>Планка «не хуже обычного»: медиана открытости подобранных видов проверки минус Tolerance.</summary>
        public double Bar
        {
            get
            {
                if (_opens.Count == 0) return double.PositiveInfinity;
                var sorted = _opens.OrderBy(x => x).ToList();
                var n = sorted.Count;
                var median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
                return median - Tolerance;
            }
        }

        /// <summary>Направления (номера в ViewPick.Candidates), которые в проверке чаще побеждали, — не больше Top; при равенстве — по порядку.</summary>
        public List<int> Favourites() =>
            _wins.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(Top).Select(kv => kv.Key).ToList();

        /// <summary>Вид коллизии подобран: какое направление победило (-1 — не из списка) и насколько открыты элементы.</summary>
        public void Remember(int direction, double open)
        {
            if (direction >= 0) _wins[direction] = _wins.TryGetValue(direction, out var n) ? n + 1 : 1;
            _opens.Add(open);
        }
    }
}
