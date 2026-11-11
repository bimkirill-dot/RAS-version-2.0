using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RASv2.BlockMirrorRotation.Logic;
using RASv2.Core;

namespace RASv2.BlockMirrorRotation.Revit
{
    /// <summary>Строка итога: вид, элемент, что не так. Щелчок в окне итога открывает вид и выделяет элемент.</summary>
    internal sealed class ReportRow
    {
        public ElementId ViewId { get; set; }
        public string View { get; set; }
        public ElementId ElementId { get; set; }
        public string Element { get; set; }
        public string Problem { get; set; }
    }

    internal sealed class Report
    {
        public int Views, ViewsFailed, Datums, Tags, Spots, Dimensions, DimensionsRebuilt, Texts, Others, Renamed;
        public List<ReportRow> Rows = new List<ReportRow>();
        public List<string> Notes = new List<string>();
        public const int MaxRows = 3000;
        public int RowsDropped;

        public void Add(View view, ElementId element, string what, string problem)
        {
            if (Rows.Count >= MaxRows)
            {
                RowsDropped++;
                return;
            }
            Rows.Add(new ReportRow { ViewId = view?.Id, View = view?.Name ?? "—", ElementId = element, Element = what ?? "—", Problem = problem });
        }
    }

    /// <summary>
    /// Восстановление оформления после «Отразить проект» (DECISIONS.md, «Описание до реализации» и «Прототип»): каждая
    /// запомненная точка — в ту же точку, отражённую вместе с моделью (map), угол марок и направление текста — прежние,
    /// кружки — с тех же сторон листа, отметки — заново, сломанные размеры — заново. По операции на вид: ошибка вида
    /// откатывает только его.
    /// </summary>
    internal sealed class Restorer
    {
        private const double Eps = 1e-6;
        private readonly Document _doc;
        private readonly PlanTransform _map;
        private readonly Report _report = new Report();

        public Restorer(Document doc, PlanTransform map)
        {
            _doc = doc;
            _map = map;
        }

        public Report Run(Snapshot snapshot, bool renameGrids, Action<int> progress)
        {
            _report.Notes.AddRange(snapshot.Skipped.Take(200));
            for (var i = 0; i < snapshot.Views.Count; i++)
            {
                progress?.Invoke(i);
                RestoreView(snapshot.Views[i]);
            }
            if (renameGrids) RenameGrids();
            return _report;
        }

        private XYZ Map(XYZ p)
        {
            if (p == null) return null;
            _map.Apply(p.X, p.Y, out var x, out var y);
            return new XYZ(x, y, p.Z);
        }

        private XYZ MapVector(XYZ v)
        {
            if (v == null) return null;
            _map.ApplyVector(v.X, v.Y, out var x, out var y);
            return new XYZ(x, y, v.Z);
        }

        private void RestoreView(ViewState state)
        {
            var view = _doc.GetElement(state.Id) as View;
            if (view == null)
            {
                _report.ViewsFailed++;
                _report.Add(null, null, state.Name, "вид пропал после отражения — оформление не восстановлено");
                return;
            }
            var errors = new List<string>();
            using (var t = new Transaction(_doc, "RAS: оформление вида «" + view.Name + "»"))
            {
                var options = t.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new QuietWarnings(errors));
                options.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(options);
                t.Start();
                try
                {
                    foreach (var d in state.Datums) One(view, d.Id, d.Name, () => Datum(view, d));
                    foreach (var tag in state.Tags) One(view, tag.Id, null, () => Tag(tag));
                    foreach (var text in state.Texts) One(view, text.Id, null, () => Text(view, text));
                    foreach (var spot in state.Spots) One(view, spot.Id, null, () => Spot(view, spot));
                    foreach (var dim in state.Dimensions) One(view, dim.Id, null, () => Dimension(view, dim));
                    foreach (var other in state.Others) Other(view, state, other);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    _report.ViewsFailed++;
                    _report.Add(view, null, "вид", "восстановление вида прервано — " + ex.Message);
                    return;
                }
                if (t.Commit() != TransactionStatus.Committed)
                {
                    _report.ViewsFailed++;
                    _report.Add(view, null, "вид", "Revit не принял изменения вида, всё на виде — как после отражения" +
                                                   (errors.Count > 0 ? ": " + string.Join("; ", errors.Distinct().Take(3)) : ""));
                    return;
                }
            }
            _report.Views++;
        }

        /// <summary>Один элемент: ошибка — строка итога, остальные элементы вида идут дальше.</summary>
        private void One(View view, ElementId id, string name, Action body)
        {
            try
            {
                body();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                var e = _doc.GetElement(id);
                _report.Add(view, id, e != null ? Snapshot.Describe(e) : name ?? "элемент " + RevitCompat.Value(id), "не восстановлен — " + ex.Message);
            }
        }

