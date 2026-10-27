using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RASv2.SheetManager
{
    /// <summary>«Подпись»: очистка DWG-подписей и добавление их в семейство подписей (SignApp).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SignCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SignApp.Instance?.Run(commandData.Application);
            return Result.Succeeded;
        }
    }
}
