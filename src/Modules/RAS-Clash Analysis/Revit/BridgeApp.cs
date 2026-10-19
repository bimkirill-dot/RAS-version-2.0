using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Showing;
using RASv2.Core;

namespace RASv2.ClashAnalysis
{
    /// <summary>
    /// Работа модуля: мост (просьбы сайта), очередь в Revit, личный вид и покраска. События Revit — через ядро
    /// (PRINCIPLES.md, раздел 4).
    /// </summary>
    internal sealed class BridgeApp
    {
        /// <summary>
        /// Свой сайт — откуда принимаются просьбы. Пока сайт только на компьютере разработчика (19.10.2026: «пока пусть локально»);
        /// перед выходом на коллег — в настройки модуля (PRINCIPLES.md, раздел 4: адреса — не в коде модуля).
        /// </summary>
        private const string SiteOrigin = "http://localhost:5080";
        private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(2);

        private ModuleLog _log;
        private int _year;
        private BridgeServer _server;
        private ShowQueue _queue;
        private ClashViews _views;
        private ClashPaint _paint;
        private volatile byte[] _status;
        private DateTime _statusAt;

        internal void Start(IModuleContext context)
        {
            _log = context.Log;
            _year = context.RevitYear;
            var window = IntPtr.Zero;
            try { window = context.Application.MainWindowHandle; }
            catch (Exception) { /* окна ещё нет — Revit вперёд выходить не будет, остальное работает */ }
            _paint = new ClashPaint();
            _views = new ClashViews(Environment.UserName, _paint, _log);
            _queue = new ShowQueue((app, request) => ClashShow.Run(app, request, _views, _paint), window, _log);
            _status = BridgeProtocol.Write(new StatusReply { Revit = _year });

            var events = context.Events;
            events.Idling(OnIdling);
            events.DocumentOpened((s, e) => _views.Opened(e.Document));
            events.DocumentClosing((s, e) => _views.Closing(e.Document));

            _server = BridgeServer.Start(SiteOrigin, () => _status, _queue.Ask, _log);
            if (_server != null) _log.Info("Мост запущен: 127.0.0.1:" + _server.Port + ", сайт — " + SiteOrigin);
        }

        internal void Stop() => _server?.Dispose();

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication app)) return;
            _queue.EnsureEvent();
            _paint.Register();
            _views.OnIdling();
            if (DateTime.UtcNow - _statusAt < StatusEvery) return;
            _statusAt = DateTime.UtcNow;
            _status = BridgeProtocol.Write(Status(app));
        }

        /// <summary>Открытые модели и их связи — для индикатора на сайте (мост отвечает из своего потока, без Revit API).</summary>
        private StatusReply Status(UIApplication app)
        {
            var reply = new StatusReply { Revit = _year };
            var active = app.ActiveUIDocument?.Document;
            foreach (Document doc in app.Application.Documents)
            {
                if (doc.IsLinked || doc.IsFamilyDocument) continue;
                var item = new OpenDocument { Title = doc.Title, Active = active != null && doc.Equals(active) };
                item.Links.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .Select(t => Logic.ModelNames.Bare(t.Name)));
                reply.Documents.Add(item);
            }
            return reply;
        }
    }
}
