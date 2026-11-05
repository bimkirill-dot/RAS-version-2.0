using System.Windows;
using System.Windows.Controls;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Обёртка, чья ширина не раздвигает окно: наверх сообщает ширину 0, а содержимое меряет и раскладывает по той, что дали. Без неё
    /// сетка окна складывает «естественную» ширину строк таблицы (название целиком и столбцы) и раздвигается шире окна — край справа
    /// обрезается (05.11.2026, «Задачи выгрузки» на ширине 880). Выданная ширина известна только при раскладке — запоминается, и
    /// по ней меряется; поменялась — ещё один замер (перемер прямо в раскладке зацикливал её).
    /// </summary>
    public sealed class NoWidthDecorator : Decorator
    {
        private double _width = double.NaN;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child == null) return new Size();
            var width = double.IsNaN(_width) ? constraint.Width : _width;
            Child.Measure(new Size(width, constraint.Height));
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child == null) return arrangeSize;
            if (double.IsNaN(_width) || System.Math.Abs(_width - arrangeSize.Width) > 0.5)
            {
                _width = arrangeSize.Width;
                InvalidateMeasure();
            }
            Child.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}
