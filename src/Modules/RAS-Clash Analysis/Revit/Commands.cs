using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RASv2.ClashAnalysis
{
    /// <summary>«Настройки вида» — личные настройки вида коллизии (окно модуля).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ViewSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            BridgeApp.Instance?.ShowSettings(commandData.Application.MainWindowHandle, commandData.Application.ActiveUIDocument?.Document);
            return Result.Succeeded;
        }
    }

    /// <summary>«Коллизии элемента»: выделенные элементы (и в связях) — их коллизии на сайте.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ElementClashesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            BridgeApp.Instance?.OpenElementClashes(commandData.Application.ActiveUIDocument);
            return Result.Succeeded;
        }
    }

    /// <summary>«ID элемента связи»: номера выделенных элементов (и в связях) — окном.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ElementIdCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            BridgeApp.Instance?.ShowElementIds(commandData.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>«Задачи выгрузки»: дерево задач выгрузки NWC — окном; работает и без открытой модели.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ExportTasksCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            BridgeApp.Instance?.ShowExportTasks(commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Кнопка доступна и без открытой модели. Свой класс у модуля: Revit ищет его в сборке команды кнопки (CLAUDE.md,
    /// «Правила, о которые уже спотыкались»).
    /// </summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
