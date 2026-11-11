using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RASv2.Core;

namespace RASv2.BlockMirrorRotation.Revit
{
    /// <summary>Ось или уровень на виде: концы на виде, тип концов (2D / 3D), кружки, координаты концов вдоль оси вида.</summary>
    internal sealed class DatumState
    {
        public ElementId Id;
        public string Name;
        public XYZ End0, End1;
        public DatumExtentType Type0, Type1;
        public bool Bubble0, Bubble1;
        public double Along0, Along1;
    }

    internal sealed class TagState
    {
        public ElementId Id;
        public XYZ Head;
        public TagOrientation Orientation;
        public double Angle;
        public bool HasAngle;
        public bool HasLeader;
        public bool FreeEnd;
        public List<XYZ> Ends = new List<XYZ>();
        public List<XYZ> Elbows = new List<XYZ>();
    }

    internal sealed class SpotState
    {
        public ElementId Id;
        public ElementId TypeId;
        public string Reference;
        public XYZ Origin, LeaderEnd, Shoulder;
        public bool HasLeader;
        public List<KeyValuePair<ElementId, object>> Parameters = new List<KeyValuePair<ElementId, object>>();
    }

    internal sealed class DimensionState
    {
        public ElementId Id;
        public ElementId TypeId;
        public List<string> References = new List<string>();
        public XYZ Origin, Direction;
        public int Segments;
        public List<SegmentText> Texts = new List<SegmentText>();
    }

    internal sealed class SegmentText
    {
        public string Override, Prefix, Suffix, Above, Below;
    }

    internal sealed class TextState
    {
        public ElementId Id;
        public XYZ Coord, Base;
        public List<XYZ> Ends = new List<XYZ>();
        public List<XYZ> Elbows = new List<XYZ>();
    }

    internal sealed class OtherState
    {
        public ElementId Id;
        public string What;
        public XYZ Center;
    }

    internal sealed class ViewState
    {
        public ElementId Id;
        public string Name;
        public int Scale;
        public List<DatumState> Datums = new List<DatumState>();
        public List<TagState> Tags = new List<TagState>();
        public List<SpotState> Spots = new List<SpotState>();
        public List<DimensionState> Dimensions = new List<DimensionState>();
        public List<TextState> Texts = new List<TextState>();
        public List<OtherState> Others = new List<OtherState>();
    }

    /// <summary>
    /// Оформление видов на листах до «Отразить проект» (DECISIONS.md, «Прототип»): всё — в координатах модели, чтобы после
    /// отражения поставить в ту же точку, отражённую вместе с моделью. Плюс точки элементов модели — по ним после команды
    /// находится, какое было отражение.
    /// </summary>
    internal sealed class Snapshot
    {
        public List<ViewState> Views = new List<ViewState>();
        public Dictionary<ElementId, XYZ> ModelPoints = new Dictionary<ElementId, XYZ>();
        public List<string> Skipped = new List<string>();

        private static readonly ViewType[] Kinds =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section,
            ViewType.Elevation, ViewType.Detail,
        };

        /// <summary>Виды на листах, где есть оформление модели (планы, разрезы, фасады, узлы; без 3D, чертёжных и легенд).</summary>
        public static List<View> ViewsOnSheets(Document doc)
        {
            var ids = new HashSet<ElementId>();
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (sheet.IsTemplate) continue;
                foreach (var id in sheet.GetAllPlacedViews()) ids.Add(id);
            }
            return ids.Select(doc.GetElement).OfType<View>().Where(v => !v.IsTemplate && Kinds.Contains(v.ViewType))
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static Snapshot Capture(Document doc, IList<View> views, Action<int> progress)
        {
            var s = new Snapshot();
            for (var i = 0; i < views.Count; i++)
            {
                progress?.Invoke(i);
                s.Views.Add(CaptureView(doc, views[i], s.Skipped));
            }
            s.ModelPoints = ModelPointsOf(doc);
            return s;
        }

