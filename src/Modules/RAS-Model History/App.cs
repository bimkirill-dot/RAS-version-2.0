using System;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using RASv2.Core;
using RASv2.ModelHistory.Commands;
using RASv2.ModelHistory.Storage;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.UI;

namespace RASv2.ModelHistory
{
    /// <summary>
    /// Работа модуля: стыкуемая панель, кнопки, подписки на события Revit. Запускает ядро через ModelHistoryModule;
    /// кнопки — через контекст ядра, события — через ядро (PRINCIPLES.md, раздел 4).
    /// </summary>
    public sealed class App
    {
        /// <summary>
        /// Панель журнала. GUID прежний: по нему Revit помнит, где пользователь закрепил панель (PRINCIPLES.md, раздел 3).
        /// </summary>
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("3F7E2C1A-8B4D-4F6E-9A21-5C0D7B9E4A13"));

        public static App Instance { get; private set; }

        public SpySettings Settings { get; private set; }
        public LogStore Store { get; private set; }
        public IdleWorkQueue Idle { get; private set; }
        public ChangeTracker Tracker { get; private set; }
        public RevitActions Actions { get; private set; }
        public MainViewModel ViewModel { get; private set; }
        public UiState UiState { get; private set; }

        private SpyPane _pane;
        private IntPtr _mainWindow;

        /// <summary>Панель журнала (её содержимое бывает в отдельном окне — SpyPane.IsDetached).</summary>
        internal SpyPane Pane => _pane;

        /// <summary>Главное окно Revit — владелец диалогов модуля.</summary>
        public IntPtr MainWindowHandle => _mainWindow;

        internal void Start(IModuleContext context)
        {
            Instance = this;
            var application = context.Application;
            try { _mainWindow = application.MainWindowHandle; } catch { /* ignore */ }
            DataFolders.Init(context);
            LegacyFolders.Migrate(DataFolders.UserDataDir, DataFolders.LocalDataDir); // личные папки с прежних мест — до чтения настроек
            WindowsUser.ResolveInBackground(); // полное имя сотрудника для записей журнала — из каталога, не задерживая запуск
            Settings = SpySettings.Load();
            Store = new LogStore();
            Idle = new IdleWorkQueue();
            Tracker = new ChangeTracker(Settings, Store, Idle);
            Actions = new RevitActions(Tracker);
            UiState = UiState.Load();
            ThemeManager.Apply(Settings); // тема как в Revit (или выбранная в настройках) — до создания панели
            ViewModel = new MainViewModel(Tracker, Settings, Actions, UiState);
            ViewModel.OpenSettingsRequested += () => ShowSettings(IntPtr.Zero);

            _pane = new SpyPane(ViewModel, UiState);
            application.RegisterDockablePane(PaneId, "RAS-Model History — журнал изменений", new SpyPaneProvider(_pane));

            CreateButtons(context);

            var events = context.Events;
            events.DocumentChanged(Tracker.OnDocumentChanged);
            events.DocumentOpened(Tracker.OnDocumentOpened);
            events.DocumentClosing(Tracker.OnDocumentClosing);
            events.DocumentClosed(Tracker.OnDocumentClosed);
            events.DocumentSaved(Tracker.OnDocumentSaved);
            events.DocumentSavedAs(Tracker.OnDocumentSavedAs);
            events.DocumentSynchronizingWithCentral(Tracker.OnSynchronizing);
            events.DocumentSynchronizedWithCentral(Tracker.OnSynchronized);
            events.DocumentReloadingLatest(Tracker.OnReloadingLatest);
            events.DocumentReloadedLatest(Tracker.OnReloadedLatest);
            events.ViewActivated(Tracker.OnViewActivated);
            events.SelectionChanged(Tracker.OnSelectionChanged);
            events.Idling(Tracker.OnIdling);
            events.Idling(Idle.OnIdling);
            events.ThemeChanged(OnThemeChanged);

            Diag.Log("Модуль запущен. Папка журналов: " + DataFolders.LogFolder);
        }

        /// <summary>Revit закрывается: сохранить вид панели и дописать очередь журнала. Отписку делает ядро.</summary>
        internal void Stop()
        {
            try { _pane?.SaveUiState(); } // столбцы и размеры панели — до следующего запуска
            catch (Exception ex) { Diag.Log("UI state save failed", ex); }
            Store?.Dispose(); // дописать всё, что осталось в очереди
        }

        private static void CreateButtons(IModuleContext context)
        {
            var journal = context.AddButton("Journal", "Журнал\nизменений", typeof(ShowPaneCommand),
                "Показать / скрыть панель журнала изменений модели");
            journal.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            journal.LongDescription = "Кто, что и когда создал, изменил или удалил: элементы, виды, шаблоны видов, типы, семейства, " +
                                      "фильтры, материалы, листы и спецификации. Для изменённых параметров — значения «было → стало».";
            journal.LargeImage = Icons.Journal(32);
            journal.Image = Icons.Journal(16);

            var history = context.AddButton("History", "История\nвыделенного", typeof(SelectionHistoryCommand),
                "Показать в журнале историю выделенных элементов");
            history.LongDescription = "Выделите один или несколько элементов (или откройте вид) и нажмите — в панели останутся только записи о них.";
            history.LargeImage = Icons.History(32);
            history.Image = Icons.History(16);

            // «Актуализировать журнал» — только в меню «⋯» панели: действие для BIM-менеджера, скрывает записи у всей
            // команды; большая кнопка на ленте приглашала каждого «почистить журнал»
            var settings = context.AddButton("Settings", "Настройки", typeof(SettingsCommand),
                "Тема и период по умолчанию; общие для команды настройки — для просмотра");
            settings.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            settings.LargeImage = Icons.Settings(32);
            settings.Image = Icons.Settings(16);
        }

        public void ShowSettings(IntPtr owner)
        {
            try
            {
                var vm = new SettingsViewModel(Settings);
                var win = new SettingsWindow(vm);
                if (owner != IntPtr.Zero) new WindowInteropHelper(win).Owner = owner;
                else if (_mainWindow != IntPtr.Zero) new WindowInteropHelper(win).Owner = _mainWindow;
                if (win.ShowDialog() != true) return;

                var oldPeriod = Settings.DefaultPeriodDays;
                Settings.CopyFrom(vm.Result);
                Settings.Save();
                ThemeManager.Apply(Settings);
                Tracker.OnSettingsChanged();
                // новый «Период по умолчанию» — сразу, без перезапуска Revit (иначе панель остаётся на своём периоде)
                if (Settings.DefaultPeriodDays != oldPeriod) ViewModel.ApplyDefaultPeriod(Settings.DefaultPeriodDays);
                ViewModel.Reload();
                ViewModel.Status = "Настройки сохранены.";
            }
            catch (Exception ex)
            {
                Diag.Log("Settings failed", ex);
                System.Windows.MessageBox.Show("Не удалось сохранить настройки:\n" + ex.Message, "RAS-Model History");
            }
        }

        /// <summary>Пользователь сменил тему Revit (светлая / тёмная) — перекрасить панель, если тема «как в Revit».</summary>
        private void OnThemeChanged(object sender, Autodesk.Revit.UI.Events.ThemeChangedEventArgs e)
        {
            try { ThemeManager.Apply(Settings); }
            catch (Exception ex) { Diag.Log("Theme change failed", ex); }
        }

        /// <summary>Запомнить главное окно Revit (для владения диалогами).</summary>
        public void RememberMainWindow(UIApplication app)
        {
            try { _mainWindow = app.MainWindowHandle; } catch { /* ignore */ }
        }
    }
}
