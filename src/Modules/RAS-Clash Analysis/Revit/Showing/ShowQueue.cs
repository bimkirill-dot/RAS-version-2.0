using System;
using System.Threading;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Bridge;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// Просьба сайта «показать» приходит в потоке моста, а Revit API можно вызывать только в контексте Revit — через внешнее
    /// событие Revit (PRINCIPLES.md, раздел 6). Выполняется последняя просьба: нажали следующую коллизию, пока Revit был занят, —
    /// прежняя уже не нужна. Ответ ждётся недолго: Revit занят (открыто окно, идёт команда) — сайту «откроется, когда освободится».
    /// </summary>
    internal sealed class ShowQueue : IExternalEventHandler
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

        private sealed class Pending
        {
            public ShowRequest Request;
            public ShowReply Reply;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();

            public void Finish(ShowReply reply)
            {
                Reply = reply;
                Done.Set();
            }
        }

        private readonly object _lock = new object();
        private readonly Func<UIApplication, ShowRequest, ShowReply> _run;
        private readonly IntPtr _window;
        private readonly ModuleLog _log;
        private Pending _pending;
        private ExternalEvent _event;

        public ShowQueue(Func<UIApplication, ShowRequest, ShowReply> run, IntPtr window, ModuleLog log)
        {
            _run = run;
            _window = window;
            _log = log;
        }

        /// <summary>Внешнее событие создаётся в контексте Revit — в первый простой после запуска.</summary>
        public void EnsureEvent()
        {
            if (_event == null) _event = ExternalEvent.Create(this);
        }

        /// <summary>Из потока моста: отдать просьбу Revit и дождаться ответа.</summary>
        public ShowReply Ask(ShowRequest request)
        {
            var ev = _event;
            if (ev == null) return ShowReply.Fail("Revit ещё запускается — нажмите ещё раз через несколько секунд.");
            var pending = new Pending { Request = request };
            Pending replaced;
            lock (_lock)
            {
                replaced = _pending;
                _pending = pending;
            }
            // прежняя просьба не выполнена и уже не нужна: открывается следующая коллизия — сайту молча «готово»
            replaced?.Finish(new ShowReply { Ok = true });
            RevitWindow.Activate(_window);
            ev.Raise();
            return pending.Done.Wait(Wait) ? pending.Reply : new ShowReply { Ok = true, Pending = true };
        }

        public void Execute(UIApplication app)
        {
            Pending pending;
            lock (_lock)
            {
                pending = _pending;
                _pending = null;
            }
            if (pending == null) return;
            ShowReply reply;
            try
            {
                reply = _run(app, pending.Request);
            }
            catch (Exception ex)
            {
                _log.Error("Показать в Revit: " + pending.Request?.Clash, ex);
                reply = ShowReply.Fail("Не удалось показать коллизию — подробности в журнале плагина («О плагине» → «Журнал»).");
            }
            pending.Finish(reply);
        }

        public string GetName() => "RAS-Clash Analysis: показать коллизию";
    }
}
