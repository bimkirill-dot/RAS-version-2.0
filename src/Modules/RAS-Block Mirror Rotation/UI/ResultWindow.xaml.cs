using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using RASv2.BlockMirrorRotation.Revit;

namespace RASv2.BlockMirrorRotation.UI
{
    /// <summary>Итог: сводка, что не вышло — списком (щелчок — вид и элемент), журнал запуска.</summary>
    public partial class ResultWindow : Window
    {
        /// <summary>Строка списка (для привязки — открытый класс).</summary>
        public sealed class Row
        {
            public string View { get; set; }
            public string Element { get; set; }
            public string Problem { get; set; }
            internal ElementId ViewId { get; set; }
            internal ElementId ElementId { get; set; }
        }

        private readonly string _log;
        private readonly Action<ElementId, ElementId> _show;

        internal ResultWindow(Report report, string log, Action<ElementId, ElementId> show)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            _log = log;
            _show = show;
            OpenLog.IsEnabled = log != null;
            var problems = report.Rows.Count + report.RowsDropped;
            Summary.Text = "Модель отражена, оформление восстановлено на видах: " + report.Views +
                           (report.ViewsFailed > 0 ? " (не вышло — " + report.ViewsFailed + ")" : "") + ".\n" +
                           "Осей и уровней — " + report.Datums + ", марок — " + report.Tags + ", высотных отметок поставлено заново — " + report.Spots +
                           ", размеров целы — " + report.Dimensions + ", построено заново — " + report.DimensionsRebuilt + ", текстов — " + report.Texts +
                           (report.Renamed > 0 ? ", осей переименовано — " + report.Renamed : "") + ".\n" +
                           (problems == 0 ? "Всё на месте — проверять листы не нужно." : "Требует внимания: " + problems +
                            (report.RowsDropped > 0 ? " (в списке — первые " + report.Rows.Count + ", все — в журнале)" : "") + ".");
            Rows.ItemsSource = report.Rows.Select(r => new Row { View = r.View, Element = r.Element, Problem = r.Problem, ViewId = r.ViewId, ElementId = r.ElementId }).ToList();
            if (problems == 0)
            {
                Rows.Visibility = System.Windows.Visibility.Collapsed;
                Hint.Visibility = System.Windows.Visibility.Collapsed;
                SizeToContent = SizeToContent.Height;
                Height = double.NaN;
            }
        }

        private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Rows.SelectedItem is Row row && (row.ViewId != null || row.ElementId != null)) _show(row.ViewId, row.ElementId);
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_log) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // нечем открыть .log — покажем путь, его можно открыть руками
                MessageBox.Show(this, "Журнал: " + _log, Title);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
