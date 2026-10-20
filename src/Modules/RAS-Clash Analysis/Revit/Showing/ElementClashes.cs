using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Reports;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// «Коллизии элемента» (DECISIONS.md, «Мост в Revit…»): выделенные элементы (в самой модели и в связях — Tab) — на сайт:
    /// браузер открывает «/element?e=файл модели|номер». Искать в базе — дело сайта (в базу пишет и её читает только сайт),
    /// он же говорит «Коллизий нет». Только в контексте Revit API.
    /// </summary>
    internal static class ElementClashes
    {
        /// <summary>Открыть на сайте; не вышло (ничего не выделено) — что сказать человеку, иначе null.</summary>
        public static string Open(UIDocument uidoc, string site)
        {
            var doc = uidoc.Document;
            var host = ModelNames.Bare(ModelFiles.Host(doc));
            var items = new List<string>();
            var wholeLinks = 0;
            foreach (var r in uidoc.Selection.GetReferences())
            {
                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    // элемент внутри связи: модель — файл связи
                    if (doc.GetElement(r.ElementId) is RevitLinkInstance link && doc.GetElement(link.GetTypeId()) is RevitLinkType type)
                        items.Add(ModelNames.Bare(type.Name) + "|" + RevitCompat.Value(r.LinkedElementId));
                    continue;
                }
                var element = doc.GetElement(r.ElementId);
                if (element is RevitLinkInstance) { wholeLinks++; continue; }
                if (element != null) items.Add(host + "|" + RevitCompat.Value(element.Id));
            }
            items = items.Distinct().ToList();
            if (items.Count == 0)
                return wholeLinks > 0
                    ? "Выделена связь целиком. Выделите элемент внутри связи: наведите на него и нажимайте Tab, пока он не подсветится, затем щёлкните."
                    : "Выделите элемент (в связи — клавишей Tab) и нажмите «Коллизии элемента» ещё раз.";
            var url = site.TrimEnd('/') + "/element?" + string.Join("&", items.Select(i => "e=" + Uri.EscapeDataString(i)));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
    }

    /// <summary>Имя файла модели в Revit — для сопоставления с моделью из отчёта Navisworks.</summary>
    internal static class ModelFiles
    {
        /// <summary>Файл хранилища (у совместной модели — без приписки локальной копии), иначе — файл модели.</summary>
        public static string Host(Document doc)
        {
            try
            {
                if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null) return ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // хранилище недоступно — по имени файла
            }
            return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        }
    }
}
