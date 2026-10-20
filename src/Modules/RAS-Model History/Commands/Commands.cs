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
                // журнал открывается в окне (по умолчанию) — открыть или показать окно, как «Настройки»
                var journal = App.Instance?.Pane;
                if (journal != null && (journal.IsDetached || journal.OpenInWindow))
                {
                    journal.OpenWindow();
                    return Result.Succeeded;
                }
                // закреплённую панель Revit создаёт вместе с первой открытой моделью: на стартовой странице её ещё нет
                // (было — окно «Сбой при выполнении внешней команды», 20.10.2026)
                if (!DockablePane.PaneExists(App.PaneId))
                {
                    TaskDialog.Show("Журнал изменений", "Откройте модель — панель журнала появится вместе с ней.");
                    return Result.Succeeded;
                }
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
                var journal = App.Instance?.Pane;
                if (journal != null && (journal.IsDetached || journal.OpenInWindow)) journal.OpenWindow();
                else
                {
                    var pane = uiapp.GetDockablePane(App.PaneId);
                    if (!pane.IsShown()) pane.Show();
                }
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

    /// <summary>
    /// Кнопка доступна и без открытой модели (панель, настройки). Свой у каждой сборки, а не общий из ядра: Revit ищет
    /// класс доступности в сборке команды кнопки (у ядра — RASv2.Core.AlwaysAvailable для «О плагине» и отделов).
    /// </summary>
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
