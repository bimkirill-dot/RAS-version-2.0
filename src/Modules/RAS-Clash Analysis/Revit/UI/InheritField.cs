using System;
using System.Windows;
using System.Windows.Controls;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Строка «своё или как выше» окон «Задач выгрузки»: своё — поле правится, рядом «Вернуть …»; как выше — значение выше
    /// бледным, «от «…»» / «По общим настройкам» и «Изменить». Extras (метки, «Обзор») — видны, только когда своё.
    /// </summary>
    internal sealed class InheritField
    {
        private readonly TextBox _box;
        private readonly TextBlock _from;
        private readonly Button _link;
        private readonly FrameworkElement[] _extras;
        private readonly string _inherited;
        private readonly string _source;
        public bool Own { get; private set; }

        public InheritField(TextBox box, TextBlock from, Button link, FrameworkElement[] extras, string own, string inherited, string source,
            Action changed)
        {
            _box = box;
            _from = from;
            _link = link;
            _extras = extras ?? new FrameworkElement[0];
            _inherited = inherited ?? "";
            _source = source;
            Own = own != null;
            box.Text = Own ? own : _inherited;
            link.Click += (s, e) =>
            {
                Own = !Own;
                if (!Own) _box.Text = _inherited;
                Show();
                if (Own)
                {
                    _box.Focus();
                    _box.SelectAll();
                }
                changed();
            };
            Show();
        }

        private void Show()
        {
            _box.IsEnabled = Own;
            _box.Opacity = Own ? 1 : 0.5;   // как выше — бледным
            foreach (var e in _extras) e.Visibility = Own ? Visibility.Visible : Visibility.Collapsed;
            _from.Text = Own ? "" : _source;
            // «По общим настройкам» — с большой буквы, когда подписью; в «Вернуть …» — с маленькой
            _link.Content = Own ? "Вернуть " + char.ToLower(_source[0]) + _source.Substring(1) : "Изменить";
        }

        /// <summary>Своё значение; null — как выше.</summary>
        public string Value => Own ? _box.Text.Trim() : null;
    }
}
