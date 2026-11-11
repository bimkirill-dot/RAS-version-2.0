using System;
using System.Windows;
using System.Windows.Threading;

namespace RASv2.BlockMirrorRotation.UI
{
    /// <summary>
    /// Ход работы. Работа идёт в потоке Revit (Revit API), поэтому окно перерисовывается само на каждом шаге (Step) — не
    /// чаще раза в 0,2 с, чтобы не замедлять.
    /// </summary>
    public partial class ProgressWindow : Window
    {
        private readonly int _total;
        private DateTime _drawn = DateTime.MinValue;

        public ProgressWindow(string what, int total)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _total = Math.Max(1, total);
            What.Text = what;
            Bar.Maximum = _total;
        }

        public void Step(int index, string name)
        {
            if (DateTime.Now - _drawn < TimeSpan.FromMilliseconds(200)) return;
            _drawn = DateTime.Now;
            Bar.Value = index;
            StepText.Text ="Вид " + (index + 1) + " из " + _total + ": " + name;
            // перерисовать окно сейчас, не дожидаясь конца работы
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }
    }
}
