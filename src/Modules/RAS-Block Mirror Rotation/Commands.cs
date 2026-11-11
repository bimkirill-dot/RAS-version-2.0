using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RASv2.BlockMirrorRotation
{
    /// <summary>«Отразить блок»: отразить блок и вернуть оформление (MirrorApp).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class MirrorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            MirrorApp.Instance?.Run(commandData.Application);
            return Result.Succeeded;
        }
    }
}
