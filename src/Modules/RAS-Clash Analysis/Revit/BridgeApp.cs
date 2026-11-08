using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Export;
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
        // «Задачи выгрузки» — общее хранилище на сервере (Data\RAS-Clash Analysis\Задачи выгрузки): видят все специалисты
        private string _exportDir;
        // этот Revit запущен фоновым выгрузчиком NWC (модели своей версии для Revit другой версии) — иначе null
        private NwcWorker _worker;

        internal void Start(IModuleContext context)
        {
            Instance = this;
            _log = context.Log;
            if (!string.IsNullOrEmpty(context.SiteAddress)) _site = context.SiteAddress;
            _year = context.RevitYear;
            _settingsPath = Path.Combine(context.UserDataDir, "view-settings.json");
            _settings = ViewSettings.Load(_settingsPath);
            // задачи и общие настройки «Менеджера задач» — у каждого свои (разработчик, 08.11.2026: «отменим многопользовательскую
            // лабуду — пусть у каждого пользователя будут свои задачи»): %AppData%\RAS_v_2.0\RAS-Clash Analysis\Задачи выгрузки; было —
            // общая папка на сервере, оттуда — один раз копией, пока своих нет
            _exportDir = Path.Combine(context.UserDataDir, "Задачи выгрузки");
            TakeShared(Path.Combine(context.SharedDataDir, "Задачи выгрузки"), _exportDir);
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

            var ids = context.AddButton("LinkedId", "ID элемента\nсвязи", typeof(ElementIdCommand),
                "Показать номер (ID) выделенного элемента связи — выделите его клавишей Tab; номер можно скопировать", icon: "LinkedId");
            ids.LongDescription = "«ID выбранного» Revit не показывает номер элемента в связи. Выделите элемент в связи (Tab) и нажмите — " +
                                  "откроется окно с номером. Выделено несколько — номера всех; элемент самой модели — тоже.";

            var settings = context.AddButton("ViewSettings", "Настройки\nвида", typeof(ViewSettingsCommand),
                "Как открывать коллизию по «Показать в Revit»: тип вида, категории элементов, обрезка, покраска, прозрачность, стиль, предупреждения", icon: "ViewSettings");
            settings.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            settings.LongDescription = "Настройки — ваши, у коллег свои. На сайте коллизий «Показать в Revit» открывает личный вид: " +
                                       "3D, план, разрез или «Авто» по элементам; закрыли вкладку вида — он удаляется из модели.";

            // «Задачи выгрузки» (DECISIONS.md модуля): нужна и в пустом Revit — модели выгрузка открывает сама
            var export = context.AddButton("ExportTasks", "Задачи\nвыгрузки", typeof(ExportTasksCommand),
                "Задачи выгрузки NWC: проекты, блоки, критичность — папки, вид, модели", icon: "ExportTasks");
            export.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            export.LongDescription = "Создайте задачу (проект) и подзадачи (блоки) — выгрузка NWC пройдёт сама: копия модели без связей, " +
                                     "вид по шаблону отдела, NWC в папку по общим правилам. Открытый проект не нужен.";

            var events = context.Events;
            events.Idling(OnIdling);
            if (NwcWorker.Requested(_year))
            {
                _worker = new NwcWorker(_year, _log);
                _worker.Start();
            }
            events.DocumentOpened((s, e) => _views.Opened(e.Document));
            events.DocumentClosing((s, e) => _views.Closing(e.Document));

            // фоновый выгрузчик — без моста: «Показать в Revit» с сайта не должна уйти в свёрнутый Revit без человека
            if (_worker != null) return;
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

        /// <summary>«ID элемента связи»: номера выделенного — окном; ничего не выделено — подсказка.</summary>
        internal void ShowElementIds(UIApplication app)
        {
            const string title = "ID элемента связи";
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show(title, "Откройте модель и выделите элемент.");
                    return;
                }
                var items = ElementIds.Selected(uidoc, out var wholeLinks);
                if (items.Count == 0)
                {
                    TaskDialog.Show(title, wholeLinks > 0
                        ? "Выделена связь целиком. Выделите элемент внутри связи: наведите на него и нажимайте Tab, пока он не подсветится, затем щёлкните."
                        : "Выделите элемент в связи (клавишей Tab) и нажмите «ID элемента связи» ещё раз.");
                    return;
                }
                var window = new ElementIdWindow(items);
                new WindowInteropHelper(window).Owner = app.MainWindowHandle;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _log.Error("ID элемента связи: окно не открылось", ex);
                TaskDialog.Show(title, "Не удалось показать номер — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
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

        /// <summary>«Задачи выгрузки» — окно дерева задач.</summary>
        /// <summary>
        /// Своих задач ещё нет, а в прежней общей папке на сервере есть — забрать копией (общая папка не меняется). Не вышло — с пустого:
        /// причина — в журнал плагина.
        /// </summary>
        private void TakeShared(string shared, string mine)
        {
            try
            {
                if (Directory.Exists(mine) || !Directory.Exists(shared)) return;
                foreach (var file in Directory.GetFiles(shared, "*.json", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(mine, file.Substring(shared.Length).TrimStart(Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, false);
                }
                _log.Info("Менеджер задач: задачи и общие настройки — из прежней общей папки " + shared + " в свою " + mine);
            }
            catch (Exception ex)
            {
                _log.Error("Менеджер задач: прежние задачи из общей папки не скопированы — " + shared, ex);
            }
        }

        // «Менеджер задач» — немодальный (разработчик, 08.11.2026: «чтобы с Revit можно было взаимодействовать, пока открыто окно»):
        // одно окно на Revit; Revit API из него — внешним событием (RevitCalls)
        private ExportTasksWindow _tasksWindow;
        private RevitCalls _revitCalls;

        internal void ShowExportTasks(UIApplication app)
        {
            try
            {
                if (_tasksWindow != null)
                {
                    if (_tasksWindow.WindowState == System.Windows.WindowState.Minimized) _tasksWindow.WindowState = System.Windows.WindowState.Normal;
                    _tasksWindow.Activate();
                    return;
                }
                // внешнее событие создаётся здесь — внутри команды Revit
                if (_revitCalls == null) _revitCalls = new RevitCalls((what, ex) => _log.Error(what, ex));
                var calls = _revitCalls;
                var window = new ExportTasksWindow(new ExportStore(_exportDir, Environment.UserName))
                {
                    RevitYear = _year, Revit = app, InRevit = action => calls.Post(ui => action(ui)),
                };
                var owner = app.MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                window.Closed += (s, e) => _tasksWindow = null;
                _tasksWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                _log.Error("Задачи выгрузки: окно не открылось", ex);
                TaskDialog.Show("Задачи выгрузки", "Окно не открылось — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
            }
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication app)) return;
            _worker?.OnIdling(app);
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
