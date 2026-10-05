using System.Windows;
using Autodesk.Revit.UI;

namespace RASv2.ModelHistory.UI
{
    public sealed class SpyPaneProvider : IDockablePaneProvider
    {
        private readonly FrameworkElement _content;

        public SpyPaneProvider(FrameworkElement content)
        {
            _content = content;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _content;
            data.VisibleByDefault = false;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Bottom
            };
        }
    }
}
