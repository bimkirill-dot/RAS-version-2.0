using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// «Показать в Revit» (DECISIONS.md, «Мост в Revit…»): в открытой модели найти модели коллизии (саму модель или связи) и
    /// оба элемента, открыть личный 3D-вид с рамкой вокруг пары, скрыть в нём связи, которые в коллизии не участвуют, выделить
    /// элементы (и в связях) и покрасить цветами Navisworks. Прототип: 3D, рамка «по размеру элементов», «Тонированный»,
    /// красить — да; настройки вида — следующим шагом. Только в контексте Revit API.
    /// </summary>
    internal static class ClashShow
    {
        private const double Metre = 1 / 0.3048;
        /// <summary>Цвета Navisworks по умолчанию: первый элемент — красный, второй — зелёный.</summary>
        private static readonly Color[] Colors = { new Color(255, 0, 0), new Color(0, 255, 0) };

        private sealed class Found
        {
            public Element Element;
            /// <summary>Связь, в которой элемент; null — элемент самой модели.</summary>
            public RevitLinkInstance Link;
            public Transform Transform = Transform.Identity;
            public Color Color;
        }

        public static ShowReply Run(UIApplication app, ShowRequest request, ClashViews views, ClashPaint paint)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return ShowReply.Fail("В Revit не открыта модель — откройте модель, где есть эта коллизия.");
            var doc = uidoc.Document;
            if (doc.IsFamilyDocument) return ShowReply.Fail("В Revit открыто семейство — перейдите на вкладку модели.");
            if (request?.Elements == null || request.Elements.Count == 0) return ShowReply.Fail("У коллизии нет элементов с номерами — показать нечего.");

            // модели, где искать: сама модель (имя файла хранилища и название) и её связи
            var hostTitle = doc.Title;
            var names = new List<string> { HostFile(doc), hostTitle };
            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            names.AddRange(linkTypes.Select(t => t.Name));
            var instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();

            var found = new List<Found>();
            var warnings = new List<string>();
            var notOpen = new List<string>();
            for (var i = 0; i < request.Elements.Count; i++)
            {
                var item = request.Elements[i];
                var model = ModelNames.Bare(item.Model);
                var color = Colors[Math.Min(i, Colors.Length - 1)];
                var k = ModelNames.Match(item.Model, names);
                if (k < 0)
                {
                    notOpen.Add(model);
                    warnings.Add($"Связи «{model}» нет в модели «{hostTitle}» — элемент {item.Id} не показан.");
                    continue;
                }
                if (k < 2)
                {
                    var element = doc.GetElement(new ElementId(item.Id));
                    if (element == null) warnings.Add($"В модели «{hostTitle}» нет элемента {item.Id} — его удалили или модель не та.");
                    else found.Add(new Found { Element = element, Color = color });
                    continue;
                }
                var type = linkTypes[k - 2];
                var linkName = ModelNames.Bare(type.Name);
                if (!RevitLinkType.IsLoaded(doc, type.Id))
                {
                    warnings.Add($"Связь «{linkName}» выгружена — загрузите её («Управление связями»), чтобы увидеть элемент {item.Id}.");
                    continue;
                }
                var instance = instances.FirstOrDefault(x => x.GetTypeId() == type.Id && x.GetLinkDocument() != null);
                var linked = instance?.GetLinkDocument()?.GetElement(new ElementId(item.Id));
                if (linked == null)
                {
                    warnings.Add($"В связи «{linkName}» нет элемента {item.Id} — его удалили или связь не та.");
                    continue;
                }
                found.Add(new Found { Element = linked, Link = instance, Transform = instance.GetTotalTransform(), Color = color });
            }

            if (found.Count == 0)
            {
                if (notOpen.Count == request.Elements.Count)
                    return ShowReply.Fail($"В открытой модели «{hostTitle}» нет моделей этой коллизии: «{string.Join("» и «", notOpen.Distinct())}». " +
                                          "Откройте модель коллизии или добавьте нужные модели связями.");
                return new ShowReply { Error = "Показать нечего.", Warnings = warnings };
            }

            var box = Box(found);
            if (box == null) return ShowReply.Fail("У элементов коллизии нет видимой формы — показать нечего.");

            View3D view;
            using (var t = new Transaction(doc, ClashViews.TransactionPrefix + "показать коллизию " + request.Clash))
            {
                t.Start();
                view = views.Prepare(doc);
                if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                view.DisplayStyle = DisplayStyle.Shading;
                view.DetailLevel = ViewDetailLevel.Fine;
                view.SetSectionBox(box);
                view.IsSectionBoxActive = true;
                ShowOnlyUsedLinks(view, instances, found);
                PaintOwn(doc, view, views, found);
                t.Commit();
            }

            if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
            uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomAndCenterRectangle(box.Min, box.Max);
            uidoc.Selection.SetReferences(found.Select(f => f.Link == null ? new Reference(f.Element) : new Reference(f.Element).CreateLinkReference(f.Link)).ToList());
            paint.Show(doc, view.Id, found.Where(f => f.Link != null).Select(f => (f.Element, f.Transform, f.Color)));
            uidoc.RefreshActiveView();
            return new ShowReply { Ok = true, Warnings = warnings };
        }

        /// <summary>Файл хранилища (у совместной модели — без приписки локальной копии), иначе — файл модели.</summary>
        private static string HostFile(Document doc)
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

        /// <summary>Рамка вокруг пары: запас — половина размера пары, не меньше 0,5 м («по размеру элементов»).</summary>
        private static BoundingBoxXYZ Box(List<Found> found)
        {
            XYZ min = null, max = null;
            foreach (var f in found)
            {
                var bb = f.Element.get_BoundingBox(null);
                if (bb == null) continue;
                for (var i = 0; i < 8; i++)
                {
                    var corner = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                    var p = f.Transform.OfPoint(corner);
                    min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
            if (min == null) return null;
            var size = max - min;
            var margin = Math.Max(0.5 * Metre, 0.5 * Math.Max(size.X, Math.Max(size.Y, size.Z)));
            var m = new XYZ(margin, margin, margin);
            return new BoundingBoxXYZ { Min = min - m, Max = max + m };
        }

        /// <summary>В нашем виде — только связи коллизии (встречное предложение принято 19.10.2026: вместо «скройте связь»).</summary>
        private static void ShowOnlyUsedLinks(View3D view, List<RevitLinkInstance> instances, List<Found> found)
        {
            var used = new HashSet<ElementId>(found.Where(f => f.Link != null).Select(f => f.Link.Id));
            var show = instances.Where(x => used.Contains(x.Id) && x.IsHidden(view)).Select(x => x.Id).ToList();
            var hide = instances.Where(x => !used.Contains(x.Id) && !x.IsHidden(view) && x.CanBeHidden(view)).Select(x => x.Id).ToList();
            if (show.Count > 0) view.UnhideElements(show);
            if (hide.Count > 0) view.HideElements(hide);
        }

        /// <summary>Элементы самой модели — переопределением графики в нашем виде; прежние покрашенные — как были.</summary>
        private static void PaintOwn(Document doc, View3D view, ClashViews views, List<Found> found)
        {
            foreach (var id in views.Painted(doc))
                if (doc.GetElement(id) != null) view.SetElementOverrides(id, new OverrideGraphicSettings());
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            var painted = new List<ElementId>();
            foreach (var f in found.Where(x => x.Link == null))
            {
                var o = new OverrideGraphicSettings()
                    .SetProjectionLineColor(Darker(f.Color))
                    .SetSurfaceForegroundPatternColor(f.Color)
                    .SetCutForegroundPatternColor(f.Color)
                    .SetCutLineColor(Darker(f.Color));
                if (solid != null) o.SetSurfaceForegroundPatternId(solid.Id).SetCutForegroundPatternId(solid.Id);
                view.SetElementOverrides(f.Element.Id, o);
                painted.Add(f.Element.Id);
            }
            views.SetPainted(doc, painted);
        }

        private static Color Darker(Color c) => new Color((byte)(c.Red * 0.55), (byte)(c.Green * 0.55), (byte)(c.Blue * 0.55));
    }
}
