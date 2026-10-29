using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// «ID элемента связи»: номера выделенных элементов — в связи и в самой модели — с «Копировать». Revit «ID выбранного»
    /// элемент связи не показывает, отсюда кнопка (разработчик, 29.10.2026). Кнопки, поля, кисти — общие образцы ядра.
    /// </summary>
    public partial class ElementIdWindow : Window
    {
        /// <summary>Выделенный элемент: где он (связь или модель), что это, номер.</summary>
        public sealed class Item
        {
            public string Model { get; set; }
            public string What { get; set; }
            public string Id { get; set; }
        }

        private readonly List<Item> _items;

        public ElementIdWindow(List<Item> items)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _items = items;
            Items.ItemsSource = items;
            CopyAll.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (items.Count > 1) Title = "ID элементов — " + items.Count;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Copy_Click(object sender, RoutedEventArgs e) => Copy((Button)sender, (string)((Button)sender).Tag);

        private void CopyAll_Click(object sender, RoutedEventArgs e) =>
            Copy((Button)sender, string.Join(Environment.NewLine, _items.Select(i => i.Id)));

        /// <summary>В буфер обмена; кнопка на миг пишет «Скопировано». Буфер занят другой программой — сказать.</summary>
        private void Copy(Button button, string text)
        {
            try
            {
                Clipboard.SetText(text);
                var label = button.Content;
                button.Content = "Скопировано";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    button.Content = label;
                };
                timer.Start();
                Note.Text = "";
            }
            catch (COMException)
            {
                Note.Text = "Буфер обмена занят другой программой — нажмите ещё раз или выделите номер и Ctrl+C.";
            }
        }
    }
}
