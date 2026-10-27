using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>Модуль и его состояние — для окна «О плагине».</summary>
    public sealed class ModuleSlot
    {
        /// <summary>Код модуля (IModule.Id; у незагруженной сборки — окончание её имени).</summary>
        public string Id;
        public string Title;
        public IModule Module;
        /// <summary>Модуль запущен: при закрытии Revit ему вызывается Stop (и после предохранителя — тоже).</summary>
        public bool Running;
        /// <summary>«работает», «выключен (ribbon.txt, {Модули})», «отключён: …».</summary>
        public string State;
    }

    /// <summary>
    /// Ядро: его запускает загрузчик (по имени типа и метода, без ссылки на ядро). Создаёт вкладку,
    /// находит и запускает модули (кроме выключенных в блоке {Модули} файла ribbon.txt — ModuleSwitches), раскладывает их кнопки
    /// по панелям (RibbonLayout). Ошибка модуля отключает только этот модуль (PRINCIPLES.md, раздел 7).
    /// </summary>
    public static class Host
    {
        public const string TabName = "RAS v. 2.0";
        private const string Tag = "Ядро";
        /// <summary>Панель по умолчанию для кнопок ядра («О плагине»).</summary>
        private const string ServicePanel = "Плагин";

        /// <summary>Запущено ли ядро в этой копии сборки (кнопки должны попадать в ту же копию).</summary>
        public static bool IsStarted { get; private set; }
        public static string BuildId { get; private set; }
        public static string BuildDir { get; private set; }
        public static string Channel { get; private set; }
        /// <summary>Почему загрузчик взял не сборку канала; null — всё штатно.</summary>
        public static string LoaderNote { get; private set; }
        public static int RevitYear { get; private set; }

        private static readonly List<ModuleSlot> ModuleList = new List<ModuleSlot>();
        private static RevitEventHub _events;
        /// <summary>Кнопки запущенных модулей и ядра — в порядке объявления; на ленту — после запуска всех модулей.</summary>
        private static readonly List<RibbonButton> RibbonOrder = new List<RibbonButton>();
        private static readonly Dictionary<string, PushButtonData> RibbonData = new Dictionary<string, PushButtonData>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Объявленные кнопки: код и подпись, заданная модулем, — для «О плагине» (коды для раскладки ribbon.txt).</summary>
        public static IEnumerable<KeyValuePair<string, string>> DeclaredButtons =>
            RibbonOrder.Select(b => new KeyValuePair<string, string>(b.Id, RibbonData[b.Id].Text));
        public static IReadOnlyList<ModuleSlot> Modules => ModuleList;

        /// <summary>Вызывает загрузчик (сигнатуру менять только вместе с ним).</summary>
        public static void Start(UIControlledApplication app, string buildId, string buildDir, string channel, string loaderNote, int revitYear)
        {
            BuildId = buildId;
            BuildDir = buildDir;
            Channel = channel;
            LoaderNote = loaderNote;
            RevitYear = revitYear;
            Log.Write(Tag, "Запуск: Revit " + revitYear + ", канал «" + channel + "»");

            _events = new RevitEventHub(app);
            _events.ModuleTripped += OnModuleTripped;
#if !REVIT2023
            _events.ThemeChanged.Add(Tag, (s, e) => OnThemeChanged()); // светлые / тёмные иконки — по теме Revit
#endif
            _events.Idling.Add(Tag, (s, e) => RibbonBuilder.RetryPending()); // кнопки без подписи и ширина, если лента была не готова
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* вкладка уже есть */ }

            var file = ReadPluginFile(buildDir);
            var switches = ModuleSwitches.Parse(file.Modules, w => Log.Write(Tag, w));
            foreach (var s in switches.Where(s => s.Value != ModuleMode.Works))
                Log.Write(Tag, "Режим модуля: " + s.Key + " — " + s.Value);
            _people = ButtonAccess.People(file.People, w => Log.Write(Tag, w));
            _layout = ParseLayout(file.Layout); // до запуска модулей: «доступна=» нужна им уже при запуске (IsAllowed)
            foreach (var slot in ModuleCatalog.Discover(buildDir, switches))
            {
                ModuleList.Add(slot);
                if (slot.Module != null) StartModule(app, slot, ModuleSwitches.ModeOf(switches, slot.Id));
            }
            var about = typeof(AboutCommand).Assembly;
            var aboutData = new PushButtonData(RibbonBuilder.SelectorId, "О плагине", about.Location, typeof(AboutCommand).FullName)
            {
                ToolTip = "Версия плагина, модули и их состояние, коды кнопок, журнал ошибок",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName
            };
            RibbonIcons.Set(aboutData, about, "About");
            AddButton(aboutData, ServicePanel);
            CreateRibbon(app);
            ScheduleWhatsNew();
            IsStarted = true;
        }

        private static bool _whatsNewShown;

        /// <summary>
        /// «Что нового» — один раз после новой сборки, окном при первом простое Revit (когда окно Revit уже есть): текст из notes.txt
        /// сборки (WhatsNew). Показано — номер сборки запоминается до показа: сбой окна не повторит его при каждом запуске.
        /// </summary>
        private static void ScheduleWhatsNew()
        {
            string notes = null, seen = null;
            var seenPath = Path.Combine(RasPaths.User, WhatsNew.SeenFile);
            try
            {
                var notesPath = Path.Combine(BuildDir ?? "", WhatsNew.NotesFile);
                if (File.Exists(notesPath)) notes = File.ReadAllText(notesPath).Trim();
                if (File.Exists(seenPath)) seen = File.ReadAllText(seenPath).Trim();
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "«Что нового»: не прочитано", ex);
                return;
            }
            if (!WhatsNew.ShouldShow(BuildId, Channel, notes, seen)) return;
            _events.Idling.Add(Tag, (s, e) =>
            {
                if (_whatsNewShown) return;
                _whatsNewShown = true;
                try
                {
                    Directory.CreateDirectory(RasPaths.User);
                    File.WriteAllText(seenPath, BuildId);
                }
                catch (Exception ex)
                {
                    Log.Write(Tag, "«Что нового»: не запомнено, что показано", ex);
                }
                var date = WhatsNew.DateOf(BuildId);
                var dialog = new TaskDialog("RAS v. 2.0 — что нового")
                {
                    MainInstruction = "Плагин обновлён" + (date.Length > 0 ? " — выпуск от " + date : ""),
                    MainContent = notes,
                    FooterText = "Версия и модули — кнопка «О плагине» на вкладке «" + TabName + "».",
                };
                dialog.Show();
            });
        }

        public static void Stop(UIControlledApplication app)
        {
            foreach (var slot in ModuleList.Where(s => s.Module != null && s.Running))
            {
                try { slot.Module.Stop(); }
                catch (Exception ex) { Log.Write(slot.Module.Id, "Ошибка при остановке", ex); }
            }
            _events?.DetachAll();
        }

        /// <summary>Группы людей из блока {Люди} — кому доступны кнопки.</summary>
        private static Dictionary<string, List<string>> _people = new Dictionary<string, List<string>>();

        /// <summary>Раскладка ленты из ribbon.txt (с «доступна=» у кнопок).</summary>
        private static List<RibbonPanelSpec> _layout = new List<RibbonPanelSpec>();

        /// <summary>
        /// Доступна ли кнопка («&lt;модуль&gt;.&lt;кнопка&gt;») этому человеку по «доступна=» в ribbon.txt — для модулей
        /// (IModuleContext.IsAllowed): закрыть то же действие в своих окнах, а не только серую кнопку на ленте.
        /// </summary>
        public static bool IsAllowed(string buttonId)
        {
            var user = Environment.UserName;
            return ButtonAccess.AllowedButton(_layout, buttonId, access => ButtonAccess.Allowed(access, user, _people));
        }

        /// <summary>
        /// ribbon.txt в сборке — один файл на всё, что разработчик задаёт для всех (разработчик, 20.10.2026: модули, люди,
        /// лента; раньше режимы модулей — Config\modules.txt на сервере, затем modules.txt в сборке). Нет файла или он не
        /// читается — все модули работают, кнопки — на панелях модулей.
        /// </summary>
        private static PluginFile ReadPluginFile(string buildDir)
        {
            var lines = new string[0];
            var path = Path.Combine(buildDir, RibbonLayout.FileName);
            try
            {
                if (File.Exists(path)) lines = File.ReadAllLines(path);
                else Log.Write(Tag, "Нет " + RibbonLayout.FileName + " — все модули работают, кнопки на панелях модулей");
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось прочитать " + RibbonLayout.FileName + " — все модули работают, кнопки на панелях модулей", ex);
            }
            return PluginFile.Split(lines, w => Log.Write(Tag, w));
        }

        private static void StartModule(UIControlledApplication app, ModuleSlot slot, ModuleMode mode)
        {
            var module = slot.Module;
            if (module.RevitYears == null || !module.RevitYears.Contains(RevitYear))
            {
                slot.State = "не работает в Revit " + RevitYear;
                return;
            }
            if (mode == ModuleMode.Off)
            {
                slot.State = "выключен (ribbon.txt, {Модули})";
                return;
            }
            var testOnly = mode == ModuleMode.TestOnly;
            if (testOnly && !ModuleSwitches.IsTestChannel(Channel))
            {
                slot.State = "только для тестовой группы";
                return;
            }
            try
            {
                var context = new ModuleContext(module, app, _events);
                module.Start(context);
                slot.Running = true;
                slot.State = testOnly ? ModuleCatalog.Running + " (только для тестовой группы)"
                    : ModuleCatalog.Running;
                // кнопки — только у запустившегося модуля: у отключённого (и «только тест» не у тестовой группы) их на ленте нет
                foreach (var data in context.Buttons) AddButton(data, module.Title);
            }
            catch (Exception ex)
            {
                // модуль отключается до следующего запуска Revit, остальные работают
                Log.Write(module.Id, "Модуль не запустился", ex);
                slot.State = "отключён: ошибка при запуске (" + ex.Message + ")";
            }
        }

        private static void AddButton(PushButtonData data, string defaultPanel)
        {
            if (RibbonData.ContainsKey(data.Name))
            {
                Log.Write(Tag, "Кнопка «" + data.Name + "» объявлена дважды — вторая пропущена");
                return;
            }
            RibbonData[data.Name] = data;
            RibbonOrder.Add(new RibbonButton(data.Name, defaultPanel));
        }

        /// <summary>
        /// Лента: отделы, панели, кнопки и стопки по раскладке из сборки (ribbon.txt) — RibbonLayout, строит RibbonBuilder.
        /// Нет файла или он с ошибками — кнопки стоят на панелях по умолчанию.
        /// </summary>
        private static List<RibbonPanelSpec> ParseLayout(string[] layoutLines)
        {
            try
            {
                return RibbonLayout.Parse(layoutLines, w => Log.Write(Tag, w));
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось разобрать раскладку ленты", ex);
                return new List<RibbonPanelSpec>();
            }
        }

        private static void CreateRibbon(UIControlledApplication app)
        {
            // кнопка видна всегда; кому недоступна по «доступна=» — серая (разработчик, 20.10.2026: «кнопку скрыть нельзя»)
            var user = Environment.UserName;
            var plan = RibbonLayout.Arrange(_layout, RibbonOrder, w => Log.Write(Tag, w), RibbonBuilder.SelectorId,
                access => ButtonAccess.Allowed(access, user, _people));
            RibbonBuilder.Build(app, TabName, plan, RibbonData);
        }

        /// <summary>
        /// Предохранитель отключил модуль: обработчики событий модуля ядро уже не вызывает, кнопки модуля — недоступны.
        /// Stop при закрытии Revit модулю по-прежнему вызывается: дописать несохранённое.
        /// </summary>
        private static void OnModuleTripped(string id)
        {
            var slot = ModuleList.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot != null) slot.State = "отключён: слишком много ошибок (с " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ")";
            foreach (var item in RibbonBuilder.AllItems.Where(i => string.Equals(RibbonBuilder.OwnerOf(i.Key), id, StringComparison.OrdinalIgnoreCase)))
            foreach (var copy in item.Value)
            {
                try { copy.Enabled = false; }
                catch (Exception ex) { Log.Write(Tag, "Не удалось выключить кнопку «" + item.Key + "»", ex); }
            }
        }

        /// <summary>Тема Revit сменилась (светлая / тёмная): иконки всех кнопок — по новой теме.</summary>
        private static void OnThemeChanged()
        {
            foreach (var item in RibbonBuilder.AllItems)
            foreach (var copy in item.Value)
            {
                try { RibbonIcons.Refresh(item.Key, copy); }
                catch (Exception ex) { Log.Write(RibbonBuilder.OwnerOf(item.Key), "Не удалось сменить иконку «" + item.Key + "»", ex); }
            }
        }

    }

    /// <summary>Поиск модулей в папке сборки: все RASv2.*.dll, кроме ядра и загрузчика.</summary>
    internal static class ModuleCatalog
    {
        public const string Running = "работает";

        /// <param name="switches">Режимы модулей ({Модули} в ribbon.txt): сборки модулей с «не загружать» не загружаются совсем.</param>
        public static List<ModuleSlot> Discover(string buildDir, IReadOnlyDictionary<string, ModuleMode> switches)
        {
            var result = new List<ModuleSlot>();
            foreach (var file in Directory.GetFiles(buildDir, "RASv2.*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("RASv2.Core", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("RASv2.Loader", StringComparison.OrdinalIgnoreCase)) continue;
                // код модуля = окончание имени сборки (RASv2.<код>.dll) — известен до загрузки
                var code = name.Substring("RASv2.".Length);
                if (ModuleSwitches.ModeOf(switches, code) == ModuleMode.NoLoad)
                {
                    result.Add(new ModuleSlot { Id = code, Title = code, State = "не загружается (ribbon.txt, {Модули})" });
                    continue;
                }
                try
                {
                    var assembly = LoadAssembly(file);
                    foreach (var type in assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                    {
                        var module = (IModule)Activator.CreateInstance(type);
                        result.Add(new ModuleSlot { Id = module.Id, Title = module.Title, Module = module });
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Ядро", "Не удалось загрузить " + name, ex);
                    result.Add(new ModuleSlot { Id = code, Title = name, State = "отключён: не загрузился (" + ex.Message + ")" });
                }
            }
            return result;
        }

        /// <summary>Модули — в тот же контекст загрузки, что и ядро: в процессе одна копия каждой сборки.</summary>
        private static Assembly LoadAssembly(string path)
        {
#if NET
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Host).Assembly)
                          ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            foreach (var loaded in context.Assemblies)
                if (string.Equals(loaded.Location, path, StringComparison.OrdinalIgnoreCase)) return loaded;
            return context.LoadFromAssemblyPath(path);
#else
            return Assembly.LoadFrom(path);
#endif
        }
    }
}
