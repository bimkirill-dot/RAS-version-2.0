using System;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// «Модель запомнена: N %» — одно число на всю подготовку модели (левый нижний угол журнала). Подготовка в два шага:
    /// сведения об элементах (быстро) и параметры (долго) — первый шаг считается за 20 %, второй — за 80 %.
    /// Без Revit и WPF: проверяется тестами.
    /// </summary>
    public static class WarmupProgress
    {
        private const int FirstStepShare = 20;

        /// <param name="phase">1 — сведения об элементах, 2 — параметры.</param>
        /// <returns>Процент 0–100.</returns>
        public static int Percent(int phase, int progress, int total, bool done)
        {
            if (done) return 100;
            var part = total <= 0 ? 0.0 : Math.Max(0, Math.Min(1.0, (double)progress / total));
            var percent = phase <= 1 ? FirstStepShare * part : FirstStepShare + (100 - FirstStepShare) * part;
            return Math.Min(99, (int)percent); // 100 — только когда подготовка действительно закончена
        }

        public static string Text(int percent, bool done) => done ? "✓ Модель запомнена" : "Модель запомнена: " + percent + " %";

        public const string ToolTip =
            "Плагин в фоне запоминает элементы модели и их параметры: так он знает «было» для любого изменения и что именно " +
            "удалено. Работать в модели можно сразу; пока здесь меньше 100 %, у ещё не запомненных элементов в журнале может " +
            "не быть «было». Выделенное запоминается сразу — его можно спокойно менять и удалять.";
    }
}
