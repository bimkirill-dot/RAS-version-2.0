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
    /// оба элемента, открыть личный вид по настройкам («Настройки вида» на ленте): 3D с рамкой, план на уровне коллизии, разрез
    /// через неё или «Авто» по элементам; с обрезкой или изолировать; скрыть в виде связи, которые в коллизии не участвуют;
    /// выделить элементы (и в связях) и покрасить цветами Navisworks. Только в контексте Revit API.
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

        public static ShowReply Run(UIApplication app, ShowRequest request, ClashViews views, ClashPaint paint, ViewSettings settings)
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
                    if (settings.WarnMissingLink) warnings.Add($"Связи «{model}» нет в модели «{hostTitle}» — элемент {item.Id} не показан.");
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
                    if (settings.WarnUnloaded) warnings.Add($"Связь «{linkName}» выгружена — загрузите её («Управление связями»), чтобы увидеть элемент {item.Id}.");
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
                return new ShowReply { Error = "Показать нечего — элементов коллизии нет в открытой модели.", Warnings = warnings };
            }

            if (!Bounds(found, out var min, out var max)) return ShowReply.Fail("У элементов коллизии нет видимой формы — показать нечего.");
            var size = max - min;
            var margin = ViewRules.MarginMeters(settings, Math.Max(size.X, Math.Max(size.Y, size.Z)) / Metre) * Metre;
            var kind = ViewRules.KindFor(settings.Kind, found.Select(f => f.Element.Category?.BuiltInCategory.ToString()));

            View view;
            using (var t = new Transaction(doc, ClashViews.TransactionPrefix + "показать коллизию " + request.Clash))
            {
                t.Start();
                view = kind == ViewSettings.KindPlan ? Plan(doc, views, min, max, margin)
                     : kind == ViewSettings.KindSection ? Section(doc, views, found, min, max, margin)
                     : Box3D(doc, views, min, max, margin);
                if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                view.DisplayStyle = Style(settings.Style);
                view.DetailLevel = ViewDetailLevel.Fine;
                ShowOnlyUsedLinks(view, instances, found);
                PaintOwn(doc, view, views, settings.Paint ? found : new List<Found>());
                Isolate(view, found, settings.Isolate);
                t.Commit();
            }

            if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
            views.CloseOthers(uidoc, view.Id);
            uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomAndCenterRectangle(min - Pad(margin), max + Pad(margin));
            uidoc.Selection.SetReferences(found.Select(f => f.Link == null ? new Reference(f.Element) : new Reference(f.Element).CreateLinkReference(f.Link)).ToList());
            if (settings.Paint) paint.Show(doc, view.Id, found.Where(f => f.Link != null).Select(f => (f.Element, f.Transform, f.Color)));
            else paint.Clear();
            uidoc.RefreshActiveView();
            return new ShowReply { Ok = true, Warnings = warnings };
        }

        private static XYZ Pad(double m) => new XYZ(m, m, m);

        /// <summary>3D: рамка вокруг пары с запасом.</summary>
        private static View Box3D(Document doc, ClashViews views, XYZ min, XYZ max, double margin)
        {
            var view = views.Prepare3D(doc);
            view.SetSectionBox(new BoundingBoxXYZ { Min = min - Pad(margin), Max = max + Pad(margin) });
            view.IsSectionBoxActive = true;
            return view;
        }

        /// <summary>
        /// План: уровень — ближайший ниже середины пары (нет такого — самый нижний), секущая плоскость — на высоте середины пары,
        /// видно — только пару с запасом по высоте; подрезка — вокруг пары.
        /// </summary>
        private static View Plan(Document doc, ClashViews views, XYZ min, XYZ max, double margin)
        {
            var center = (min + max) / 2;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            var level = levels.LastOrDefault(l => l.ProjectElevation <= center.Z + 1e-6) ?? levels.First();
            var view = views.NewPlan(doc, level.Id);
            var cut = center.Z - level.ProjectElevation;
            var half = (max.Z - min.Z) / 2 + margin;
            var range = view.GetViewRange();
            foreach (var plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                range.SetLevelId(plane, level.Id);
            range.SetOffset(PlanViewPlane.TopClipPlane, cut + half);
            range.SetOffset(PlanViewPlane.CutPlane, cut);
            range.SetOffset(PlanViewPlane.BottomClipPlane, cut - half);
            range.SetOffset(PlanViewPlane.ViewDepthPlane, cut - half);
            view.SetViewRange(range);
            view.CropBoxActive = true;
            view.CropBox = new BoundingBoxXYZ { Min = new XYZ(min.X - margin, min.Y - margin, min.Z - margin), Max = new XYZ(max.X + margin, max.Y + margin, max.Z + margin) };
            view.CropBoxVisible = false;
            return view;
        }

        /// <summary>
        /// Разрез через середину пары. У стены — вдоль стены (смотрим на стену: видно, где сквозь неё проходит другой элемент);
        /// иначе — смотрим вдоль короткой стороны пары. Глубина — только пара с запасом.
        /// </summary>
        private static View Section(Document doc, ClashViews views, List<Found> found, XYZ min, XYZ max, double margin)
        {
            var center = (min + max) / 2;
            var size = max - min;
            XYZ look = size.X <= size.Y ? XYZ.BasisX : XYZ.BasisY;
            var wall = found.FirstOrDefault(f => f.Element is Wall);
            if (wall != null && wall.Element.Location is LocationCurve lc)
            {
                var along = wall.Transform.OfVector(lc.Curve.ComputeDerivatives(0.5, true).BasisX);
                along = new XYZ(along.X, along.Y, 0);
                if (!along.IsZeroLength()) look = new XYZ(-along.Y, along.X, 0).Normalize();
            }
            var up = XYZ.BasisZ;
            var right = up.CrossProduct(look);
            var t = Transform.Identity;
            t.Origin = center;
            t.BasisX = right;
            t.BasisY = up;
            t.BasisZ = look;
            double w = 0, h = 0, d = 0;
            for (var i = 0; i < 8; i++)
            {
                var p = new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) - center;
                w = Math.Max(w, Math.Abs(p.DotProduct(right)));
                h = Math.Max(h, Math.Abs(p.DotProduct(up)));
                d = Math.Max(d, Math.Abs(p.DotProduct(look)));
            }
            // плоскость разреза — через середину пары (Max.Z), дальняя граница — за парой с запасом
            var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(-w - margin, -h - margin, -d - margin), Max = new XYZ(w + margin, h + margin, 0) };
            var view = views.NewSection(doc, box);
            view.CropBoxVisible = false;
            return view;
        }

        /// <summary>«Изолировать»: временно — только элементы пары (элементы связи по отдельности Revit не изолирует — их связь целиком).</summary>
        private static void Isolate(View view, List<Found> found, bool isolate)
        {
            if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate)) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            if (!isolate) return;
            var ids = found.Select(f => f.Link == null ? f.Element.Id : f.Link.Id).Distinct().ToList();
            view.IsolateElementsTemporary(ids);
        }

        private static DisplayStyle Style(string style) => style switch
        {
            ViewSettings.StyleWireframe => DisplayStyle.Wireframe,
            ViewSettings.StyleHidden => DisplayStyle.HLR,
            ViewSettings.StyleConsistent => DisplayStyle.FlatColors,
            ViewSettings.StyleRealistic => DisplayStyle.Realistic,
            _ => DisplayStyle.Shading,
        };

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

        /// <summary>Габарит пары в координатах открытой модели (у элемента связи — с положением связи).</summary>
        private static bool Bounds(List<Found> found, out XYZ min, out XYZ max)
        {
            min = max = null;
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
            return min != null;
        }

        /// <summary>В нашем виде — только связи коллизии (встречное предложение принято 19.10.2026: вместо «скройте связь»).</summary>
        private static void ShowOnlyUsedLinks(View view, List<RevitLinkInstance> instances, List<Found> found)
        {
            var used = new HashSet<ElementId>(found.Where(f => f.Link != null).Select(f => f.Link.Id));
            var show = instances.Where(x => used.Contains(x.Id) && x.IsHidden(view)).Select(x => x.Id).ToList();
            var hide = instances.Where(x => !used.Contains(x.Id) && !x.IsHidden(view) && x.CanBeHidden(view)).Select(x => x.Id).ToList();
            if (show.Count > 0) view.UnhideElements(show);
            if (hide.Count > 0) view.HideElements(hide);
        }

        /// <summary>Элементы самой модели — переопределением графики в нашем виде; прежние покрашенные — как были.</summary>
        private static void PaintOwn(Document doc, View view, ClashViews views, List<Found> found)
        {
            foreach (var id in views.Painted(doc, view.Id))
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
            views.SetPainted(doc, view.Id, painted);
        }

        private static Color Darker(Color c) => new Color((byte)(c.Red * 0.55), (byte)(c.Green * 0.55), (byte)(c.Blue * 0.55));
    }
}
