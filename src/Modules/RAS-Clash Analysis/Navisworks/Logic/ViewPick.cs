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
    /// Подбор вида коллизии (DECISIONS.md модуля, «Navisworks: подстроить виды коллизий»): всё, кроме двух элементов, скрыто; для
    /// каждого направления-кандидата Navisworks рисует четыре маленькие картинки (оба, только первый, только второй, ни одного), и по
    /// ним считается, сколько видно каждого элемента (Visible). Побеждает вид, где хуже видимый элемент виден лучше всего: один закрыл другой — второго цвета почти нет.
    /// Оценка по тому, что реально видно, а не по габаритам (прежняя попытка по габаритам не справлялась с трубой в стене: габариты
    /// перекрываются при любом взгляде). Без Navisworks — проверяется тестами.
    /// </summary>
    public static class ViewPick
    {
        /// <summary>
        /// Вид годится без перебора (ускорение, 28.10.2026): каждый элемент открыт (не закрыт другим) не меньше чем на GoodOpen
        /// и занимает в кадре не меньше GoodSize (у картинки 160×120 — около 100 точек).
        /// </summary>
        public const double GoodOpen = 0.9, GoodSize = 0.005;

        /// <summary>Сколько направлений смотреть сначала — привычные (наискось сверху с 8 сторон): лучший из них годится — остальные не нужны.</summary>
        public const int FirstRound = 8;

        public static bool Good(int first, int firstAll, int second, int secondAll, int total) =>
            Open(first, firstAll, second, secondAll, total) >= GoodOpen;

        /// <summary>
        /// Открытость вида: какая доля хуже открытого элемента не закрыта другим (0…1); элемента нет в кадре или он крошечный
        /// (меньше GoodSize кадра) — 0.
        /// </summary>
        public static double Open(int first, int firstAll, int second, int secondAll, int total) =>
            total <= 0 || firstAll <= 0 || secondAll <= 0 || Math.Min(first, second) < GoodSize * total
                ? 0
                : Math.Min(first / (double)firstAll, second / (double)secondAll);

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

        /// <summary>Пиксели совпадают (с допуском на сглаживание краёв): цвет — 0xRRGGBB.</summary>
        public static bool Same(int a, int b, int tolerance = 12) =>
            Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tolerance &&
            Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tolerance &&
            Math.Abs((a & 255) - (b & 255)) <= tolerance;

        /// <summary>
        /// Сколько видно каждого элемента — по четырём картинкам одного направления: оба элемента, только первый, только второй,
        /// ни одного (фон, оси, подписи). Элемент — там, где его картинка отличается от пустой (firstAll, secondAll — сколько это
        /// точек); виден — где картинка с обоими
        /// совпадает с его собственной (второй его там не заслонил). Цвета не важны: подсветка Clash Detective (первый — красный,
        /// второй — зелёный) и оси модели перекраску перебивали (проверка разработчика 23.10.2026).
        /// </summary>
        public static void Visible(int[] both, int[] onlyFirst, int[] onlySecond, int[] none, out int first, out int second,
            out int firstAll, out int secondAll)
        {
            first = second = firstAll = secondAll = 0;
            for (var i = 0; i < both.Length; i++)
            {
                var isFirst = !Same(onlyFirst[i], none[i]);
                var isSecond = !Same(onlySecond[i], none[i]);
                if (isFirst) firstAll++;
                if (isSecond) secondAll++;
                if (isFirst && Same(both[i], onlyFirst[i]) && !(isSecond && Same(both[i], onlySecond[i]) && !Same(onlyFirst[i], onlySecond[i]))) first++;
                if (isSecond && Same(both[i], onlySecond[i]) && !(isFirst && Same(both[i], onlyFirst[i]) && !Same(onlyFirst[i], onlySecond[i]))) second++;
            }
        }

        /// <summary>
        /// Оценка вида. Главное — какая доля каждого элемента не закрыта другим (открытость): побеждает вид, где хуже открытый
        /// элемент открыт больше всего. Крошечный в кадре элемент засчитывается не целиком — множитель размера (меньше ~100 точек
        /// из 19 200 — уже заметно хуже); чуть-чуть — привычность ракурса. Прежняя оценка «сколько видно обоих вместе» выбирала вид
        /// сверху, где плита во весь кадр, а второго элемента 3 точки (проверка разработчика 23.10.2026).
        /// </summary>
        public static double Score(int first, int firstAll, int second, int secondAll, int total, double preference)
        {
            if (total <= 0 || first <= 0 || second <= 0 || firstAll <= 0 || secondAll <= 0) return -1 + preference * 0.001;
            var open = Math.Min(first / (double)firstAll, second / (double)secondAll);
            var size = Math.Min(first, second) / (double)total;
            return open * size / (size + 0.005) + 0.02 * preference;
        }

        /// <summary>
        /// Что показать: место коллизии (общая часть габаритов, нет её — промежуток между ними) с запасом — чтобы были видны оба
        /// элемента вокруг места, а не большая стена целиком: запас — половина размера места, но не меньше четверти меньшего элемента
        /// (треть и целый размер давали мелкие элементы в кадре — проверка разработчика 23.10.2026).
        /// </summary>
        public static Box3 Zone(Box3 a, Box3 b)
        {
            var zone = new Box3(
                Math.Min(Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX)), Math.Min(Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY)),
                Math.Min(Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ)),
                Math.Max(Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX)), Math.Max(Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY)),
                Math.Max(Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ)));
            var margin = Math.Max(zone.Diagonal / 2, Math.Min(a.Diagonal, b.Diagonal) / 4);
            if (margin <= 0) margin = Math.Max(Math.Max(a.Diagonal, b.Diagonal), 1e-3);
            return new Box3(zone.MinX - margin, zone.MinY - margin, zone.MinZ - margin, zone.MaxX + margin, zone.MaxY + margin, zone.MaxZ + margin);
        }
    }
}
