using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.UI;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// «ID элемента связи» (разработчик, 29.10.2026): номера выделенных элементов — в связях (выделены клавишей Tab) и в самой
    /// модели, с тем, где элемент и что это. Только в контексте Revit API.
    /// </summary>
    internal static class ElementIds
    {
        /// <summary>Выделенное; ничего — пусто, а wholeLinks — сколько связей выделено целиком.</summary>
        public static List<ElementIdWindow.Item> Selected(UIDocument uidoc, out int wholeLinks)
        {
            var doc = uidoc.Document;
            var items = new List<ElementIdWindow.Item>();
            wholeLinks = 0;
            foreach (var r in uidoc.Selection.GetReferences())
            {
                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    if (!(doc.GetElement(r.ElementId) is RevitLinkInstance link)) continue;
                    var name = doc.GetElement(link.GetTypeId()) is RevitLinkType type ? ModelNames.Bare(type.Name) : link.Name;
                    items.Add(Item("Связь «" + name + "»", link.GetLinkDocument()?.GetElement(r.LinkedElementId), r.LinkedElementId));
                    continue;
                }
                var element = doc.GetElement(r.ElementId);
                if (element is RevitLinkInstance) { wholeLinks++; continue; }
                if (element != null) items.Add(Item("Модель «" + doc.Title + "»", element, element.Id));
            }
            return items.GroupBy(i => i.Model + "|" + i.Id).Select(g => g.First()).ToList();
        }

        /// <summary>Что это — категория и семейство с типоразмером, как пишет Revit («Стены · Базовая стена: 200»).</summary>
        private static ElementIdWindow.Item Item(string model, Element element, ElementId id)
        {
            var parts = new List<string>();
            if (element?.Category != null) parts.Add(element.Category.Name);
            if (element != null && element.Document.GetElement(element.GetTypeId()) is ElementType type)
                parts.Add(string.IsNullOrEmpty(type.FamilyName) ? type.Name : type.FamilyName + ": " + type.Name);
            else if (!string.IsNullOrEmpty(element?.Name)) parts.Add(element.Name);
            return new ElementIdWindow.Item
            {
                Model = model,
                What = parts.Count > 0 ? string.Join(" · ", parts) : "—",
                Id = RevitCompat.Value(id).ToString(),
            };
        }
    }
}
