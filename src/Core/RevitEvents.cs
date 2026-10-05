using System;
using System.Collections.Generic;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RASv2.Core
{
    /// <summary>
    /// События Revit для модуля. Подписка — только так, не напрямую (PRINCIPLES.md, раздел 4):
    /// ядро подписывается на событие один раз и раздаёт его модулям; ошибка обработчика модуля пишется
    /// в журнал с меткой модуля и не мешает другим модулям.
    /// </summary>
    public interface IRevitEvents
    {
        void DocumentChanged(EventHandler<DocumentChangedEventArgs> handler);
        void DocumentOpened(EventHandler<DocumentOpenedEventArgs> handler);
        void DocumentClosing(EventHandler<DocumentClosingEventArgs> handler);
        void DocumentClosed(EventHandler<DocumentClosedEventArgs> handler);
        void DocumentSaved(EventHandler<DocumentSavedEventArgs> handler);
        void DocumentSavedAs(EventHandler<DocumentSavedAsEventArgs> handler);
        void DocumentSynchronizingWithCentral(EventHandler<DocumentSynchronizingWithCentralEventArgs> handler);
        void DocumentSynchronizedWithCentral(EventHandler<DocumentSynchronizedWithCentralEventArgs> handler);
        void DocumentReloadingLatest(EventHandler<DocumentReloadingLatestEventArgs> handler);
        void DocumentReloadedLatest(EventHandler<DocumentReloadedLatestEventArgs> handler);
        void ViewActivated(EventHandler<ViewActivatedEventArgs> handler);
        void SelectionChanged(EventHandler<SelectionChangedEventArgs> handler);
        void Idling(EventHandler<IdlingEventArgs> handler);
#if !REVIT2023
        /// <summary>Тема Revit (светлая / тёмная) — с Revit 2024.</summary>
        void ThemeChanged(EventHandler<ThemeChangedEventArgs> handler);
#endif
    }

    /// <summary>Одно событие Revit: одна подписка ядра, список обработчиков модулей.</summary>
    internal sealed class EventChannel<TArgs> where TArgs : EventArgs
    {
        private readonly string _name;
        private readonly Action<EventHandler<TArgs>> _attach;
        private readonly Action<EventHandler<TArgs>> _detach;
        private readonly List<KeyValuePair<string, EventHandler<TArgs>>> _handlers = new List<KeyValuePair<string, EventHandler<TArgs>>>();
        private bool _attached;

        public EventChannel(string name, Action<EventHandler<TArgs>> attach, Action<EventHandler<TArgs>> detach)
        {
            _name = name;
            _attach = attach;
            _detach = detach;
        }

        public void Add(string tag, EventHandler<TArgs> handler)
        {
            _handlers.Add(new KeyValuePair<string, EventHandler<TArgs>>(tag, handler));
            if (_attached) return;
            _attach(Raise);
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            try { _detach(Raise); }
            catch (Exception ex) { Log.Write("Ядро", "Не удалось отписаться от " + _name, ex); }
            _attached = false;
        }

        private void Raise(object sender, TArgs e)
        {
            foreach (var h in _handlers.ToArray())
            {
                try { h.Value(sender, e); }
                catch (Exception ex) { Log.Write(h.Key, "Ошибка в обработчике события " + _name, ex); }
            }
        }
    }

    /// <summary>Все события Revit, которые ядро раздаёт модулям.</summary>
    internal sealed class RevitEventHub
    {
        public readonly EventChannel<DocumentChangedEventArgs> DocumentChanged;
        public readonly EventChannel<DocumentOpenedEventArgs> DocumentOpened;
        public readonly EventChannel<DocumentClosingEventArgs> DocumentClosing;
        public readonly EventChannel<DocumentClosedEventArgs> DocumentClosed;
        public readonly EventChannel<DocumentSavedEventArgs> DocumentSaved;
        public readonly EventChannel<DocumentSavedAsEventArgs> DocumentSavedAs;
        public readonly EventChannel<DocumentSynchronizingWithCentralEventArgs> Synchronizing;
        public readonly EventChannel<DocumentSynchronizedWithCentralEventArgs> Synchronized;
        public readonly EventChannel<DocumentReloadingLatestEventArgs> ReloadingLatest;
        public readonly EventChannel<DocumentReloadedLatestEventArgs> ReloadedLatest;
        public readonly EventChannel<ViewActivatedEventArgs> ViewActivated;
        public readonly EventChannel<SelectionChangedEventArgs> SelectionChanged;
        public readonly EventChannel<IdlingEventArgs> Idling;
#if !REVIT2023
        public readonly EventChannel<ThemeChangedEventArgs> ThemeChanged;
#endif

        public RevitEventHub(UIControlledApplication ui)
        {
            var app = ui.ControlledApplication;
            DocumentChanged = new EventChannel<DocumentChangedEventArgs>("DocumentChanged", h => app.DocumentChanged += h, h => app.DocumentChanged -= h);
            DocumentOpened = new EventChannel<DocumentOpenedEventArgs>("DocumentOpened", h => app.DocumentOpened += h, h => app.DocumentOpened -= h);
            DocumentClosing = new EventChannel<DocumentClosingEventArgs>("DocumentClosing", h => app.DocumentClosing += h, h => app.DocumentClosing -= h);
            DocumentClosed = new EventChannel<DocumentClosedEventArgs>("DocumentClosed", h => app.DocumentClosed += h, h => app.DocumentClosed -= h);
            DocumentSaved = new EventChannel<DocumentSavedEventArgs>("DocumentSaved", h => app.DocumentSaved += h, h => app.DocumentSaved -= h);
            DocumentSavedAs = new EventChannel<DocumentSavedAsEventArgs>("DocumentSavedAs", h => app.DocumentSavedAs += h, h => app.DocumentSavedAs -= h);
            Synchronizing = new EventChannel<DocumentSynchronizingWithCentralEventArgs>("DocumentSynchronizingWithCentral",
                h => app.DocumentSynchronizingWithCentral += h, h => app.DocumentSynchronizingWithCentral -= h);
            Synchronized = new EventChannel<DocumentSynchronizedWithCentralEventArgs>("DocumentSynchronizedWithCentral",
                h => app.DocumentSynchronizedWithCentral += h, h => app.DocumentSynchronizedWithCentral -= h);
            ReloadingLatest = new EventChannel<DocumentReloadingLatestEventArgs>("DocumentReloadingLatest",
                h => app.DocumentReloadingLatest += h, h => app.DocumentReloadingLatest -= h);
            ReloadedLatest = new EventChannel<DocumentReloadedLatestEventArgs>("DocumentReloadedLatest",
                h => app.DocumentReloadedLatest += h, h => app.DocumentReloadedLatest -= h);
            ViewActivated = new EventChannel<ViewActivatedEventArgs>("ViewActivated", h => ui.ViewActivated += h, h => ui.ViewActivated -= h);
            SelectionChanged = new EventChannel<SelectionChangedEventArgs>("SelectionChanged", h => ui.SelectionChanged += h, h => ui.SelectionChanged -= h);
            Idling = new EventChannel<IdlingEventArgs>("Idling", h => ui.Idling += h, h => ui.Idling -= h);
#if !REVIT2023
            ThemeChanged = new EventChannel<ThemeChangedEventArgs>("ThemeChanged", h => ui.ThemeChanged += h, h => ui.ThemeChanged -= h);
#endif
        }

        public void DetachAll()
        {
            DocumentChanged.Detach();
            DocumentOpened.Detach();
            DocumentClosing.Detach();
            DocumentClosed.Detach();
            DocumentSaved.Detach();
            DocumentSavedAs.Detach();
            Synchronizing.Detach();
            Synchronized.Detach();
            ReloadingLatest.Detach();
            ReloadedLatest.Detach();
            ViewActivated.Detach();
            SelectionChanged.Detach();
            Idling.Detach();
#if !REVIT2023
            ThemeChanged.Detach();
#endif
        }
    }

    /// <summary>События для одного модуля: обработчики помечаются меткой модуля.</summary>
    internal sealed class ModuleEvents : IRevitEvents
    {
        private readonly RevitEventHub _hub;
        private readonly string _tag;

        public ModuleEvents(RevitEventHub hub, string tag)
        {
            _hub = hub;
            _tag = tag;
        }

        public void DocumentChanged(EventHandler<DocumentChangedEventArgs> handler) => _hub.DocumentChanged.Add(_tag, handler);
        public void DocumentOpened(EventHandler<DocumentOpenedEventArgs> handler) => _hub.DocumentOpened.Add(_tag, handler);
        public void DocumentClosing(EventHandler<DocumentClosingEventArgs> handler) => _hub.DocumentClosing.Add(_tag, handler);
        public void DocumentClosed(EventHandler<DocumentClosedEventArgs> handler) => _hub.DocumentClosed.Add(_tag, handler);
        public void DocumentSaved(EventHandler<DocumentSavedEventArgs> handler) => _hub.DocumentSaved.Add(_tag, handler);
        public void DocumentSavedAs(EventHandler<DocumentSavedAsEventArgs> handler) => _hub.DocumentSavedAs.Add(_tag, handler);
        public void DocumentSynchronizingWithCentral(EventHandler<DocumentSynchronizingWithCentralEventArgs> handler) => _hub.Synchronizing.Add(_tag, handler);
        public void DocumentSynchronizedWithCentral(EventHandler<DocumentSynchronizedWithCentralEventArgs> handler) => _hub.Synchronized.Add(_tag, handler);
        public void DocumentReloadingLatest(EventHandler<DocumentReloadingLatestEventArgs> handler) => _hub.ReloadingLatest.Add(_tag, handler);
        public void DocumentReloadedLatest(EventHandler<DocumentReloadedLatestEventArgs> handler) => _hub.ReloadedLatest.Add(_tag, handler);
        public void ViewActivated(EventHandler<ViewActivatedEventArgs> handler) => _hub.ViewActivated.Add(_tag, handler);
        public void SelectionChanged(EventHandler<SelectionChangedEventArgs> handler) => _hub.SelectionChanged.Add(_tag, handler);
        public void Idling(EventHandler<IdlingEventArgs> handler) => _hub.Idling.Add(_tag, handler);
#if !REVIT2023
        public void ThemeChanged(EventHandler<ThemeChangedEventArgs> handler) => _hub.ThemeChanged.Add(_tag, handler);
#endif
    }
}
