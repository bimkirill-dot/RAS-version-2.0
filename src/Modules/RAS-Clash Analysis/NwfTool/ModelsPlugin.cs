#if NAVISWORKS
using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
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

    /// <summary>
    /// Скрыть: наборы, только что загруженные встроенным импортом, — по именам (параметры после файла итога): найденное — скрыть,
    /// набор — убрать. Итог — «имя\tсколько скрыто» (−1 — набора нет).
    /// </summary>
    [Plugin("RasNwfHide", "RASV", DisplayName = "RAS v. 2.0 — скрыть по наборам")]
    [AddInPlugin(AddInLocation.None)]
    public class HidePlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            var doc = NwApp.ActiveDocument;
            var lines = new System.Collections.Generic.List<string>();
            foreach (var name in parameters.Skip(1))
            {
                var set = All(doc.SelectionSets.RootItem).OfType<SelectionSet>().LastOrDefault(s => s.DisplayName == name);
                if (set == null)
                {
                    // в журнал — какие наборы есть (разобрать, почему не нашёлся)
                    lines.Add(name + "\t-1\t" + string.Join(" | ", All(doc.SelectionSets.RootItem).Select(s => s.GetType().Name + ":" + s.DisplayName).Take(20)));
                    continue;
                }
                var items = set.HasSearch ? set.Search.FindAll(doc, false) : set.GetSelectedItems(doc);
                if (items.Count > 0) doc.Models.SetHidden(items, true);
                lines.Add(name + "\t" + items.Count);
                doc.SelectionSets.Remove(set);
            }
            File.WriteAllLines(parameters[0], lines, new UTF8Encoding(false));
            return 0;
        }

        private static System.Collections.Generic.IEnumerable<SavedItem> All(GroupItem g)
        {
            foreach (var c in g.Children)
            {
                yield return c;
                if (c is GroupItem inner)
                    foreach (var x in All(inner)) yield return x;
            }
        }
    }

    /// <summary>
    /// Проверки Clash Detective: «count» — сколько проверок в NWF (в файл); «run» — прогнать все, по каждой «имя\tсостояние\tколлизий\tновых».
    /// </summary>
    [Plugin("RasNwfTests", "RASV", DisplayName = "RAS v. 2.0 — проверки NWF")]
    [AddInPlugin(AddInLocation.None)]
    public class TestsPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            var doc = NwApp.ActiveDocument;
            var data = doc.GetClash().TestsData;
            var tests = All(data.Tests).ToList();
            if (parameters.Length > 1 && parameters[1] == "clear")
            {
                data.TestsClear();
                File.WriteAllText(parameters[0], "0");
                return 0;
            }
            if (parameters.Length > 1 && parameters[1] == "run")
            {
                data.TestsRunAllTests();
                tests = All(data.Tests).ToList();
                File.WriteAllLines(parameters[0], tests.Select(t =>
                {
                    var results = Results(t).ToList();
                    return t.DisplayName + "\t" + t.Status + "\t" + results.Count + "\t" + results.Count(r => r.Status == ClashResultStatus.New);
                }).ToArray(), new UTF8Encoding(false));
            }
            else File.WriteAllText(parameters[0], tests.Count.ToString());
            return 0;
        }

        private static System.Collections.Generic.IEnumerable<ClashTest> All(System.Collections.Generic.IEnumerable<SavedItem> items)
        {
            foreach (var i in items)
            {
                if (i is ClashTest t) yield return t;
                else if (i is GroupItem g)
                    foreach (var x in All(g.Children)) yield return x;
            }
        }

        private static System.Collections.Generic.IEnumerable<ClashResult> Results(GroupItem g)
        {
            foreach (var i in g.Children)
            {
                if (i is ClashResult r) yield return r;
                else if (i is GroupItem inner)
                    foreach (var x in Results(inner)) yield return x;
            }
        }
    }
}
#endif