        // ── оси и уровни ──

        private void Datum(View view, DatumState d)
        {
            var datum = _doc.GetElement(d.Id) as DatumPlane;
            if (datum == null)
            {
                _report.Add(view, d.Id, "«" + d.Name + "»", "ось (уровень) удалена после отражения");
                return;
            }
            var current = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
            if (current == null || !current.IsBound) return;
            var q0 = current.GetEndPoint(0);
            var q1 = current.GetEndPoint(1);
            var a = Map(d.End0);
            var b = Map(d.End1);
            // какой новый конец — бывший конец 0: тот, что ближе к его отражённой точке
            var swap = a.DistanceTo(q1) + b.DistanceTo(q0) < a.DistanceTo(q0) + b.DistanceTo(q1);
            var t0 = swap ? b : a;
            var t1 = swap ? a : b;
            var type0 = swap ? d.Type1 : d.Type0;
            var type1 = swap ? d.Type0 : d.Type1;
            var u = (q1 - q0).Normalize();
            var p0 = q0 + u * (t0 - q0).DotProduct(u);
            var p1 = q0 + u * (t1 - q0).DotProduct(u);
            if ((p1 - p0).DotProduct(u) <= Eps)
            {
                _report.Add(view, d.Id, "«" + d.Name + "»", "концы не восстановлены: после отражения они поменялись местами");
                return;
            }
            if (type0 == DatumExtentType.ViewSpecific) datum.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
            if (type1 == DatumExtentType.ViewSpecific) datum.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
            if (type0 == DatumExtentType.ViewSpecific || type1 == DatumExtentType.ViewSpecific)
                datum.SetCurveInView(DatumExtentType.ViewSpecific, view, Line.CreateBound(p0, p1));
            if (type0 == DatumExtentType.Model && datum.GetDatumExtentTypeInView(DatumEnds.End0, view) != DatumExtentType.Model)
                datum.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.Model);
            if (type1 == DatumExtentType.Model && datum.GetDatumExtentTypeInView(DatumEnds.End1, view) != DatumExtentType.Model)
                datum.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.Model);

            // кружки — с тех же сторон листа; координаты вдоль оси вида — по нынешним концам
            var now = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view).First();
            var axis = Snapshot.AxisAlong(now.GetEndPoint(1) - now.GetEndPoint(0), view.RightDirection, view.UpDirection);
            BubbleSides.Choose(d.Bubble0, d.Bubble1, d.Along0, d.Along1,
                               now.GetEndPoint(0).DotProduct(axis), now.GetEndPoint(1).DotProduct(axis), out var show0, out var show1);
            Bubble(datum, view, DatumEnds.End0, show0);
            Bubble(datum, view, DatumEnds.End1, show1);
            _report.Datums++;
        }

        private static void Bubble(DatumPlane datum, View view, DatumEnds end, bool show)
        {
            if (datum.IsBubbleVisibleInView(end, view) == show) return;
            if (show) datum.ShowBubbleInView(end, view);
            else datum.HideBubbleInView(end, view);
        }

        // ── марки ──

        private void Tag(TagState s)
        {
            var tag = _doc.GetElement(s.Id) as IndependentTag;
            if (tag == null) return;
            if (tag.TagOrientation != s.Orientation) tag.TagOrientation = s.Orientation;
            if (s.HasAngle && Math.Abs(tag.RotationAngle - s.Angle) > Eps) tag.RotationAngle = s.Angle;
            tag.TagHeadPosition = Map(s.Head);
            if (s.HasLeader && tag.HasLeader)
            {
                var refs = tag.GetTaggedReferences();
                if (refs.Count == s.Ends.Count)
                    for (var i = 0; i < refs.Count; i++)
                    {
                        if (s.FreeEnd && s.Ends[i] != null && tag.LeaderEndCondition == LeaderEndCondition.Free)
                            tag.SetLeaderEnd(refs[i], Map(s.Ends[i]));
                        if (s.Elbows[i] != null) tag.SetLeaderElbow(refs[i], Map(s.Elbows[i]));
                    }
            }
            _report.Tags++;
        }

        // ── текст ──

        private void Text(View view, TextState s)
        {
            var text = _doc.GetElement(s.Id) as TextNote;
            if (text == null) return;
            var target = Map(s.Coord);
            var shift = target - text.Coord;
            if (shift.GetLength() > Eps) ElementTransformUtils.MoveElement(_doc, text.Id, shift);
            // направление текста — прежнее (текст читается так же): поворот вокруг направления взгляда вида
            var now = text.BaseDirection;
            var axis = view.ViewDirection;
            var angle = Math.Atan2(now.CrossProduct(s.Base).DotProduct(axis), now.DotProduct(s.Base));
            if (Math.Abs(angle) > Eps) ElementTransformUtils.RotateElement(_doc, text.Id, Line.CreateBound(target, target + axis), angle);
            var leaders = text.GetLeaders();
            if (leaders.Count == s.Ends.Count)
                for (var i = 0; i < leaders.Count; i++)
                {
                    leaders[i].End = Map(s.Ends[i]);
                    if (s.Elbows[i] != null) leaders[i].Elbow = Map(s.Elbows[i]);
                }
            _report.Texts++;
        }

        // ── высотные отметки: заново ──

        private void Spot(View view, SpotState s)
        {
            var name = "высотная отметка " + RevitCompat.Value(s.Id);
            if (s.Reference == null)
            {
                _report.Add(view, s.Id, name, "не к чему привязать заново — оставлена как есть");
                return;
            }
            Reference reference;
            try
            {
                reference = Reference.ParseFromStableRepresentation(_doc, s.Reference);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                _report.Add(view, s.Id, name, "элемента, на котором стояла отметка, больше нет — оставлена как есть");
                return;
            }
            var origin = Map(s.Origin);
            XYZ end, bend;
            if (s.HasLeader)
            {
                end = Map(s.LeaderEnd);
                bend = s.Shoulder != null ? Map(s.Shoulder) : end;
            }
            else
            {
                // без выноски точки излома и конца не видны, но API нужны разные точки — в плоскости вида
                bend = origin + view.UpDirection + view.RightDirection;
                end = bend + view.RightDirection;
            }
            var created = _doc.Create.NewSpotElevation(view, reference, origin, bend, end, origin, s.HasLeader);
            if (created == null)
            {
                _report.Add(view, s.Id, name, "Revit не поставил отметку заново — оставлена как есть");
                return;
            }
            if (created.GetTypeId() != s.TypeId) created.ChangeTypeId(s.TypeId);
            foreach (var kv in s.Parameters)
            {
                var p = created.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == kv.Key);
                if (p == null || p.IsReadOnly) continue;
                try
                {
                    switch (kv.Value)
                    {
                        case int i: p.Set(i); break;
                        case double v: p.Set(v); break;
                        case string str: p.Set(str); break;
                        case ElementId id: p.Set(id); break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // параметр не принимает прежнее значение у новой отметки (зависит от типа) — остаётся по типу
                }
            }
            if (_doc.GetElement(s.Id) != null) _doc.Delete(s.Id);
            _report.Spots++;
        }

        // ── размеры: сломанные — заново ──

        private void Dimension(View view, DimensionState s)
        {
            var dim = _doc.GetElement(s.Id) as Dimension;
            var name = "размер " + RevitCompat.Value(s.Id);
            string why = null;
            if (dim == null) why = "пропал";
            else if (dim.References.Size < s.References.Count) why = "пропали сегменты";
            else if (HasZero(dim)) why = "нулевые сегменты";
            if (why == null)
            {
                _report.Dimensions++;
                return;
            }
            if (s.Direction == null)
            {
                _report.Add(view, s.Id, name, why + " — не восстановлен: размер не прямой");
                return;
            }
            var refs = new ReferenceArray();
            var lost = 0;
            foreach (var r in s.References)
            {
                try
                {
                    refs.Append(Reference.ParseFromStableRepresentation(_doc, r));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    lost++;
                }
            }
            if (refs.Size < 2)
            {
                _report.Add(view, s.Id, name, why + " — не восстановлен: элементов, к которым он был привязан, почти не осталось");
                return;
            }
            var origin = Map(s.Origin);
            var direction = MapVector(s.Direction).Normalize();
            var line = Line.CreateBound(origin - direction, origin + direction);
            var type = _doc.GetElement(s.TypeId) as DimensionType;
            var created = Create(view, line, refs, type);
            if (created != null && HasZero(created))
            {
                // нули — убрать ссылки, совпавшие с предыдущей (ссылки нового размера идут по порядку вдоль линии)
                var values = Segments(created);
                var keep = ZeroSegments.Keep(values, Eps);
                var filtered = new ReferenceArray();
                var all = created.References;
                foreach (var i in keep) filtered.Append(all.get_Item(i));
                _doc.Delete(created.Id);
                created = filtered.Size >= 2 ? Create(view, line, filtered, type) : null;
            }
            if (created == null)
            {
                _report.Add(view, s.Id, name, why + " — Revit не построил размер заново, оставлен как есть");
                return;
            }
            if (dim != null && _doc.GetElement(dim.Id) != null) _doc.Delete(dim.Id);
            CopyTexts(created, s);
            _report.DimensionsRebuilt++;
            if (lost > 0)
                _report.Add(view, created.Id, "размер " + RevitCompat.Value(created.Id),
                            "построен заново без " + lost + " ссылок: элементов больше нет");
        }

        private Dimension Create(View view, Line line, ReferenceArray refs, DimensionType type) =>
            type != null ? _doc.Create.NewDimension(view, line, refs, type) : _doc.Create.NewDimension(view, line, refs);

        private static List<double> Segments(Dimension dim)
        {
            var values = new List<double>();
            if (dim.NumberOfSegments == 0) values.Add(dim.Value ?? 1);
            else foreach (DimensionSegment seg in dim.Segments) values.Add(seg.Value ?? 1);
            return values;
        }

        private static bool HasZero(Dimension dim) => ZeroSegments.Any(Segments(dim), Eps);

        private static void CopyTexts(Dimension dim, DimensionState s)
        {
            if (dim.NumberOfSegments == 0)
            {
                if (s.Texts.Count == 1) Apply(s.Texts[0], v => dim.ValueOverride = v, v => dim.Prefix = v, v => dim.Suffix = v, v => dim.Above = v, v => dim.Below = v);
                return;
            }
            if (dim.NumberOfSegments != s.Texts.Count) return;
            for (var i = 0; i < s.Texts.Count; i++)
            {
                var seg = dim.Segments.get_Item(i);
                Apply(s.Texts[i], v => seg.ValueOverride = v, v => seg.Prefix = v, v => seg.Suffix = v, v => seg.Above = v, v => seg.Below = v);
            }
        }

        private static void Apply(SegmentText t, params Action<string>[] setters)
        {
            var values = new[] { t.Override, t.Prefix, t.Suffix, t.Above, t.Below };
            for (var i = 0; i < setters.Length; i++)
            {
                if (string.IsNullOrEmpty(values[i])) continue;
                try
                {
                    setters[i](values[i]);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // текст сегмента не принимается (например, переопределение у нового размера другого вида) — без него
                }
            }
        }

        // ── остальное — проверка ──

        private void Other(View view, ViewState state, OtherState s)
        {
            var e = _doc.GetElement(s.Id);
            if (e == null)
            {
                _report.Add(view, null, s.What, "пропал после отражения");
                return;
            }
            var box = e.get_BoundingBox(view);
            if (box == null) return;
            var center = (box.Min + box.Max) / 2;
            var expected = Map(s.Center);
            // допуск — 1 мм на листе
            var tolerance = 0.001 * state.Scale / 0.3048;
            var off = center.DistanceTo(expected);
            if (off > tolerance)
                _report.Add(view, s.Id, s.What, "не на месте: сдвиг " + (off * 0.3048 * 1000 / state.Scale).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) +
                                                 " мм на листе — плагин такое пока не чинит");
            else _report.Others++;
        }

        // ── имена осей ──

        private void RenameGrids()
        {
            var grids = new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => !g.IsCurved).ToList();
            var infos = new List<GridInfo>();
            foreach (var g in grids)
            {
                var c = g.Curve;
                var dir = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                var mid = c.Evaluate(0.5, true);
                infos.Add(new GridInfo { Id = RevitCompat.Value(g.Id), Name = g.Name, X = mid.X, Y = mid.Y, Vertical = Math.Abs(dir.Y) > Math.Abs(dir.X) });
            }
            var plan = GridNaming.Make(infos);
            _report.Notes.AddRange(plan.Problems);
            foreach (var p in plan.Problems) _report.Add(null, null, "оси", p);
            if (plan.Renames.Count == 0) return;
            var byId = grids.ToDictionary(g => RevitCompat.Value(g.Id));
            var errors = new List<string>();
            using (var t = new Transaction(_doc, "RAS: имена осей"))
            {
                var options = t.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new QuietWarnings(errors));
                t.SetFailureHandlingOptions(options);
                t.Start();
                try
                {
                    // имена осей в модели не повторяются — сначала временные
                    var n = 0;
                    var stamp = Guid.NewGuid().ToString("N").Substring(0, 6);
                    foreach (var id in plan.Renames.Keys) byId[id].Name = "~" + stamp + "_" + n++;
                    foreach (var kv in plan.Renames) byId[kv.Key].Name = kv.Value;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    t.RollBack();
                    _report.Add(null, null, "оси", "оси не переименованы — " + ex.Message);
                    return;
                }
                if (t.Commit() != TransactionStatus.Committed)
                {
                    _report.Add(null, null, "оси", "оси не переименованы: Revit не принял" + (errors.Count > 0 ? " — " + errors[0] : ""));
                    return;
                }
            }
            _report.Renamed = plan.Renames.Count;
        }
    }

    /// <summary>Предупреждения Revit при восстановлении — скрыть; ошибки — откатить операцию вида, текст — в итог.</summary>
    internal sealed class QuietWarnings : IFailuresPreprocessor
    {
        private readonly List<string> _errors;

        public QuietWarnings(List<string> errors) => _errors = errors;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var rollback = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else
                {
                    _errors.Add(f.GetDescriptionText());
                    rollback = true;
                }
            }
            return rollback ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
