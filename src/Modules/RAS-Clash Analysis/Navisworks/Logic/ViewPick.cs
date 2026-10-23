using System;
using System.Collections.Generic;

namespace RASv2.ClashAnalysis.Navisworks.Logic
{
    /// <summary>Габарит: мин. и макс. по осям (в единицах модели Navisworks).</summary>
    public struct Box3
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public Box3(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public double Diagonal => Math.Sqrt(Sq(MaxX - MinX) + Sq(MaxY - MinY) + Sq(MaxZ - MinZ));

        private static double Sq(double v) => v * v;
    }

    /// <summary>Направление взгляда-кандидат: куда смотрит камера (единичный вектор), и насколько такой ракурс привычен глазу.</summary>
    public struct ViewCandidate
    {
        public double X, Y, Z;
        /// <summary>Наклон вниз, градусы (0 — горизонтально, 90 — сверху).</summary>
        public double Elevation;
        /// <summary>0…1: при равной видимости побеждает привычный ракурс (наискось сверху).</summary>
        public double Preference;
    }

    /// <summary>
    /// Подбор вида коллизии (DECISIONS.md модуля, «Navisworks: подстроить виды коллизий»): два элемента — пурпурный и жёлтый (оси
    /// модели Navisworks рисует красным и зелёным, фон — голубой: первые цвета, красный и зелёный, путались с осями), всё
    /// остальное скрыто; для каждого направления-кандидата Navisworks рисует маленькую картинку, и по ней считается, сколько видно
    /// каждого элемента. Побеждает вид, где хуже видимый элемент виден лучше всего: один закрыл другой — второго цвета почти нет.
    /// Оценка по тому, что реально видно, а не по габаритам (прежняя попытка по габаритам не справлялась с трубой в стене: габариты
    /// перекрываются при любом взгляде). Без Navisworks — проверяется тестами.
    /// </summary>
    public static class ViewPick
    {
        /// <summary>
        /// Направления: наискось сверху (8 сторон), круче сверху, почти горизонтально, сверху и чуть снизу (плита над стеной или
        /// колонной: сверху плита закрывает её при любом взгляде).
        /// </summary>
        public static List<ViewCandidate> Candidates()
        {
            var list = new List<ViewCandidate>();
            void Add(double elevation, double azimuth, double preference)
            {
                var el = elevation * Math.PI / 180;
                var az = azimuth * Math.PI / 180;
                // камера смотрит из точки (cos el·cos az, cos el·sin az, sin el) на место коллизии
                list.Add(new ViewCandidate
                {
                    X = -Math.Cos(el) * Math.Cos(az), Y = -Math.Cos(el) * Math.Sin(az), Z = -Math.Sin(el),
                    Elevation = elevation, Preference = preference,
                });
            }
            for (var az = 0; az < 360; az += 45) Add(35, az + 22.5, 1.0);
            for (var az = 0; az < 360; az += 90) Add(60, az + 45, 0.8);
            for (var az = 0; az < 360; az += 90) Add(10, az, 0.7);
            Add(85, 0, 0.6);
            for (var az = 0; az < 360; az += 90) Add(-20, az + 45, 0.5);
            return list;
        }

        /// <summary>
        /// Пиксель первого элемента (пурпурный): красный и синий заметно сильнее зелёного — с учётом освещения и тени. Красные оси
        /// (синего нет) и голубой фон (красного мало) — не он.
        /// </summary>
        public static bool IsFirst(byte r, byte g, byte b) => r > 60 && b > 60 && g * 1.6 < Math.Min(r, b) && Math.Abs(r - b) < Math.Max(r, b) * 0.45;

        /// <summary>Пиксель второго элемента (жёлтый): красный и зелёный заметно сильнее синего. Зелёные оси (красного нет) — не он.</summary>
        public static bool IsSecond(byte r, byte g, byte b) => r > 60 && g > 60 && b * 1.6 < Math.Min(r, g) && Math.Abs(r - g) < Math.Max(r, g) * 0.45;

        /// <summary>
        /// Оценка вида: главное — сколько видно хуже видимого элемента (доля картинки); немного — сколько видно обоих вместе;
        /// чуть-чуть — привычность ракурса. Одного из элементов не видно вовсе — вид не годится.
        /// </summary>
        public static double Score(int first, int second, int total, double preference)
        {
            if (total <= 0 || first <= 0 || second <= 0) return -1 + preference * 0.001;
            var worse = Math.Min(first, second) / (double)total;
            var both = (first + second) / (double)total;
            return worse + 0.2 * both + 0.02 * preference;
        }

        /// <summary>
        /// Что показать: место коллизии (общая часть габаритов, нет её — промежуток между ними) с запасом — чтобы были видны оба
        /// элемента вокруг места, а не большая стена целиком: запас — размер места, но не меньше трети меньшего элемента.
        /// </summary>
        public static Box3 Zone(Box3 a, Box3 b)
        {
            var zone = new Box3(
                Math.Min(Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX)), Math.Min(Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY)),
                Math.Min(Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ)),
                Math.Max(Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX)), Math.Max(Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY)),
                Math.Max(Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ)));
            var margin = Math.Max(zone.Diagonal, Math.Min(a.Diagonal, b.Diagonal) / 3);
            if (margin <= 0) margin = Math.Max(Math.Max(a.Diagonal, b.Diagonal), 1e-3);
            return new Box3(zone.MinX - margin, zone.MinY - margin, zone.MinZ - margin, zone.MaxX + margin, zone.MaxY + margin, zone.MaxZ + margin);
        }
    }
}