        /// <summary>
        /// Точки элементов модели для поиска отражения: середины осей, точки семейств, середины стен — до 600 штук. Те же
        /// номера после команды дают пары «до → после».
        /// </summary>
        public static Dictionary<ElementId, XYZ> ModelPointsOf(Document doc, ICollection<ElementId> only = null)
        {
            var points = new Dictionary<ElementId, XYZ>();
            if (only != null)
            {
                foreach (var id in only)
                {
                    var p = PointOf(doc.GetElement(id));
                    if (p != null) points[id] = p;
                }
                return points;
            }
            foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                var p = PointOf(grid);
                if (p != null) points[grid.Id] = p;
            }
            foreach (var type in new[] { typeof(FamilyInstance), typeof(Wall) })
            {
                var taken = 0;
                foreach (var e in new FilteredElementCollector(doc).OfClass(type).WhereElementIsNotElementType())
                {
                    if (taken >= 300) break;
                    if (e.ViewSpecific) continue;
                    var p = PointOf(e);
                    if (p == null) continue;
                    points[e.Id] = p;
                    taken++;
                }
            }
            return points;
        }

        private static XYZ PointOf(Element e)
        {
            try
            {
                if (e is Grid g) return g.IsCurved ? null : g.Curve.Evaluate(0.5, true);
                if (e?.Location is LocationPoint lp) return lp.Point;
                if (e?.Location is LocationCurve lc && lc.Curve.IsBound) return lc.Curve.Evaluate(0.5, true);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // у элемента нет точки (вложенное семейство, разрушенная ось) — для поиска отражения он просто не нужен
            }
            return null;
        }

        private static ViewState CaptureView(Document doc, View view, List<string> skipped)
        {
            var v = new ViewState { Id = view.Id, Name = view.Name, Scale = Math.Max(1, view.Scale) };
            var right = view.RightDirection;
            var up = view.UpDirection;
            // уровни — только на разрезах, фасадах и узлах: на планах их линий нет
            var withLevels = view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail;
            var datums = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<DatumPlane>().ToList();
            if (withLevels) datums.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<DatumPlane>());
            foreach (var datum in datums)
            {
                if (datum is Grid g && g.IsCurved) continue;
                try
                {
                    var curve = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
                    if (curve == null || !curve.IsBound) continue;
                    var p0 = curve.GetEndPoint(0);
                    var p1 = curve.GetEndPoint(1);
                    var axis = AxisAlong(p1 - p0, right, up);
                    v.Datums.Add(new DatumState
                    {
                        Id = datum.Id, Name = datum.Name, End0 = p0, End1 = p1,
                        Type0 = datum.GetDatumExtentTypeInView(DatumEnds.End0, view),
                        Type1 = datum.GetDatumExtentTypeInView(DatumEnds.End1, view),
                        Bubble0 = datum.IsBubbleVisibleInView(DatumEnds.End0, view),
                        Bubble1 = datum.IsBubbleVisibleInView(DatumEnds.End1, view),
                        Along0 = p0.DotProduct(axis), Along1 = p1.DotProduct(axis),
                    });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add($"{view.Name}: «{datum.Name}» не запомнена — {ex.Message}");
                }
            }

            foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (e.OwnerViewId != view.Id) continue;
                try
                {
                    switch (e)
                    {
                        case IndependentTag tag:
                            v.Tags.Add(TagOf(tag));
                            break;
                        case SpotDimension spot when IsSpotElevation(spot):
                            v.Spots.Add(SpotOf(doc, spot));
                            break;
                        case SpotDimension _:
                            v.Others.Add(OtherOf(e, view));
                            break;
                        case Dimension dim when dim.DimensionShape == DimensionShape.Linear:
                            v.Dimensions.Add(DimensionOf(doc, dim));
                            break;
                        case TextNote text:
                            v.Texts.Add(TextOf(text));
                            break;
                        default:
                            if (e.Category == null || e is Dimension) break;
                            var other = OtherOf(e, view);
                            if (other != null) v.Others.Add(other);
                            break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add($"{view.Name}: {Describe(e)} не запомнен — {ex.Message}");
                }
            }
            v.Others.RemoveAll(o => o == null);
            return v;
        }

        /// <summary>Ось вида, вдоль которой идёт линия: вправо или вверх.</summary>
        public static XYZ AxisAlong(XYZ direction, XYZ right, XYZ up) =>
            Math.Abs(direction.DotProduct(right)) >= Math.Abs(direction.DotProduct(up)) ? right : up;

        public static bool IsSpotElevation(SpotDimension spot) =>
            spot.Category != null && RevitCompat.Value(spot.Category.Id) == (long)BuiltInCategory.OST_SpotElevations;

        private static TagState TagOf(IndependentTag tag)
        {
            var t = new TagState
            {
                Id = tag.Id, Head = tag.TagHeadPosition, Orientation = tag.TagOrientation, HasLeader = tag.HasLeader,
            };
            try
            {
                t.Angle = tag.RotationAngle;
                t.HasAngle = true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // угла нет у этой марки (старый вид марки) — ставится только ориентация
            }
            if (!tag.HasLeader) return t;
            t.FreeEnd = tag.LeaderEndCondition == LeaderEndCondition.Free;
            foreach (var r in tag.GetTaggedReferences())
            {
                t.Ends.Add(t.FreeEnd ? Try(() => tag.GetLeaderEnd(r)) : null);
                t.Elbows.Add(Try(() => tag.HasLeaderElbow(r) ? tag.GetLeaderElbow(r) : null));
            }
            return t;
        }

        private static SpotState SpotOf(Document doc, SpotDimension spot)
        {
            var refs = spot.References;
            var s = new SpotState
            {
                Id = spot.Id, TypeId = spot.GetTypeId(), Origin = spot.Origin,
                Reference = refs != null && refs.Size > 0 ? refs.get_Item(0).ConvertToStableRepresentation(doc) : null,
                LeaderEnd = Try(() => spot.LeaderEndPosition),
                Shoulder = Try(() => spot.LeaderHasShoulder ? spot.LeaderShoulderPosition : null),
            };
            s.HasLeader = s.LeaderEnd != null && s.LeaderEnd.DistanceTo(s.Origin) > 1e-6;
            foreach (Parameter p in spot.Parameters)
            {
                if (p.IsReadOnly || !p.HasValue) continue;
                object value;
                switch (p.StorageType)
                {
                    case StorageType.Integer: value = p.AsInteger(); break;
                    case StorageType.Double: value = p.AsDouble(); break;
                    case StorageType.String: value = p.AsString(); break;
                    case StorageType.ElementId: value = p.AsElementId(); break;
                    default: continue;
                }
                s.Parameters.Add(new KeyValuePair<ElementId, object>(p.Id, value));
            }
            return s;
        }

        private static DimensionState DimensionOf(Document doc, Dimension dim)
        {
            var d = new DimensionState { Id = dim.Id, TypeId = dim.GetTypeId(), Segments = Math.Max(1, dim.NumberOfSegments) };
            foreach (Reference r in dim.References) d.References.Add(r.ConvertToStableRepresentation(doc));
            d.Origin = dim.Origin;
            d.Direction = (dim.Curve as Line)?.Direction;
            if (dim.NumberOfSegments == 0)
                d.Texts.Add(new SegmentText { Override = dim.ValueOverride, Prefix = dim.Prefix, Suffix = dim.Suffix, Above = dim.Above, Below = dim.Below });
            else
                foreach (DimensionSegment seg in dim.Segments)
                    d.Texts.Add(new SegmentText { Override = seg.ValueOverride, Prefix = seg.Prefix, Suffix = seg.Suffix, Above = seg.Above, Below = seg.Below });
            return d;
        }

        private static TextState TextOf(TextNote text)
        {
            var t = new TextState { Id = text.Id, Coord = text.Coord, Base = text.BaseDirection };
            foreach (var leader in text.GetLeaders())
            {
                t.Ends.Add(leader.End);
                t.Elbows.Add(Try(() => leader.Elbow));
            }
            return t;
        }

        private static OtherState OtherOf(Element e, View view)
        {
            var box = e.get_BoundingBox(view);
            if (box == null) return null;
            return new OtherState { Id = e.Id, What = Describe(e), Center = (box.Min + box.Max) / 2 };
        }

        public static string Describe(Element e)
        {
            var category = e.Category?.Name ?? "элемент";
            return category + " " + RevitCompat.Value(e.Id);
        }

        private static XYZ Try(Func<XYZ> get)
        {
            try
            {
                return get();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // у этой выноски точки нет (прикреплённый конец, нет излома) — восстанавливать нечего
                return null;
            }
        }
    }
}
