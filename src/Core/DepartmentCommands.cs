using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>
    /// Команды переключателя отделов. Кнопка в выпадающем списке Revit не может передать команде, какой отдел выбран, —
    /// поэтому заготовлено Count одинаковых команд «отдел 1…N» по порядку отделов в раскладке (ribbon.txt).
    /// </summary>
    internal static class DepartmentCommands
    {
        public const int Count = 10;

        private static readonly Type[] Types =
        {
            typeof(Department1Command), typeof(Department2Command), typeof(Department3Command), typeof(Department4Command),
            typeof(Department5Command), typeof(Department6Command), typeof(Department7Command), typeof(Department8Command),
            typeof(Department9Command), typeof(Department10Command)
        };

        public static Type Type(int index) => Types[index];
    }

    /// <summary>Показать отдел с номером Index (с нуля) из раскладки.</summary>
    public abstract class DepartmentCommand : IExternalCommand
    {
        protected abstract int Index { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var departments = RibbonBuilder.Departments;
                if (Index < departments.Count) RibbonBuilder.ShowDepartment(departments[Index]);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Write("Ядро", "Не удалось переключить отдел", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)] public sealed class Department1Command : DepartmentCommand { protected override int Index => 0; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department2Command : DepartmentCommand { protected override int Index => 1; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department3Command : DepartmentCommand { protected override int Index => 2; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department4Command : DepartmentCommand { protected override int Index => 3; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department5Command : DepartmentCommand { protected override int Index => 4; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department6Command : DepartmentCommand { protected override int Index => 5; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department7Command : DepartmentCommand { protected override int Index => 6; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department8Command : DepartmentCommand { protected override int Index => 7; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department9Command : DepartmentCommand { protected override int Index => 8; }
    [Transaction(TransactionMode.ReadOnly)] public sealed class Department10Command : DepartmentCommand { protected override int Index => 9; }

    /// <summary>
    /// Кнопка доступна и без открытой модели («О плагине», переключатель отделов). Только для команд ядра: Revit ищет
    /// класс доступности в сборке команды кнопки, поэтому у модулей — свой такой же класс.
    /// </summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
