using System;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Место коллизии по габаритам двух элементов, когда точного пересечения тел нет (проверка «ближе допуска» — элементы не
    /// касаются) или Revit его не посчитал: по каждой оси — общая часть отрезков, а если её нет — промежуток между ними.
    /// Так рамка — у места коллизии, а не вокруг всего большого элемента (длинного воздуховода, перекрытия, стены из связи;
    /// разработчик, 19.10.2026: «Revit не полноценно обрезает вид — один из элементов связь, и она большая»). Без Revit API.
    /// </summary>
    public static class ClashZone
    {
        /// <summary>Отрезок места коллизии по одной оси: [minA, maxA] и [minB, maxB] пересекаются — их общая часть, иначе — промежуток.</summary>
        public static (double Min, double Max) Axis(double minA, double maxA, double minB, double maxB)
        {
            var lo = Math.Max(minA, minB);
            var hi = Math.Min(maxA, maxB);
            return lo <= hi ? (lo, hi) : (hi, lo);
        }
    }
}
