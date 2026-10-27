using System.Windows;

namespace RASv2.Core
{
    /// <summary>Общие образцы окон модулей (WindowStyles.xaml): создаётся классом, а не по адресу pack:// — в Revit надёжнее.</summary>
    public partial class WindowStyles : ResourceDictionary
    {
        public WindowStyles()
        {
            InitializeComponent();
        }
    }
}
