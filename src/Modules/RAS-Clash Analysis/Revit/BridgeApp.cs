using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Showing;
using RASv2.ClashAnalysis.UI;
using RASv2.Core;

namespace RASv2.ClashAnalysis
{
    /// <summary>
    /// Работа модуля: мост (просьбы сайта), очередь в Revit, личный вид, «Настройки вида» на ленте. События Revit —
    /// через ядро (PRINCIPLES.md, раздел 4).
    /// </summary>
    internal sealed class BridgeApp
    {
        /// <summary>
        /// Свой сайт — адрес от ядра (RasPaths.SiteOf; PRINCIPLES.md, раздел 4: адреса — не в коде модуля); просьбы моста — только
        /// с него (и с этого компьютера), «Коллизии элемента» открывают его. Нет адреса — сайт на этом компьютере.
        /// </summary>
        private string _site = "http://localhost:5080";
        private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(2);

        public static BridgeApp Instance { get; private set; }

        private ModuleLog _log;
        private int _year;
        private string _settingsPath;
        private volatile ViewSettings _settings = new ViewSettings();
        private BridgeServer _server;
        private ShowQueue _queue;
        private ClashViews _views;
        private volatile byte[] _status;
        private DateTime _statusAt;

        internal void Start(IModuleContext context)
        {
            Instance = this;
            _log = context.Log;
            if (!string.IsNullOrEmpty(context.SiteAddress)) _site = context.SiteAddress;
            _year = context.RevitYear;
            _settingsPath = Path.Combine(context.UserDataDir, "view-settings.json");
            _settings = ViewSettings.Load(_settingsPath);
            var window = IntPtr.Zero;
            try { window = context.Application.MainWindowHandle; }
            catch (Exception) { /* окна ещё нет — Revit вперёд выходить не будет, остальное работает */ }
            _views = new ClashViews(Environment.UserName, _log);
            _queue = new ShowQueue((app, request) => ClashShow.Run(app, request, _views, _settings), window, _log);
            _status = BridgeProtocol.Write(new StatusReply { Revit = _year });

            // ← прицел: из Revit на сайт (разработчик, 20.10.2026: «вот на ней уже можно и прицел»)
            var clashes = context.AddButton("ElementClashes", "Коллизии\nэлемента", typeof(ElementClashesCommand),
                "Открыть на сайте коллизий все коллизии выделенного элемента (в связи — выделите клавишей Tab)", icon: "ElementClashes");
            clashes.LongDescription = "Выделите элемент — в модели или в связи (Tab) — и нажмите: браузер откроет его коллизии во всех статусах. " +
                                      "Коллизий нет — сайт так и напишет.";

            var settings = context.AddButton("ViewSettings", "Настройки\nвида", typeof(ViewSettingsCommand),
                "Как открывать коллизию по «Показать в Revit»: тип вида, категории элементов, обрезка, покраска, прозрачность, стиль, предупреждения", icon: "ViewSettings");
            settings.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            settings.LongDescription = "Настройки — ваши, у коллег свои. На сайте коллизий «Показать в Revit» открывает личный вид: " +
                                       "3D, план, разрез или «Авто» по элементам; закрыли вкладку вида — он удаляется из модели.";

            var events = context.Events;
            events.Idling(OnIdling);
            events.DocumentOpened((s, e) => _views.Opened(e.Document));
            events.DocumentClosing((s, e) => _views.Closing(e.Document));

            _server = BridgeServer.Start(_site, () => _status, _queue.Ask, _log);
            if (_server != null) _log.Info("Мост запущен: 127.0.0.1:" + _server.Port + ", сайт — " + _site);
        }

        internal void Stop() => _server?.Dispose();

        /// <summary>«Коллизии элемента»: выделенное — на сайт; ничего не выделено — подсказка.</summary>
        internal void OpenElementClashes(UIDocument uidoc)
        {
            try
            {
                if (uidoc == null)
                {
                    TaskDialog.Show("Коллизии элемента", "Откройте модель и выделите элемент.");
                    return;
                }
                var hint = ElementClashes.Open(uidoc, _site);
                if (hint != null) TaskDialog.Show("Коллизии элемента", hint);
            }
            catch (Exception ex)
            {
                _log.Error("Коллизии элемента: сайт не открыт", ex);
                TaskDialog.Show("Коллизии элемента", "Не удалось открыть сайт коллизий — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
            }
        }

        /// <summary>Окно «Настройки вида»; сохранённое действует со следующего «Показать в Revit».</summary>
        internal void ShowSettings(IntPtr owner, Document doc)
        {
            try
            {
                // список категорий — из открытой модели, в контексте команды (окно модальное)
                var window = new ViewSettingsWindow(_settings, doc == null || doc.IsFamilyDocument ? null : (Func<List<CategoryItem>>)(() => ClashCategories.List(doc)));
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                if (window.ShowDialog() != true) return;
                window.Result.Save(_settingsPath);
                _settings = window.Result;
            }
            catch (Exception ex)
            {
                _log.Error("Настройки вида не сохранены", ex);
                TaskDialog.Show("RAS-Clash Analysis", "Настройки вида не сохранены — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
            }
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication app)) return;
            _queue.EnsureEvent();
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
                    .Select(t => ModelNames.Bare(t.Name)));
                reply.Documents.Add(item);
            }
            return reply;
        }
    }
}
