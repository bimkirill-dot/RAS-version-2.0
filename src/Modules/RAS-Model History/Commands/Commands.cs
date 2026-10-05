using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Commands
{
    /// <summary>Показать / скрыть панель журнала.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ShowPaneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;
                App.Instance?.RememberMainWindow(uiapp);
                var pane = uiapp.GetDockablePane(App.PaneId);
                if (pane.IsShown()) pane.Hide();
                else pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Diag.Log("ShowPane failed", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>История выделенных элементов (или активного вида, если ничего не выделено).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SelectionHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;
                App.Instance?.RememberMainWindow(uiapp);
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null) return Result.Cancelled;

                var ids = uidoc.Selection.GetElementIds().Select(x => x.Value).ToList();
                var pane = uiapp.GetDockablePane(App.PaneId);
                if (!pane.IsShown()) pane.Show();
                var view = uidoc.Document.ActiveView;
                // ничего не выделено — история открытого вида: сам вид и то, что на нём нарисовано (размеры, марки)
                if (ids.Count == 0 && view != null) App.Instance?.ViewModel?.ShowViewHistory(view.Id.Value, view.Name);
                else App.Instance?.ViewModel?.ShowHistoryOf(ids);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Diag.Log("SelectionHistory failed", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Кнопка доступна и без открытой модели (панель, настройки).</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            App.Instance?.RememberMainWindow(uiapp);
            App.Instance?.ShowSettings(uiapp.MainWindowHandle);
            return Result.Succeeded;
        }
    }
}
