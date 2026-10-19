using Autodesk.Revit.DB;

namespace RASv2.Core
{
    /// <summary>
    /// Различия Revit API 2023 и 2026 для модулей (PRINCIPLES.md, раздел 9: условная компиляция по версии — только в ядре).
    /// Номер элемента: в Revit 2023 — 32-битный (ElementId(int), IntegerValue), с 2024 — 64-битный (ElementId(long), Value).
    /// </summary>
    public static class RevitCompat
    {
        /// <summary>Номер элемента из числа (у Revit 2023 номера не бывают больше int — больший номер не найдётся).</summary>
        public static ElementId Id(long value)
        {
#if REVIT2023
            return value > int.MaxValue || value < int.MinValue ? ElementId.InvalidElementId : new ElementId((int)value);
#else
            return new ElementId(value);
#endif
        }

        /// <summary>Число номера элемента.</summary>
        public static long Value(ElementId id)
        {
#if REVIT2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        /// <summary>Интерфейс Revit тёмный (в Revit 2023 тёмной темы нет).</summary>
        public static bool IsDarkTheme => RibbonIcons.IsDark;
    }
}
