using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// Личный вид коллизии — свой у каждого человека, помечен названием («RAS-Clash Analysis — коллизия (логин)»), рабочие виды
    /// не трогаются (PRINCIPLES.md, раздел 6). Следующая коллизия — тот же вид перестраивается. Закрыли вкладку вида — вид
    /// удаляется из модели; оставшийся (Revit закрыли с открытым видом, сбой) — при следующем открытии модели, если не открыт.
    /// Поведение — как у вида изменений RAS-Model History, код — свой (разработчик, 19.10.2026). Только в контексте Revit API.
    /// </summary>
    internal sealed class ClashViews
    {
        public const string TransactionPrefix = "RAS-Clash Analysis: ";
        private const string NamePrefix = "RAS-Clash Analysis — коллизия";

        private sealed class Tracked
        {
            public Document Document;
            public ElementId View;
            /// <summary>Вкладку вида видели открытой — значит, «нет среди открытых» — её закрыли.</summary>
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
            _user = user;
            _paint = paint;
            _log = log;
        }

        public string ViewName
        {
            get
            {
                var clean = new string((_user ?? "").Select(ch => "\\:{}[]|;<>?`~".IndexOf(ch) >= 0 ? '_' : ch).ToArray()).Trim();
                return NamePrefix + " (" + (clean.Length == 0 ? "пользователь" : clean) + ")";
            }
        }

        private Tracked Find(Document doc) => _views.FirstOrDefault(t => t.Document.IsValidObject && t.Document.Equals(doc));

        /// <summary>Вид этого человека в модели (внутри транзакции): уже открытый для коллизий, оставшийся — или новый.</summary>
        public View3D Prepare(Document doc)
        {
            var tracked = Find(doc);
            var view = tracked == null ? null : doc.GetElement(tracked.View) as View3D;
            view ??= new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName);
            if (view == null)
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, type.Id);
                view.Name = ViewName;
            }
            if (tracked == null) _views.Add(tracked = new Tracked { Document = doc });
            if (tracked.View != view.Id)
            {
                tracked.View = view.Id;
                tracked.SeenOpen = false;
            }
            return view;
        }

        public List<ElementId> Painted(Document doc) => Find(doc)?.Painted ?? new List<ElementId>();

        public void SetPainted(Document doc, List<ElementId> ids)
        {
            var tracked = Find(doc);
            if (tracked != null) tracked.Painted = ids;
        }

        /// <summary>Модель открыта: оставшийся вид — убрать в ближайший простой.</summary>
        public void Opened(Document doc)
        {
            if (doc != null && !doc.IsLinked && !doc.IsFamilyDocument) _opened.Add(doc);
        }

        /// <summary>Модель закрывается: менять её уже нельзя — вид уберётся при следующем открытии.</summary>
        public void Closing(Document doc)
        {
            _views.RemoveAll(t => !t.Document.IsValidObject || t.Document.Equals(doc));
            _opened.RemoveAll(d => !d.IsValidObject || d.Equals(doc));
            _paint.Clear(doc);
        }

        /// <summary>В простой: закрытые вкладки вида — удалить вид; в только что открытых моделях — оставшиеся.</summary>
        public void OnIdling()
        {
            for (var i = _views.Count - 1; i >= 0; i--)
            {
                var t = _views[i];
                if (!t.Document.IsValidObject) { _views.RemoveAt(i); continue; }
                if (t.Document.GetElement(t.View) == null) { _views.RemoveAt(i); _paint.Clear(t.Document); continue; }
                if (IsOpen(t.Document, t.View)) { t.SeenOpen = true; continue; }
                if (!t.SeenOpen || !CanChange(t.Document)) continue;
                // не удалился — больше не пытаемся (иначе ошибка в каждый простой): уберётся при следующем открытии модели
                Delete(t.Document, new[] { t.View });
                _views.RemoveAt(i);
                _paint.Clear(t.Document);
            }
            if (_opened.Count == 0) return;
            var opened = _opened.ToList();
            _opened.Clear();
            foreach (var doc in opened.Where(d => d.IsValidObject))
            {
                var leftovers = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .Where(v => !v.IsTemplate && v.Name == ViewName).Select(v => v.Id).ToList();
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

        private bool Delete(Document doc, IList<ElementId> views)
        {
            try
            {
                using var t = new Transaction(doc, TransactionPrefix + "убрать вид коллизии");
                t.Start();
                doc.Delete(views);
                t.Commit();
                return true;
            }
            catch (Exception ex)
            {
                // например, вид синхронизирован и занят у коллеги — останется до следующего раза, работе не мешает
                _log.Error("Вид коллизии не удалён из «" + doc.Title + "»", ex);
                return false;
            }
        }
    }
}
