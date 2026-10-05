using System.Windows;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Стили панели и окна настроек (ThemeResources.xaml) + общие кисти темы.
    /// Подключается в разметке как объединённый словарь: &lt;local:ThemeResources /&gt; — без pack-адресов,
    /// поэтому работает внутри Revit, где нет своего WPF-приложения.
    /// </summary>
    public partial class ThemeResources : ResourceDictionary
    {
        public ThemeResources()
        {
            InitializeComponent();
            ThemeManager.AddBrushes(this);
        }
    }
}
