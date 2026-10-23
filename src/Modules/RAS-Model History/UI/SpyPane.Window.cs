using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Журнал — всегда в отдельном окне (разработчик, 23.10.2026: «надо, чтобы сразу было отдельное окно»; закрепление в панели
    /// Revit убрано — нажатое случайно, оно давало узкую панель без «Развернуть»): у стыкуемой панели Revit нет кнопки
    /// «Развернуть», а окно растягивается, разворачивается на весь экран и переносится на другой монитор. Содержимое панели
    /// переносится в окно и обратно: переносится корень разметки (PaneRoot) — те же элементы и та же модель представления.
    /// Закрыть окно — закрыть журнал (содержимое тихо возвращается в скрытую панель). Положение окна — в ui.json.
    /// </summary>
    public partial class SpyPane
    {
        private const string DetachIcon = ""; // «Во весь экран» (Segoe MDL2 Assets)
        private const string DetachTip = "Открывать журнал в отдельном окне: его можно развернуть на весь экран или перенести на другой монитор";

        private Window _window;
        private FrameworkElement _placeholder;

        /// <summary>Журнал сейчас в отдельном окне.</summary>
        public bool IsDetached => _window != null;

        /// <summary>Журнал открывается в окне — всегда (прежнее «закрепить в панели», UiState.OpenInWindow = false, не действует).</summary>
        public bool OpenInWindow => true;

        /// <summary>Открыть журнал в окне или показать уже открытое (кнопки ленты).</summary>
        public void OpenWindow()
        {
            if (_window == null)
            {
                Detach();
                ShowRevitPane(false); // панель Revit, открытая с прошлого сеанса (было «закрепить в панели»), — убрать
            }
            else ActivateWindow();
        }

        /// <summary>Кнопка видна, только если журнал оказался в панели Revit (панель, открытая Revit с прошлого сеанса), — в окно.</summary>
        private void WindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsDetached) MoveToWindow();
        }

        /// <summary>Из панели — в окно; дальше журнал открывается в окне, панель Revit скрывается.</summary>
        private void MoveToWindow()
        {
            _state.OpenInWindow = true;
            Detach();
            ShowRevitPane(false);
        }

        /// <summary>Показать / скрыть стыкуемую панель — это Revit API, поэтому через ExternalEvent.</summary>
        private static void ShowRevitPane(bool show)
        {
            App.Instance?.Actions?.Run(app =>
            {
                if (!Autodesk.Revit.UI.DockablePane.PaneExists(App.PaneId)) return; // модели ещё нет — панели тоже (Revit создаёт её с первой моделью)
                var pane = app.GetDockablePane(App.PaneId);
                if (show) pane.Show();
                else pane.Hide();
            });
        }

        private void ActivateWindow()
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
            WindowButton.Visibility = Visibility.Collapsed; // в окне — закреплять некуда
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
            WindowButton.Visibility = Visibility.Visible;
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
            return new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Журнал открыт в отдельном окне.", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { show } }
                }
            };
        }
    }
}
