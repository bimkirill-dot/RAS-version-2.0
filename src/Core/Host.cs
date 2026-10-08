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
        /// <summary>«работает», «выключен в общих настройках», «отключён: …».</summary>
        public string State;
    }

    /// <summary>
    /// Ядро: его запускает загрузчик (по имени типа и метода, без ссылки на ядро). Создаёт вкладку,
    /// находит и запускает модули (кроме выключенных в общих настройках — ModuleSwitches), раскладывает их кнопки
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
        /// <summary>Общие настройки модулей взяты не с сервера, а из копии — почему; null — всё штатно.</summary>
        public static string ConfigNote { get; private set; }

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
            _events.Idling.Add(Tag, (s, e) => RibbonBuilder.RetryHiddenText()); // кнопки без подписи, если лента была не готова
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* вкладка уже есть */ }

            var switches = LoadSwitches();
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
            CreateRibbon(app, buildDir);
            IsStarted = true;
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

        /// <summary>Общие настройки модулей (Config\modules.txt на сервере или его копия). Нет файла — все работают.</summary>
        private static Dictionary<string, ModuleMode> LoadSwitches()
        {
            var lines = ServerConfig.ReadLines(ModuleSwitches.FileName, out var note);
            ConfigNote = note;
            if (note != null) Log.Write(Tag, "Общие настройки модулей: " + note);
            var switches = ModuleSwitches.Parse(lines ?? new string[0], w => Log.Write(Tag, w));
            foreach (var s in switches.Where(s => s.Value != ModuleMode.Works))
                Log.Write(Tag, "Общие настройки: " + s.Key + " — " + s.Value);
            return switches;
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
                slot.State = "выключен в общих настройках";
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
                slot.State = mode == ModuleMode.NoButtons ? ModuleCatalog.Running + " без кнопок (общие настройки)"
                    : testOnly ? ModuleCatalog.Running + " (только для тестовой группы)"
                    : ModuleCatalog.Running;
                // кнопки — только у запустившегося модуля: у отключённого их на ленте нет; «без кнопок» — тоже нет
                if (mode != ModuleMode.NoButtons)
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
        private static void CreateRibbon(UIControlledApplication app, string buildDir)
        {
            var layout = new List<RibbonPanelSpec>();
            var file = Path.Combine(buildDir, RibbonLayout.FileName);
            try
            {
                if (File.Exists(file)) layout = RibbonLayout.Parse(File.ReadAllLines(file), w => Log.Write(Tag, w));
                else Log.Write(Tag, "Нет раскладки ленты (" + RibbonLayout.FileName + ") — кнопки на панелях модулей");
            }
            catch (Exception ex)
            {
                Log.Write(Tag, "Не удалось прочитать раскладку ленты", ex);
            }
            var plan = RibbonLayout.Arrange(layout, RibbonOrder, w => Log.Write(Tag, w), RibbonBuilder.SelectorId);
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

        /// <param name="switches">Общие настройки: сборки модулей с «не загружать» не загружаются совсем.</param>
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
                    result.Add(new ModuleSlot { Id = code, Title = code, State = "не загружается: выключен в общих настройках" });
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
