using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// Покраска элементов коллизии поверх модели — для элементов связей: Revit API перекрашивает только элементы самой модели
    /// (DECISIONS.md, «Мост в Revit…», «Покраска элемента связи — проверить первым в прототипе»). Форма элемента (у связи —
    /// с её положением) рисуется в нашем личном виде цветом Navisworks. Это временное изображение (DirectContext3D): его нет
    /// в модели, оно не сохраняется и не синхронизируется. Рисуется чуть больше элемента (Offset) — иначе поверхности
    /// совпадают и мерцают.
    /// </summary>
    internal sealed class ClashPaint : IDirectContext3DServer
    {
        private static readonly Guid ServerId = new Guid("8E3B6C2D-4F1A-4B7E-9C55-2A71D0E6B913");
        /// <summary>2 мм наружу.</summary>
        private const double Offset = 0.002 / 0.3048;
        /// <summary>Индексы в буфере — 16-битные: не больше 65535 вершин за раз.</summary>
        private const int MaxVertices = 60000;

        private sealed class Part
        {
            public readonly List<XYZ> Triangles = new List<XYZ>();   // по три точки
            public readonly List<XYZ> Normals = new List<XYZ>();     // по одной на треугольник
            public readonly List<XYZ> Lines = new List<XYZ>();       // по две точки — рёбра
            public Color Fill, Edge;
        }

        private sealed class Batch : IDisposable
        {
            public VertexBuffer Vertices;
            public IndexBuffer Indices;
            public int VertexCount, IndexCount, PrimitiveCount;
            public PrimitiveType Primitive;
            public VertexFormat Format;
            public EffectInstance Effect;

            public void Dispose()
            {
                Vertices?.Dispose();
                Indices?.Dispose();
                Format?.Dispose();
                Effect?.Dispose();
            }
        }

        private readonly object _lock = new object();
        private Document _document;
        private ElementId _view = ElementId.InvalidElementId;
        private List<Part> _parts = new List<Part>();
        private Outline _bounds;
        private List<Batch> _batches;
        private bool _registered;

        /// <summary>Подключиться к Revit как источник изображения — один раз, в контексте Revit API.</summary>
        public void Register()
        {
            if (_registered) return;
            _registered = true;
            var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            if (service == null) return;
            service.AddServer(this);
            var active = service.GetActiveServerIds();
            if (!active.Contains(ServerId)) active.Add(ServerId);
            service.SetActiveServers(active);
        }

        /// <summary>Что рисовать: элементы (у связи — с её положением) и их цвета; в виде view документа document.</summary>
        public void Show(Document document, ElementId view, IEnumerable<(Element Element, Transform Transform, Color Color)> items)
        {
            var parts = new List<Part>();
            XYZ min = null, max = null;
            var options = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            foreach (var (element, transform, color) in items)
            {
                var part = new Part { Fill = color, Edge = Darker(color) };
                Collect(element.get_Geometry(options), transform ?? Transform.Identity, part);
                if (part.Triangles.Count == 0 && part.Lines.Count == 0) continue;
                foreach (var p in part.Triangles) Grow(ref min, ref max, p);
                foreach (var p in part.Lines) Grow(ref min, ref max, p);
                parts.Add(part);
            }
            lock (_lock)
            {
                _document = document;
                _view = view;
                _parts = parts;
                _bounds = min == null ? null : new Outline(min, max);
                DisposeBatches();
            }
        }

        /// <summary>Убрать изображение: вид удалён, модель закрывается.</summary>
        public void Clear(Document document = null)
        {
            lock (_lock)
            {
                if (document != null && (_document == null || !_document.IsValidObject || !_document.Equals(document))) return;
                _document = null;
                _view = ElementId.InvalidElementId;
                _parts = new List<Part>();
                _bounds = null;
                DisposeBatches();
            }
        }

        private void DisposeBatches()
        {
            if (_batches != null) foreach (var b in _batches) b.Dispose();
            _batches = null;
        }

        private static void Collect(GeometryElement geometry, Transform transform, Part part)
        {
            if (geometry == null) return;
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces) AddFace(face, transform, part);
                        foreach (Edge edge in solid.Edges) AddPolyline(edge.Tessellate(), transform, part);
                        break;
                    case Mesh mesh:
                        AddMesh(mesh, transform, part);
                        break;
                    case GeometryInstance instance:
                        // уже в координатах модели элемента (у связи — модели связи), дальше — положение связи
                        Collect(instance.GetInstanceGeometry(), transform, part);
                        break;
                }
            }
        }

        private static void AddFace(Face face, Transform transform, Part part)
        {
            Mesh mesh;
            try { mesh = face.Triangulate(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return; } // грань, которую Revit не разбивает, — без неё
            if (mesh == null) return;
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var t = mesh.get_Triangle(i);
                XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);
                var normal = Cross(a, b, c);
                // наружу — по нормали грани (у тела она смотрит из тела), иначе — по обходу треугольника
                var projected = face.Project((a + b + c) / 3);
                if (projected != null)
                {
                    var outward = face.ComputeNormal(projected.UVPoint);
                    if (normal.DotProduct(outward) < 0) { var s = b; b = c; c = s; }
                    normal = outward;
                }
                Add(part, transform, a, b, c, normal);
            }
        }

        private static void AddMesh(Mesh mesh, Transform transform, Part part)
        {
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var t = mesh.get_Triangle(i);
                XYZ a = t.get_Vertex(0), b = t.get_Vertex(1), c = t.get_Vertex(2);
                Add(part, transform, a, b, c, Cross(a, b, c));
            }
        }

        private static void Add(Part part, Transform transform, XYZ a, XYZ b, XYZ c, XYZ normal)
        {
            if (normal.IsZeroLength()) return;
            normal = normal.Normalize();
            var shift = normal * Offset;
            part.Triangles.Add(transform.OfPoint(a + shift));
            part.Triangles.Add(transform.OfPoint(b + shift));
            part.Triangles.Add(transform.OfPoint(c + shift));
            part.Normals.Add(transform.OfVector(normal));
        }

        private static void AddPolyline(IList<XYZ> points, Transform transform, Part part)
        {
            for (var i = 1; i < points.Count; i++)
            {
                part.Lines.Add(transform.OfPoint(points[i - 1]));
                part.Lines.Add(transform.OfPoint(points[i]));
            }
        }

        private static XYZ Cross(XYZ a, XYZ b, XYZ c) => (b - a).CrossProduct(c - a);

        private static void Grow(ref XYZ min, ref XYZ max, XYZ p)
        {
            min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        private static Color Darker(Color c) => new Color((byte)(c.Red * 0.55), (byte)(c.Green * 0.55), (byte)(c.Blue * 0.55));

        // ───── буферы для Revit: строятся при первом рисовании ─────

        private List<Batch> Build()
        {
            var batches = new List<Batch>();
            foreach (var part in _parts)
            {
                var fill = new ColorWithTransparency(part.Fill.Red, part.Fill.Green, part.Fill.Blue, 0);
                var triangles = part.Normals.Count;
                for (var start = 0; start < triangles; start += MaxVertices / 3)
                    batches.Add(Faces(part, start, Math.Min(MaxVertices / 3, triangles - start), fill));
                var edge = new ColorWithTransparency(part.Edge.Red, part.Edge.Green, part.Edge.Blue, 0);
                var lines = part.Lines.Count / 2;
                for (var start = 0; start < lines; start += MaxVertices / 2)
                    batches.Add(Edges(part, start, Math.Min(MaxVertices / 2, lines - start), edge));
            }
            return batches;
        }

        private static Batch Faces(Part part, int start, int count, ColorWithTransparency color)
        {
            var vertices = count * 3;
            var vb = new VertexBuffer(vertices * VertexPositionNormalColored.GetSizeInFloats());
            vb.Map(vertices * VertexPositionNormalColored.GetSizeInFloats());
            var vs = vb.GetVertexStreamPositionNormalColored();
            for (var i = start; i < start + count; i++)
            {
                var n = part.Normals[i];
                for (var k = 0; k < 3; k++) vs.AddVertex(new VertexPositionNormalColored(part.Triangles[i * 3 + k], n, color));
            }
            vb.Unmap();
            var ib = new IndexBuffer(count * IndexTriangle.GetSizeInShortInts());
            ib.Map(count * IndexTriangle.GetSizeInShortInts());
            var stream = ib.GetIndexStreamTriangle();
            for (var i = 0; i < count; i++) stream.AddTriangle(new IndexTriangle(i * 3, i * 3 + 1, i * 3 + 2));
            ib.Unmap();
            return new Batch
            {
                Vertices = vb, Indices = ib, VertexCount = vertices, IndexCount = count * 3, PrimitiveCount = count,
                Primitive = PrimitiveType.TriangleList,
                Format = new VertexFormat(VertexFormatBits.PositionNormalColored),
                Effect = new EffectInstance(VertexFormatBits.PositionNormalColored),
            };
        }

        private static Batch Edges(Part part, int start, int count, ColorWithTransparency color)
        {
            var vertices = count * 2;
            var vb = new VertexBuffer(vertices * VertexPositionColored.GetSizeInFloats());
            vb.Map(vertices * VertexPositionColored.GetSizeInFloats());
            var vs = vb.GetVertexStreamPositionColored();
            for (var i = start * 2; i < (start + count) * 2; i++) vs.AddVertex(new VertexPositionColored(part.Lines[i], color));
            vb.Unmap();
            var ib = new IndexBuffer(count * IndexLine.GetSizeInShortInts());
            ib.Map(count * IndexLine.GetSizeInShortInts());
            var stream = ib.GetIndexStreamLine();
            for (var i = 0; i < count; i++) stream.AddLine(new IndexLine(i * 2, i * 2 + 1));
            ib.Unmap();
            return new Batch
            {
                Vertices = vb, Indices = ib, VertexCount = vertices, IndexCount = count * 2, PrimitiveCount = count,
                Primitive = PrimitiveType.LineList,
                Format = new VertexFormat(VertexFormatBits.PositionColored),
                Effect = new EffectInstance(VertexFormatBits.PositionColored),
            };
        }

        // ───── IDirectContext3DServer ─────

        public Guid GetServerId() => ServerId;
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "RAS-Clash Analysis — покраска коллизии";
        public string GetVendorId() => "RAS";
        public string GetDescription() => "Элементы коллизии цветами Navisworks в личном виде коллизии";
        public string GetApplicationId() => "";
        public string GetSourceId() => "";
        public bool UsesHandles() => false;
        public bool UseInTransparentPass(View view) => false;

        public bool CanExecute(View view)
        {
            lock (_lock)
                return _parts.Count > 0 && _document != null && _document.IsValidObject && view.Id == _view && view.Document.Equals(_document);
        }

        public Outline GetBoundingBox(View view)
        {
            lock (_lock) return _bounds;
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            lock (_lock)
            {
                if (_parts.Count == 0) return;
                if (_batches == null) _batches = Build();
                foreach (var b in _batches)
                {
                    if (DrawContext.IsInterrupted()) return;
                    DrawContext.FlushBuffer(b.Vertices, b.VertexCount, b.Indices, b.IndexCount, b.Format, b.Effect, b.Primitive, 0, b.PrimitiveCount);
                }
            }
        }
    }
}
