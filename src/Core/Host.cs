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
        public string Title;
        public IModule Module;
        /// <summary>«работает», «отключён: …».</summary>
        public string State;
    }

    /// <summary>
    /// Ядро: его запускает загрузчик (по имени типа и метода, без ссылки на ядро). Создаёт вкладку,
    /// находит и запускает модули, раскладывает их кнопки по панелям (RibbonLayout).
    /// Ошибка модуля отключает только этот модуль (PRINCIPLES.md, раздел 7).
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
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* вкладка уже есть */ }

            foreach (var slot in ModuleCatalog.Discover(buildDir))
            {
                ModuleList.Add(slot);
                if (slot.Module != null) StartModule(app, slot);
            }
            AddButton(new PushButtonData("Core.About", "О плагине", typeof(AboutCommand).Assembly.Location, typeof(AboutCommand).FullName)
            {
                ToolTip = "Версия плагина, модули и их состояние, журнал ошибок"
            }, ServicePanel);
            CreateRibbon(app, buildDir);
            IsStarted = true;
        }

        public static void Stop(UIControlledApplication app)
        {
            foreach (var slot in ModuleList.Where(s => s.Module != null && s.State == ModuleCatalog.Running))
            {
                try { slot.Module.Stop(); }
                catch (Exception ex) { Log.Write(slot.Module.Id, "Ошибка при остановке", ex); }
            }
            _events?.DetachAll();
        }

        private static void StartModule(UIControlledApplication app, ModuleSlot slot)
        {
            var module = slot.Module;
            if (module.RevitYears == null || !module.RevitYears.Contains(RevitYear))
            {
                slot.State = "не работает в Revit " + RevitYear;
                return;
            }
            try
            {
                var context = new ModuleContext(module, app, _events);
                module.Start(context);
                slot.State = ModuleCatalog.Running;
                // кнопки — только у запустившегося модуля: у отключённого их на ленте нет
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
        /// Лента: панели и кнопки по раскладке из сборки (ribbon.txt). Нет файла или он с ошибками — кнопки
        /// стоят на панелях по умолчанию. Ошибка одной кнопки или панели не мешает остальным.
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

            foreach (var panel in RibbonLayout.Arrange(layout, RibbonOrder, w => Log.Write(Tag, w)))
            {
                RibbonPanel ribbonPanel;
                try { ribbonPanel = app.CreateRibbonPanel(TabName, panel.Title); }
                catch (Exception ex)
                {
                    Log.Write(Tag, "Не удалось создать панель «" + panel.Title + "»", ex);
                    continue;
                }
                foreach (var id in panel.Buttons)
                {
                    try { ribbonPanel.AddItem(RibbonData[id]); }
                    catch (Exception ex) { Log.Write(ButtonOwner(id), "Не удалось поставить кнопку «" + id + "» на ленту", ex); }
                }
            }
        }

        /// <summary>Метка журнала для кнопки: модуль — часть кода до точки.</summary>
        private static string ButtonOwner(string id)
        {
            var dot = id.IndexOf('.');
            return dot > 0 ? id.Substring(0, dot) : Tag;
        }
    }

    /// <summary>Поиск модулей в папке сборки: все RASv2.*.dll, кроме ядра и загрузчика.</summary>
    internal static class ModuleCatalog
    {
        public const string Running = "работает";

        public static List<ModuleSlot> Discover(string buildDir)
        {
            var result = new List<ModuleSlot>();
            foreach (var file in Directory.GetFiles(buildDir, "RASv2.*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("RASv2.Core", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("RASv2.Loader", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var assembly = LoadAssembly(file);
                    foreach (var type in assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                    {
                        var module = (IModule)Activator.CreateInstance(type);
                        result.Add(new ModuleSlot { Title = module.Title, Module = module });
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Ядро", "Не удалось загрузить " + name, ex);
                    result.Add(new ModuleSlot { Title = name, State = "отключён: не загрузился (" + ex.Message + ")" });
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
