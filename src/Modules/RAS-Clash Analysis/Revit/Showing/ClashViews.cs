using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// Личные виды коллизии — свои у каждого человека, помечены названием («RAS-Clash Analysis — коллизия (логин)», план и разрез —
    /// «…, план (логин)», «…, разрез (логин)»), рабочие виды не трогаются (PRINCIPLES.md, раздел 6). На экране — один: открыли
    /// коллизию в другом виде — вкладка прежнего закрывается. 3D-вид перестраивается для следующей коллизии; план и разрез
    /// создаются заново (уровень плана и направление разреза у вида не меняются), прежний — переименовывается и закрывается.
    /// Закрыли вкладку — вид удаляется из модели; оставшийся (Revit закрыли с открытым видом, сбой) — при следующем открытии
    /// модели. Поведение — как у вида изменений RAS-Model History, код — свой (разработчик, 19.10.2026). Только в контексте Revit API.
    /// </summary>
    internal sealed class ClashViews
    {
        public const string TransactionPrefix = "RAS-Clash Analysis: ";
        private const string NamePrefix = "RAS-Clash Analysis — коллизия";

        private sealed class Tracked
        {
            public Document Document;
            public ElementId View;
            /// <summary>Вкладку вида видели открытой (или закрыли сами) — «нет среди открытых» значит «закрыли»: удалить.</summary>
            public bool SeenOpen;
            /// <summary>Свои элементы модели, перекрашенные в виде, — снять покраску при следующей коллизии.</summary>
            public List<ElementId> Painted = new List<ElementId>();
        }

        private readonly string _user;
        private readonly ClashPaint _paint;
        private readonly ModuleLog _log;
        private readonly List<Tracked> _views = new List<Tracked>();
        private readonly List<Document> _opened = new List<Document>();

        public ClashViews(string user, ClashPaint paint, ModuleLog log)
        {
            _user = new string((user ?? "").Select(ch => "\\:{}[]|;<>?`~".IndexOf(ch) >= 0 ? '_' : ch).ToArray()).Trim();
            if (_user.Length == 0) _user = "пользователь";
            _paint = paint;
            _log = log;
        }

        private string Name(string kind) => NamePrefix + kind + " (" + _user + ")";
        private bool IsOurs(View v) => !v.IsTemplate && v.Name.StartsWith(NamePrefix, StringComparison.Ordinal) && v.Name.EndsWith("(" + _user + ")", StringComparison.Ordinal);

        private Tracked Find(Document doc, ElementId view) =>
            _views.FirstOrDefault(t => t.View == view && t.Document.IsValidObject && t.Document.Equals(doc));

        private Tracked Track(Document doc, View view)
        {
            var t = Find(doc, view.Id);
            if (t == null) _views.Add(t = new Tracked { Document = doc, View = view.Id });
            return t;
        }

        /// <summary>3D-вид (внутри транзакции): уже открытый — перестраивается, иначе — новый.</summary>
        public View3D Prepare3D(Document doc)
        {
            var name = Name("");
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (view == null)
            {
                view = View3D.CreateIsometric(doc, TypeOf(doc, ViewFamily.ThreeDimensional));
                view.Name = name;
            }
            Track(doc, view);
            return view;
        }

        /// <summary>Новый план на уровне (внутри транзакции); прежний план коллизии — в сторону.</summary>
        public ViewPlan NewPlan(Document doc, ElementId level)
        {
            var name = Name(", план");
            Retire(doc, name);
            var view = ViewPlan.Create(doc, TypeOf(doc, ViewFamily.FloorPlan), level);
            view.Name = name;
            Track(doc, view);
            return view;
        }

        /// <summary>Новый разрез по рамке (внутри транзакции); прежний разрез коллизии — в сторону.</summary>
        public ViewSection NewSection(Document doc, BoundingBoxXYZ box)
        {
            var name = Name(", разрез");
            Retire(doc, name);
            var view = ViewSection.CreateSection(doc, TypeOf(doc, ViewFamily.Section), box);
            view.Name = name;
            Track(doc, view);
            return view;
        }

        private static ElementId TypeOf(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == family).Id;

        /// <summary>Вид с этим названием — под другое название (его вкладку закроет CloseOthers, тогда он и удалится).</summary>
        private void Retire(Document doc, string name)
        {
            var old = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (old == null) return;
            old.Name = NamePrefix + ", прежний " + RevitCompat.Value(old.Id) + " (" + _user + ")";
            Track(doc, old).SeenOpen = true;
        }

        /// <summary>После показа: вкладки других своих видов этой модели — закрыть (виды удалятся в простой).</summary>
        public void CloseOthers(UIDocument uidoc, ElementId keep)
        {
            var doc = uidoc.Document;
            var open = uidoc.GetOpenUIViews();
            foreach (var t in _views.Where(t => t.View != keep && t.Document.IsValidObject && t.Document.Equals(doc)))
            {
                t.SeenOpen = true;
                var ui = open.FirstOrDefault(v => v.ViewId == t.View);
                try { ui?.Close(); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* последняя открытая вкладка — останется, удалится, когда закроют */ }
            }
        }

        public List<ElementId> Painted(Document doc, ElementId view) => Find(doc, view)?.Painted ?? new List<ElementId>();

        public void SetPainted(Document doc, ElementId view, List<ElementId> ids)
        {
            var t = Find(doc, view);
            if (t != null) t.Painted = ids;
        }

        /// <summary>Модель открыта: оставшиеся виды — убрать в ближайший простой.</summary>
        public void Opened(Document doc)
        {
            if (doc != null && !doc.IsLinked && !doc.IsFamilyDocument) _opened.Add(doc);
        }

        /// <summary>Модель закрывается: менять её уже нельзя — виды уберутся при следующем открытии.</summary>
        public void Closing(Document doc)
        {
            _views.RemoveAll(t => !t.Document.IsValidObject || t.Document.Equals(doc));
            _opened.RemoveAll(d => !d.IsValidObject || d.Equals(doc));
            _paint.Clear(doc);
        }

        /// <summary>В простой: закрытые вкладки видов — удалить виды; в только что открытых моделях — оставшиеся.</summary>
        public void OnIdling()
        {
            var gone = new List<Tracked>();
            foreach (var t in _views)
            {
                if (!t.Document.IsValidObject || t.Document.GetElement(t.View) == null) { gone.Add(t); continue; }
                if (IsOpen(t.Document, t.View)) { t.SeenOpen = true; continue; }
                if (t.SeenOpen && CanChange(t.Document)) gone.Add(t);
            }
            // по моделям (Document сравнивается через Equals — обёртки одной модели могут быть разными объектами)
            var docs = new List<Document>();
            foreach (var t in gone.Where(t => t.Document.IsValidObject))
                if (!docs.Any(d => d.Equals(t.Document))) docs.Add(t.Document);
            foreach (var doc in docs)
            {
                var mine = gone.Where(t => t.Document.IsValidObject && t.Document.Equals(doc)).ToList();
                var ids = mine.Where(t => doc.GetElement(t.View) != null).Select(t => t.View).ToList();
                // не удалился — больше не пытаемся (иначе ошибка в каждый простой): уберётся при следующем открытии модели
                if (ids.Count > 0) Delete(doc, ids);
                foreach (var t in mine) _paint.Clear(doc, t.View);
            }
            _views.RemoveAll(gone.Contains);

            if (_opened.Count == 0) return;
            var opened = _opened.ToList();
            _opened.Clear();
            foreach (var doc in opened.Where(d => d.IsValidObject))
            {
                var leftovers = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(IsOurs).Select(v => v.Id).ToList();
                if (leftovers.Count == 0) continue;
                // модель открылась с этим видом на экране — он снова наш: уберётся, когда закроют вкладку
                var open = leftovers.Where(id => IsOpen(doc, id)).ToList();
                foreach (var id in open) _views.Add(new Tracked { Document = doc, View = id, SeenOpen = true });
                var closed = leftovers.Except(open).ToList();
                if (closed.Count > 0 && CanChange(doc)) Delete(doc, closed);
            }
        }

        private static bool IsOpen(Document doc, ElementId view)
        {
            try { return new UIDocument(doc).GetOpenUIViews().Any(v => v.ViewId == view); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        private static bool CanChange(Document doc) => !doc.IsReadOnly && !doc.IsModifiable;

        private void Delete(Document doc, IList<ElementId> views)
        {
            try
            {
                using var t = new Transaction(doc, TransactionPrefix + "убрать вид коллизии");
                t.Start();
                doc.Delete(views);
                t.Commit();
            }
            catch (Exception ex)
            {
                // например, вид синхронизирован и занят у коллеги — останется до следующего раза, работе не мешает
                _log.Error("Вид коллизии не удалён из «" + doc.Title + "»", ex);
            }
        }
    }
}
