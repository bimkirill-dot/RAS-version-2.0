using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Журнал в отдельном окне. У стыкуемой панели Revit нет кнопки «Развернуть»: содержимое панели переносится
    /// в обычное окно (развернуть на весь экран, перенести на другой монитор), а при закрытии окна возвращается в панель.
    /// Переносится корень разметки (PaneRoot) — те же элементы и та же модель представления, ничего не пересоздаётся.
    /// Положение и размер окна запоминаются в ui.json.
    /// </summary>
    public partial class SpyPane
    {
        private const string DetachIcon = ""; // «Во весь экран» (Segoe MDL2 Assets)
        private const string AttachIcon = ""; // «Вернуть в окно»
        private const string DetachTip = "Открыть журнал в отдельном окне: его можно развернуть на весь экран или перенести на другой монитор";
        private const string AttachTip = "Вернуть журнал в панель Revit";

        private Window _window;
        private FrameworkElement _placeholder;

        /// <summary>Журнал сейчас в отдельном окне.</summary>
        public bool IsDetached => _window != null;

        private void WindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsDetached) _window.Close(); // Closed → Attach
            else Detach();
        }

        /// <summary>Показать открытое окно поверх остальных (кнопки ленты, пока журнал в окне).</summary>
        public void ActivateWindow()
        {
            if (_window == null) return;
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private void Detach()
        {
            Content = _placeholder ??= BuildPlaceholder(); // PaneRoot освобождается от панели
            var window = new Window
            {
                Title = "RAS-Model History — журнал изменений",
                Content = PaneRoot,
                DataContext = DataContext,
                FontSize = FontSize,
                MinWidth = MinWidth,
                MinHeight = MinHeight,
                ShowInTaskbar = false
            };
            // тема и стили панели — те же словари: смена темы перекрашивает и окно
            window.Resources.MergedDictionaries.Add(Resources);
            window.SetResourceReference(BackgroundProperty, "Spy.Bg");
            window.SetResourceReference(ForegroundProperty, "Spy.Text");
            var owner = App.Instance?.MainWindowHandle ?? IntPtr.Zero;
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner; // поверх Revit и закрывается вместе с ним
            Place(window);
            window.Closed += (s, e) => Attach();

            _window = window;
            WindowButton.Content = AttachIcon;
            WindowButton.ToolTip = AttachTip;
            window.Show();
            _vm.SetPaneVisible(true); // журнал виден, даже если панель Revit скрыта
        }

        private void Attach()
        {
            var window = _window;
            if (window == null) return;
            RememberWindow(window);
            _window = null;
            window.Content = null;
            Content = PaneRoot;
            WindowButton.Content = DetachIcon;
            WindowButton.ToolTip = DetachTip;
            _vm.SetPaneVisible(IsVisible);
            QueueSave();
        }

        /// <summary>Прежние положение и размер, если они на одном из мониторов; иначе — по центру экрана.</summary>
        private void Place(Window window)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var saved = _state.WindowWidth > 0 && _state.WindowHeight > 0
                ? new Rect(_state.WindowLeft, _state.WindowTop, _state.WindowWidth, _state.WindowHeight)
                : Rect.Empty;
            if (!saved.IsEmpty && screen.IntersectsWith(saved))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = saved.Left;
                window.Top = saved.Top;
                window.Width = saved.Width;
                window.Height = saved.Height;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Width = Math.Min(1400, screen.Width * 0.8);
                window.Height = Math.Min(800, screen.Height * 0.8);
            }
            if (_state.WindowMaximized) window.WindowState = WindowState.Maximized;
        }

        private void RememberWindow(Window window)
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            _state.WindowMaximized = window.WindowState == WindowState.Maximized;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
            _state.WindowLeft = bounds.Left;
            _state.WindowTop = bounds.Top;
            _state.WindowWidth = bounds.Width;
            _state.WindowHeight = bounds.Height;
        }

        /// <summary>Что остаётся в панели Revit, пока журнал в окне.</summary>
        private FrameworkElement BuildPlaceholder()
        {
            var show = new Button { Content = "Показать окно", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            show.Click += (s, e) => ActivateWindow();
            var back = new Button { Content = "Вернуть в панель", Padding = new Thickness(12, 3, 12, 3) };
            back.Click += (s, e) => _window?.Close();
            return new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Журнал открыт в отдельном окне.", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { show, back } }
                }
            };
        }
    }
}
