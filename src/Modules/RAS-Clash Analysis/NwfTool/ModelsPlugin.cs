#if NAVISWORKS
using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NwApp = Autodesk.Navisworks.Api.Application;

namespace RASv2.ClashAnalysis.NwfBuild
{
    /// <summary>
    /// Работает внутри Navisworks, запущенного программой (AddPluginAssembly — эта же программа): какие NWC в открытой NWF — по
    /// строке в файл (параметр). Automation API этого не умеет. Во вкладке надстроек не показывается.
    /// </summary>
    [Plugin("RasNwfModels", "RASV", DisplayName = "RAS v. 2.0 — модели NWF")]
    [AddInPlugin(AddInLocation.None)]
    public class ModelsPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            var doc = NwApp.ActiveDocument;
            File.WriteAllLines(parameters[0], doc.Models.Select(m => m.FileName ?? "").ToArray(), new UTF8Encoding(false));
            return 0;
        }
    }
}
#endif
