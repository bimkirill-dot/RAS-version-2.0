namespace RASv2.BlockMirrorRotation.Logic
{
    /// <summary>
    /// Кружки оси (уровня) на виде — с тех же сторон листа, что до отражения (DECISIONS.md, 11.11.2026: «важно, чтобы кружки
    /// были слева и снизу»). Концы сравниваются по координате вдоль оси вида, по которой идёт линия (вправо или вверх).
    /// Без Revit API — с тестами.
    /// </summary>
    public static class BubbleSides
    {
        /// <summary>
        /// Кружки после отражения. before0 / before1 — координаты концов 0 и 1 вдоль оси вида до отражения, after0 / after1 —
        /// после. Кружок был на одном конце — после он на том конце, который лежит с той же стороны («ниже» или «выше»).
        /// На обоих или ни на одном — так же.
        /// </summary>
        public static void Choose(bool show0, bool show1, double before0, double before1, double after0, double after1,
                                  out bool new0, out bool new1)
        {
            new0 = show0;
            new1 = show1;
            if (show0 == show1) return;
            // кружок на «меньшем» конце (левее / ниже)?
            var onLower = show0 ? before0 < before1 : before1 < before0;
            var lowerIs0 = after0 < after1;
            new0 = onLower == lowerIs0;
            new1 = !new0;
        }
    }
}
