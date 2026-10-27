using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Модель представления панели «Журнал изменений».</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private const string AllAuthors = "Все авторы";
        private const string UnknownAuthor = "(неизвестно)";

        private readonly Dispatcher _ui;
        /// <summary>Сотрудники за авторами записей — для поля «Сотрудник» в подробностях.</summary>
        private readonly EmployeeIndex _employees = new EmployeeIndex();
        private readonly ChangeTracker _tracker;
        private readonly SpySettings _settings;
        private readonly RevitActions _actions;
        private readonly UiState _uiState;
        /// <summary>«Нет сведений» об удалённом — сведения из других записей журнала об этом элементе.</summary>
        private UnknownElementResolver _unknowns = new UnknownElementResolver();

        private readonly List<ChangeRecord> _all = new List<ChangeRecord>();
        /// <summary>Растёт при каждом изменении _all — по нему видно, что расчёты по журналу (_journal) устарели.</summary>
        private int _allVersion;
        private JournalData _journal;
        private volatile LogReader _reader;
        private int _filterVersion;
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private int _readPending;
        private Timer _filterTimer;
        private readonly Timer _autoRefresh;
        private bool _paneVisible;

        public MainViewModel(ChangeTracker tracker, SpySettings settings, RevitActions actions, UiState uiState)
        {
            _ui = Dispatcher.CurrentDispatcher; // создаётся в главном потоке Revit
            _tracker = tracker;
            _settings = settings;
            _actions = actions;
            _uiState = uiState ?? new UiState();
            foreach (var name in _uiState.ParamColumns) if (!string.IsNullOrWhiteSpace(name) && !ParamColumns.Contains(name)) ParamColumns.Add(name);
            _groupByOperation = _uiState.GroupByOperation;
            _followSelection = _uiState.FollowSelection;
            _preferredTab = _uiState.DetailsTab;
            _sortLevels = LoadSort(_uiState.Sort);

            GroupOptions = new List<Option<ObjectGroup?>> { new Option<ObjectGroup?>("Все типы объектов", null) }
                .Concat(new[]
                {
                    ObjectGroup.Model, ObjectGroup.Annotation, ObjectGroup.View, ObjectGroup.ViewTemplate,
                    ObjectGroup.Sheet, ObjectGroup.Schedule, ObjectGroup.ElementType, ObjectGroup.Family,
                    ObjectGroup.Material, ObjectGroup.Filter, ObjectGroup.Parameter, ObjectGroup.Analytical,
                    ObjectGroup.Settings, ObjectGroup.Unknown
                }.Select(g => new Option<ObjectGroup?>(ElementDescriber.GroupDisplayName(g), g)))
                .ToList();
            _periodOptions = BuildPeriods(settings.DefaultPeriodDays);
            _selectedGroup = GroupOptions[0];
            _selectedPeriod = _periodOptions.FirstOrDefault(p => p.Value == settings.DefaultPeriodDays)
                              ?? _periodOptions.First(p => p.Value == 7);
            Authors.Add(AllAuthors);
            _selectedAuthor = AllAuthors;

            RefreshCommand = new RelayCommand(Reload);
            ExportCommand = new RelayCommand(Export, () => Rows.Count > 0);
            ShowInModelCommand = new RelayCommand(ShowInModel, () => SelectedRow?.CanShowInModel == true);
            HistoryCommand = new RelayCommand(ShowRowHistory, () => SelectedRow?.IsElementRecord == true);
            CopyIdCommand = new RelayCommand(CopyId, () => SelectedRow != null && (SelectedRow.IsElementRecord || SelectedRow.IsGroupHeader));
            SelectAllInModelCommand = new RelayCommand(SelectAllInModel, () => _flat.Count > 0 && IsChangesView);
            RowActivatedCommand = new RelayCommand(RowActivated);
            ToggleGroupCommand = new RelayCommand(p => ToggleGroup(p as ChangeRow ?? SelectedRow));
            GoToParentCommand = new RelayCommand(p => GoToParent(p as ChangeRow ?? SelectedRow));
            CardHistoryCommand = new RelayCommand(ShowCardHistory, () => Card != null);
            ClearElementFilterCommand = new RelayCommand(() => SetElementFilter(null, null));
            ResetFiltersCommand = new RelayCommand(ResetFilters);
            SettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke(), () => SettingsAllowed);
            ActualizeCommand = new RelayCommand(StartActualize, () => HasModel && !IsActualizing && !HasPurgePreview);
            ConfirmPurgeCommand = new RelayCommand(ConfirmPurge, () => HasPurgePreview);
            CancelPurgeCommand = new RelayCommand(CancelPurge, () => HasPurgePreview);
            StopActualizeCommand = new RelayCommand(StopActualize, () => IsActualizing);
            ClearSearchCommand = new RelayCommand(() => SearchText = "");
            OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
            LinkCandidateCommand = new RelayCommand(() => { if (_journalCandidate != null) LinkJournal(_journalCandidate); });
            RejectCandidateCommand = new RelayCommand(() => { if (_journalCandidate != null) RejectJournal(_journalCandidate); });
            JournalLinksCommand = new RelayCommand(() => JournalLinksRequested?.Invoke(), () => HasModel);
            AllKindsCommand = new RelayCommand(SetAllKinds);
            OpenSortCommand = new RelayCommand(() => SortDialogRequested?.Invoke());
            OpenFilterCommand = new RelayCommand(() => FilterDialogRequested?.Invoke());
            ShowIn3DCommand = new RelayCommand(ShowIn3D, () => HasModel);
            CloseChangesViewCommand = new RelayCommand(CloseChangesView, () => HasChangesView);
            RebuildChips();

            _tracker.ActiveModelChanged += t => Post(() => SetModel(t));
            _tracker.ChangesViewClosed += t => Post(() =>
            {
                if (string.Equals(t.Model.Key, _changesViewKey, StringComparison.OrdinalIgnoreCase)) HideChangesView();
            });
            _tracker.SelectionCardChanged += card => Post(() =>
            {
                Card = card;
                if (_followSelection && !IsOwnSelection(card)) FollowCard();
            });
            _tracker.WarmupProgressChanged += t => Post(() =>
            {
                UpdateWarmupText(t);
                RefreshParamNames();
            });
            _tracker.Store.Flushed += key =>
            {
                if (_paneVisible && _reader != null && string.Equals(key, _reader.ModelKey, StringComparison.OrdinalIgnoreCase))
                    RequestRead();
            };
            _autoRefresh = new Timer(_ => { if (_paneVisible && _reader != null) RequestRead(); }, null, 5000, 5000);
        }

        /// <summary>Панель просит открыть окно настроек (обрабатывает App).</summary>
        public event Action OpenSettingsRequested;

        /// <summary>«Настройки…» в меню «⋯» — как кнопка на ленте: только тем, кому она доступна (ribbon.txt).</summary>
        public bool SettingsAllowed { get; set; } = true;

        public string SettingsTip => SettingsAllowed ? null : "Недоступна у вас — обратитесь к BIM-менеджеру.";
        /// <summary>«⋯» → «Связанные журналы…» (обрабатывает код окна).</summary>
        public event Action JournalLinksRequested;
        /// <summary>Запрос диалога сохранения файла (обрабатывает код окна).</summary>
        public Func<string, string> SaveFileDialog { get; set; }
        /// <summary>Скопировать текст в буфер (обрабатывает код окна).</summary>
        public Action<string> CopyToClipboard { get; set; }
        /// <summary>Открыть папку в проводнике (обрабатывает код окна).</summary>
        public Action<string> RevealFolder { get; set; }
        /// <summary>Вид панели изменился — сохранить ui.json (с задержкой, обрабатывает код окна).</summary>
        public Action RequestUiSave { get; set; }
        /// <summary>Окно «Показать в 3D…» (обрабатывает код окна): true — «Показать».</summary>
        public Func<ChangesViewDialogModel, bool> AskChangesView { get; set; }
        /// <summary>Прокрутить список к строке: после раскрытия операции или перехода из «Истории» (обрабатывает код окна).</summary>
        public event Action<ChangeRow> ScrollRequested;

        // ───────────────────────────── свойства ─────────────────────────────

        public IReadOnlyList<Option<ObjectGroup?>> GroupOptions { get; }
        /// <summary>Особые значения периода: «Вчера» и «С моей последней синхронизации» (иначе — число дней, 0 — всё время).</summary>
        public const int PeriodYesterday = -1;
        public const int PeriodSinceMySync = -2;

        private IReadOnlyList<Option<int>> _periodOptions;
        public IReadOnlyList<Option<int>> PeriodOptions { get => _periodOptions; private set => Set(ref _periodOptions, value); }

        /// <summary>Периоды панели — те же, что в окне «3D» (там ещё «Свой период»).</summary>
        private static List<Option<int>> BuildPeriods(int defaultDays)
        {
            var periods = new List<Option<int>>
            {
                new Option<int>("Сегодня", 1),
                new Option<int>("Вчера", PeriodYesterday),
                new Option<int>("3 дня", 3),
                new Option<int>("7 дней", 7),
                new Option<int>("30 дней", 30),
                new Option<int>("90 дней", 90),
                new Option<int>("Год", 365),
                new Option<int>("Всё время", 0),
                new Option<int>("С моей последней синхронизации", PeriodSinceMySync)
            };
            // «Период по умолчанию» в настройках — любое число дней: добавляем его в список на своё место
            if (!periods.Any(p => p.Value == defaultDays))
            {
                var at = periods.FindIndex(p => p.Value == 0 || p.Value > defaultDays);
                periods.Insert(at < 0 ? periods.Count : at, new Option<int>(OperationGrouping.Days(defaultDays), defaultDays));
            }
            return periods;
        }

        /// <summary>«Период по умолчанию» изменили в настройках — применить сразу, без перезапуска Revit.</summary>
        public void ApplyDefaultPeriod(int days)
        {
            if (_periodOptions.Any(p => p.Value == days) && SelectedPeriod?.Value == days) return;
            PeriodOptions = BuildPeriods(days);
            SelectedPeriod = PeriodOptions.First(p => p.Value == days);
        }
        public ObservableCollection<string> Authors { get; } = new ObservableCollection<string>();

        private string _modelKey;
        public string ModelKey => _modelKey;
        /// <summary>Прежние ключи модели: журнал, записанный старыми версиями плагина, лежит и в их подпапках.</summary>
        private IReadOnlyList<string> _legacyKeys = Array.Empty<string>();
        /// <summary>Связанные журналы прежней истории (DECISIONS.md модуля, «Связь журналов») — читаются вместе с журналом модели.</summary>
        private IReadOnlyList<string> _linkedKeys = Array.Empty<string>();
        /// <summary>Путь модели (для поиска журнала прежней истории по тому же месту на Revit Server).</summary>
        private string _modelPath;
        /// <summary>Связи и найденные журналы — по модели, один раз за сеанс (сетевая папка бывает медленной); связали — заново.</summary>
        private readonly Dictionary<string, (List<string> Linked, List<JournalInfo> Candidates)> _linkCache =
            new Dictionary<string, (List<string>, List<JournalInfo>)>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Что читать для 3D-вида и актуализации: прежние ключи и связанные журналы.</summary>
        private List<string> ReadKeys => _legacyKeys.Concat(_linkedKeys).ToList();

        private string _modelTitle = "Нет открытой модели";
        public string ModelTitle { get => _modelTitle; private set => Set(ref _modelTitle, value); }

        private string _modelInfo = "";
        /// <summary>Полные сведения о модели и папке журнала (подсказка к названию модели).</summary>
        public string ModelInfo { get => _modelInfo; private set => Set(ref _modelInfo, value); }

        private string _modelMode = "";
        /// <summary>«совместная работа» / «одиночный файл» — рядом с названием модели.</summary>
        public string ModelMode { get => _modelMode; private set => Set(ref _modelMode, value); }

        private string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }

        private string _warmupText = "";
        public string WarmupText
        {
            get => _warmupText;
            private set { if (Set(ref _warmupText, value)) OnPropertyChanged(nameof(HasWarmup)); }
        }

        private int _warmupPercent;
        /// <summary>Ход подготовки модели, % (полоска в строке состояния).</summary>
        public int WarmupPercent { get => _warmupPercent; private set => Set(ref _warmupPercent, value); }

        public bool HasWarmup => !string.IsNullOrEmpty(WarmupText);

        private bool _isWarmupDone;
        /// <summary>Модель запомнена полностью — полоска не нужна, остаётся «✓ Модель запомнена».</summary>
        public bool IsWarmupDone { get => _isWarmupDone; private set => Set(ref _isWarmupDone, value); }

        public string WarmupToolTip => WarmupProgress.ToolTip;

        private string _summaryText = "";
        /// <summary>Строка состояния: «записей: 1 245» / «показаны последние …» / предпросмотр актуализации.</summary>
        public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

        private string _statCreated = "", _statModified = "", _statDeleted = "";
        public string StatCreated { get => _statCreated; private set => Set(ref _statCreated, value); }
        public string StatModified { get => _statModified; private set => Set(ref _statModified, value); }
        public string StatDeleted { get => _statDeleted; private set => Set(ref _statDeleted, value); }

        private bool _hasStats;
        public bool HasStats { get => _hasStats; private set => Set(ref _hasStats, value); }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }

        public bool HasModel => _modelKey != null;

        public bool ShowEmptyHint => !HasModel || (Rows.Count == 0 && !IsLoading);

        public string EmptyHint
        {
            get
            {
                if (!HasModel) return _tracker.UntrackedNote ?? "Откройте модель Revit — здесь появится журнал её изменений.";
                if (IsEventsView)
                    return "Событий модели за выбранный период нет." + Environment.NewLine +
                           "Здесь видно, кто открывал, синхронизировал, сохранял и закрывал модель.";
                if (HasElementFilter)
                    return $"Для выбранных элементов записей нет за период «{SelectedPeriod?.Label}»." + Environment.NewLine +
                           "Их могли менять раньше — увеличьте период (например, «Всё время») или сбросьте фильтры.";
                return "Записей нет за выбранный период или по заданным фильтрам." + Environment.NewLine +
                       "Изменения появятся здесь, как только вы или коллеги что-то измените в модели.";
            }
        }

        private IReadOnlyList<ChangeRow> _rows = new List<ChangeRow>();
        /// <summary>Строки таблицы (с учётом группировки по операциям).</summary>
        public IReadOnlyList<ChangeRow> Rows
        {
            get => _rows;
            private set
            {
                if (!Set(ref _rows, value)) return;
                OnPropertyChanged(nameof(ChangeRows));
                OnPropertyChanged(nameof(EventRows));
                OnPropertyChanged(nameof(ShowEmptyHint));
                OnPropertyChanged(nameof(EmptyHint));
                RaiseCommands();
            }
        }

        private static readonly IReadOnlyList<ChangeRow> NoRows = new List<ChangeRow>();

        /// <summary>
        /// Строки для таблицы «Изменения» и для таблицы «События модели». У каждой таблицы свой список:
        /// иначе они делили бы одно представление коллекции, и сортировка одной ломала бы порядок в другой.
        /// </summary>
        public IReadOnlyList<ChangeRow> ChangeRows => _eventsView ? NoRows : _rows;
        public IReadOnlyList<ChangeRow> EventRows => _eventsView ? _rows : NoRows;

        /// <summary>
        /// Выбранная строка для каждой таблицы. Скрытая таблица (у неё пустой список) не должна сбрасывать выбор:
        /// пустое значение от таблицы принимается, только если её список действительно пуст или строка из него ушла.
        /// </summary>
        public ChangeRow SelectedChangeRow
        {
            get => _eventsView ? null : _selectedRow;
            set { if (!_eventsView) SelectedRow = value; }
        }

        public ChangeRow SelectedEventRow
        {
            get => _eventsView ? _selectedRow : null;
            set { if (_eventsView) SelectedRow = value; }
        }

        /// <summary>Все записи текущего списка по одной (без заголовков операций) — для экспорта и «Выделить все».</summary>
        /// <summary>Весь отфильтрованный список последнего расчёта и его контекст (статусы, убранные) — для экспорта и выделения.</summary>
        private List<ChangeRecord> _filtered = new List<ChangeRecord>();
        private RowContext _filteredContext;
        private List<ChangeRow> _flat = new List<ChangeRow>();

        private ChangeRow _selectedRow;
        public ChangeRow SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!Set(ref _selectedRow, value)) return;
                foreach (var n in new[]
                         {
                             nameof(HasSelectedRow), nameof(NoSelectedRow), nameof(SelectedParams), nameof(ChangesTabHeader),
                             nameof(HasSelectedParams), nameof(ChangesEmptyText), nameof(DetailTitle), nameof(DetailSubtitle),
                             nameof(DetailKindKey), nameof(DetailFields), nameof(DetailNote), nameof(HasDetailNote),
                             nameof(SelectedAllParams), nameof(SelectedAllParamsTitle), nameof(HasElementDetails),
                             nameof(ElementHistory), nameof(HistoryTabHeader), nameof(DetailsTabIndex), nameof(IsHeaderSelected),
                             nameof(SelectedChangeRow), nameof(SelectedEventRow)
                         })
                    OnPropertyChanged(n);
                RaiseCommands();
            }
        }

        public bool HasSelectedRow => SelectedRow != null;
        public bool NoSelectedRow => SelectedRow == null;
        /// <summary>Выбрана запись об элементе: доступны вкладки «Было → стало», «Все параметры», «История».</summary>
        public bool HasElementDetails => SelectedRow?.IsElementRecord == true;
        public bool IsHeaderSelected => SelectedRow?.IsGroupHeader == true;

        private int _preferredTab = 1;
        /// <summary>
        /// Открытая вкладка подробностей. У событий модели и операций есть только «Запись»,
        /// но выбранная пользователем вкладка запоминается и возвращается для обычных записей.
        /// </summary>
        public int DetailsTabIndex
        {
            get => HasElementDetails ? _preferredTab : 0;
            set
            {
                if (!HasElementDetails || value < 0 || value > 3 || value == _preferredTab) return;
                _preferredTab = value;
                _uiState.DetailsTab = value;
                RequestUiSave?.Invoke();
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<ParamChangeRow> SelectedParams => SelectedRow?.ParamRows ?? new List<ParamChangeRow>();

        public bool HasSelectedParams => SelectedRow != null && !SelectedRow.IsGroupHeader && SelectedRow.Record.HasParams;

        /// <summary>Что показать на вкладке «Было → стало», если изменённых параметров нет.</summary>
        public string ChangesEmptyText
        {
            get
            {
                var r = SelectedRow?.Record;
                if (r == null || HasSelectedParams) return "";
                switch (r.Kind)
                {
                    case ChangeKind.Created:
                        return "Элемент создан. Значения параметров сразу после создания — на вкладке «Все параметры».";
                    case ChangeKind.Deleted:
                        return "Элемент удалён. Значения параметров на момент удаления — на вкладке «Все параметры».";
                    case ChangeKind.Event:
                        return "Событие модели — параметров нет.";
                    default:
                        return r.IsSummary
                            ? "Сводная запись массовой операции: параметры по отдельным элементам не разбирались."
                            : NoParamsText(r);
                }
            }
        }

        /// <summary>Почему у «Изменён» нет «было → стало» — по отметке «было известно» (bk).</summary>
        private static string NoParamsText(ChangeRecord r)
        {
            if (r.BeforeKnown == true)
                return "Параметры не изменились: Revit пересчитал элемент (соединения, помещения, правка соседних элементов) " +
                       "или изменилось то, чего нет в параметрах (геометрия эскиза, графика).";
            if (r.BeforeKnown == false)
                return "«Было» неизвестно: подготовка модели ещё шла, сравнить было не с чем.";
            return "Параметры не изменились или «было» неизвестно (запись старой версии плагина) — " +
                   "вероятно, Revit пересчитал элемент или изменилась геометрия.";
        }

        /// <summary>Заголовок вкладки «Было → стало» с числом изменённых параметров (не «Изменения» — так называется вкладка панели).</summary>
        public string ChangesTabHeader
        {
            get
            {
                var n = HasSelectedParams ? SelectedRow.Record.Params.Count : 0;
                return n > 0 ? $"Было → стало ({n})" : "Было → стало";
            }
        }

        // ── подробности выбранной записи ──

        /// <summary>Имя элемента (крупно) в подробностях.</summary>
        public string DetailTitle
        {
            get
            {
                var row = SelectedRow;
                if (row == null) return "";
                if (row.IsGroupHeader) return row.ObjectTitle;
                if (row.Record.Kind == ChangeKind.Event) return row.EventTypeText;
                return string.IsNullOrEmpty(row.Record.Name) ? row.KindText : row.Record.Name;
            }
        }

        /// <summary>«Изменён · Стены · ID 348112».</summary>
        public string DetailSubtitle
        {
            get
            {
                var row = SelectedRow;
                var r = row?.Record;
                if (r == null) return "";
                if (row.IsGroupHeader) return row.KindText + " · операция Revit";
                if (r.Kind == ChangeKind.Event) return "Событие модели";
                var parts = new List<string> { row.KindText };
                if (!string.IsNullOrEmpty(r.Category)) parts.Add(r.Category);
                if (r.ElementId > 0) parts.Add("ID " + r.ElementId.ToString(CultureInfo.InvariantCulture));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Цвет подзаголовка (как у действия в таблице).</summary>
        public string DetailKindKey => SelectedRow?.KindKey ?? "";

        /// <summary>Поля записи: «Автор — Иванов · PC-012», «Команда — …». Пустые поля не показываются.</summary>
        public IReadOnlyList<DetailField> DetailFields
        {
            get
            {
                var row = SelectedRow;
                var r = row?.Record;
                var list = new List<DetailField>();
                if (r == null) return list;
                void Add(string label, string value, string tip = null)
                {
                    if (!string.IsNullOrWhiteSpace(value)) list.Add(new DetailField(label, value, tip));
                }
                Add("Автор", string.IsNullOrEmpty(r.Machine) ? row.AuthorText : row.AuthorText + " · " + r.Machine);
                Add("Сотрудник", _employees.Describe(r),
                    "Полное имя и учётная запись Windows автора — по ним сотрудника находят в каталоге компании");
                Add("Время", row.TimeText);
                if (row.HasParentLink)
                    Add("Удалён вместе с", row.ParentText.Substring("Удалён вместе с: ".Length),
                        "Элемент удалён не сам: Revit удалил его вместе с элементом, от которого он зависел. Ссылка — в «Что изменилось».");
                if (r.InfoFromJournal)
                    Add("Сведения", "из прежних записей журнала об этом элементе",
                        "Модуль не успел запомнить элемент до удаления; имя, категория и тип взяты из прежних записей журнала о нём.");
                if (r.Kind == ChangeKind.Event)
                {
                    Add("Событие", row.EventTypeText);
                    Add("Описание", r.Name);
                    return list;
                }
                Add("Источник", row.SourceText);
                Add("Команда", r.Transaction);
                if (row.IsGroupHeader)
                {
                    Add("Элементов", row.GroupCount.ToString(CultureInfo.InvariantCulture));
                    Add("По категориям", row.CategorySummary(12));
                    Add("Параметры", row.ParamsText);
                    Add("Статус", row.StatusText, row.StatusExplanation);
                    return list;
                }
                Add("Тип объекта", row.GroupText);
                Add("Тип", r.TypeName);
                Add("Уровень", r.Level);
                Add("Рабочий набор", r.Workset);
                Add("Вид", r.OwnerView);
                Add("Создатель", r.Creator);
                Add("Последним синхронизировал", r.LastChangedBy, "Кто последним синхронизировал изменение этого элемента (данные совместной работы Revit)");
                Add("Статус", row.StatusText, row.StatusExplanation);
                return list;
            }
        }

        /// <summary>Пояснение под полями: что значит статус, почему нет «было → стало», как раскрыть операцию.</summary>
        public string DetailNote
        {
            get
            {
                var row = SelectedRow;
                var r = row?.Record;
                if (r == null) return "";
                var lines = new List<string>();
                if (row.IsGroupHeader)
                    lines.Add("Одна операция Revit затронула несколько элементов. Раскройте строку (▸ или двойной щелчок), " +
                              "чтобы увидеть каждый; «Показать в модели» выделит их все.");
                var explain = row.StatusExplanation;
                if (!string.IsNullOrEmpty(explain)) lines.Add(explain);
                if (!row.IsGroupHeader && r.Kind == ChangeKind.Modified && !r.HasParams)
                    lines.Add(NoParamsText(r));
                return string.Join(Environment.NewLine, lines);
            }
        }

        public bool HasDetailNote => !string.IsNullOrEmpty(DetailNote);

        // ── вкладка «История»: все записи о том же элементе за выбранный период ──

        public IReadOnlyList<HistoryItem> ElementHistory
        {
            get
            {
                var row = SelectedRow;
                if (row == null || !row.IsElementRecord) return new List<HistoryItem>();
                if (ReferenceEquals(_historyFor, row.Record) && _historyAllCount == _all.Count &&
                    ReferenceEquals(_historyStatuses, _lastStatuses) && _historyCache != null)
                    return _historyCache;
                var id = row.Record.ElementId;
                var ctx = MakeRowContext();
                ctx.Statuses = _lastStatuses;
                ctx.Excluded = _lastExcluded;
                var records = _all.Where(r => r.ElementId == id && r.Kind != ChangeKind.Event).ToList();
                _historyCount = records.Count;
                _historyCache = records
                    .OrderByDescending(r => r.Time)
                    .Take(500)
                    .Select(r => new HistoryItem(new ChangeRow(r, ctx), ReferenceEquals(r, row.Record)))
                    .ToList();
                _historyFor = row.Record;
                _historyAllCount = _all.Count;
                _historyStatuses = _lastStatuses;
                return _historyCache;
            }
        }

        private List<HistoryItem> _historyCache;
        private ChangeRecord _historyFor;
        private int _historyAllCount = -1;
        private int _historyCount;
        private Dictionary<ChangeRecord, SyncStatus> _historyStatuses;

        public string HistoryTabHeader
        {
            get
            {
                if (!HasElementDetails) return "История";
                var n = ElementHistory.Count > 0 ? _historyCount : 0;
                return n > 1 ? $"История ({n})" : "История";
            }
        }

        /// <summary>Щелчок по записи на вкладке «История» — выбрать её в списке.</summary>
        public HistoryItem SelectedHistoryItem
        {
            get => null;
            set
            {
                if (value == null) return;
                var record = value.Row.Record;
                var isCurrent = value.IsCurrent;
                _ui.BeginInvoke(new Action(() =>
                {
                    if (!isCurrent) SelectRecord(record);
                    OnPropertyChanged(nameof(SelectedHistoryItem)); // снять выделение в списке истории
                }));
            }
        }

        // ── параметры: столбец «значение параметра» и вкладка «Все параметры» ──

        /// <summary>Имена всех параметров модели (по снимку) и встреченных в журнале.</summary>
        public ObservableCollection<string> ParamNames { get; } = new ObservableCollection<string>();
        private readonly HashSet<string> _journalParamNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>
        /// Столбцы параметров модели в таблице (правый щелчок по заголовку → «Добавить столбец параметра…»);
        /// запоминаются в ui.json. Таблицу по ним строит окно (SpyPane).
        /// </summary>
        public ObservableCollection<string> ParamColumns { get; } = new ObservableCollection<string>();

        public void AddParamColumn(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || ParamColumns.Contains(name, StringComparer.CurrentCultureIgnoreCase)) return;
            ParamColumns.Add(name);
            SaveParamColumns();
        }

        public void RemoveParamColumn(string name)
        {
            var existing = ParamColumns.FirstOrDefault(x => string.Equals(x, name, StringComparison.CurrentCultureIgnoreCase));
            if (existing == null) return;
            ParamColumns.Remove(existing);
            if (string.Equals(ParamColumnName, existing, StringComparison.CurrentCultureIgnoreCase)) ParamColumnName = "";
            SaveParamColumns();
        }

        /// <summary>Щелчок по заголовку столбца параметра: сортировать по нему.</summary>
        public void SortByParam(string name)
        {
            ParamColumnName = name;
            SortByColumn(SortField.Param);
        }

        private void SaveParamColumns()
        {
            _uiState.ParamColumns = ParamColumns.ToList();
            RequestUiSave?.Invoke();
        }

        private string _paramColumnName = "";
        /// <summary>Параметр, по которому сортируют (столбец параметра, по заголовку которого щёлкнули).</summary>
        public string ParamColumnName
        {
            get => _paramColumnName;
            set
            {
                if (!Set(ref _paramColumnName, (value ?? "").Trim())) return;
                OnPropertyChanged(nameof(HasParamColumn));
                OnPropertyChanged(nameof(SortSummary));
                RebuildChips();
                ScheduleFilter();
            }
        }

        public bool HasParamColumn => !string.IsNullOrEmpty(_paramColumnName);

        private bool _modelIsWorkshared = true;

        private RowContext MakeRowContext()
        {
            var key = _modelKey;
            return new RowContext
            {
                ParamName = HasParamColumn ? _paramColumnName : null,
                Lookup = (id, name) => CachedLookup(key, id, name),
                IsWorkshared = _modelIsWorkshared
            };
        }

        private void CollectParamNames(IEnumerable<ChangeRecord> records)
        {
            foreach (var r in records)
            {
                if (r.Params != null) foreach (var p in r.Params) if (!IsPseudo(p.Name)) _journalParamNames.Add(p.Name);
                if (r.State != null) foreach (var p in r.State) if (!IsPseudo(p.Name)) _journalParamNames.Add(p.Name);
            }
        }

        private static bool IsPseudo(string name) => string.IsNullOrEmpty(name) || name.StartsWith("[", StringComparison.Ordinal);

        private readonly HashSet<string> _listedParamNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>
        /// Дополнить список параметров для выбора (снимок модели + журнал).
        /// Список не очищается, новые имена вставляются на своё место — иначе редактируемый
        /// ComboBox сбрасывал бы текст, пока пользователь вводит имя.
        /// </summary>
        public void RefreshParamNames()
        {
            var fresh = new List<string>();
            foreach (var n in _journalParamNames) if (_listedParamNames.Add(n)) fresh.Add(n);
            foreach (var n in _tracker.ParamNames(_modelKey)) if (_listedParamNames.Add(n)) fresh.Add(n);
            if (fresh.Count == 0) return;
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            if (ParamNames.Count == 0 || fresh.Count > 200)
            {
                // первая (или крупная) порция: собрать общий отсортированный список один раз
                var all = ParamNames.Concat(fresh).OrderBy(x => x, cmp).ToList();
                for (int i = 0; i < all.Count; i++)
                {
                    if (i < ParamNames.Count && cmp.Equals(ParamNames[i], all[i])) continue;
                    ParamNames.Insert(i, all[i]);
                }
                return;
            }
            foreach (var n in fresh)
            {
                int lo = 0, hi = ParamNames.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (cmp.Compare(ParamNames[mid], n) < 0) lo = mid + 1; else hi = mid;
                }
                ParamNames.Insert(lo, n);
            }
        }

        // кэш значений для столбца параметра (сортировка / экспорт не читают диск повторно)
        private readonly Dictionary<(string, long), string> _paramValueCache = new Dictionary<(string, long), string>();
        private string _paramValueCacheModel;
        private int _paramValueCacheStamp = -1;

        private string CachedLookup(string modelKey, long id, string name)
        {
            if (_paramValueCacheStamp != _tracker.ChangeStamp || _paramValueCacheModel != modelKey)
            {
                _paramValueCache.Clear();
                _paramValueCacheStamp = _tracker.ChangeStamp;
                _paramValueCacheModel = modelKey;
            }
            var key = ((name ?? "").ToLowerInvariant(), id);
            if (_paramValueCache.TryGetValue(key, out var cached)) return cached;
            var v = _tracker.LookupParam(modelKey, id, name);
            _paramValueCache[key] = v;
            return v;
        }

        private string _paramFilterText = "";
        /// <summary>Поиск на вкладке «Все параметры» (по имени или значению).</summary>
        public string ParamFilterText
        {
            get => _paramFilterText;
            set { if (Set(ref _paramFilterText, value ?? "")) OnPropertyChanged(nameof(SelectedAllParams)); }
        }

        /// <summary>Все параметры элемента выбранной записи (с учётом поиска по параметрам).</summary>
        public IReadOnlyList<ParamValueRow> SelectedAllParams
        {
            get
            {
                var r = SelectedRow?.Record;
                if (r == null || r.ElementId <= 0) return new List<ParamValueRow>();
                var state = r.HasState ? r.State : _tracker.LookupState(_modelKey, r.ElementId);
                if (state == null) return new List<ParamValueRow>();
                var changed = new HashSet<string>(r.Params?.Select(p => p.Name) ?? Enumerable.Empty<string>(),
                    StringComparer.CurrentCultureIgnoreCase);
                var f = _paramFilterText.Trim();
                IEnumerable<ParamValue> q = state;
                if (f.Length > 0) q = q.Where(p => Has(p.Name, f) || Has(p.Value, f));
                return q.Select(p => new ParamValueRow(p.Name, p.Value, changed.Contains(p.Name))).ToList();
            }
        }

        public string SelectedAllParamsTitle
        {
            get
            {
                var r = SelectedRow?.Record;
                if (r == null || r.ElementId <= 0) return "";
                if (r.HasState)
                    return r.Kind == ChangeKind.Deleted ? "Параметры на момент удаления" : "Параметры сразу после создания";
                var hasCurrent = _tracker.LookupState(_modelKey, r.ElementId) != null;
                if (!hasCurrent)
                    return r.Kind == ChangeKind.Deleted
                        ? "Параметры удалённого элемента неизвестны (он был удалён до того, как плагин успел их запомнить)"
                        : "Параметры ещё не запомнены (идёт подготовка модели или полный снимок отключён)";
                return r.Kind == ChangeKind.Deleted
                    ? "Последние известные параметры (элемент удалён)"
                    : "Текущие значения (по последнему снимку; жирным — изменённые в этой записи)";
            }
        }

        // фильтры

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value ?? "")) return;
                OnPropertyChanged(nameof(HasSearchText));
                ScheduleFilter();
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

        private Option<ObjectGroup?> _selectedGroup;
        public Option<ObjectGroup?> SelectedGroup { get => _selectedGroup; set { if (Set(ref _selectedGroup, value)) FiltersChanged(); } }

        private string _selectedAuthor;
        public string SelectedAuthor
        {
            get => _selectedAuthor;
            set
            {
                if (_rebuildingAuthors || value == null) return; // ComboBox сбрасывает выбор при пересборке списка
                if (Set(ref _selectedAuthor, value)) FiltersChanged();
            }
        }

        private Option<int> _selectedPeriod;
        public Option<int> SelectedPeriod { get => _selectedPeriod; set { if (Set(ref _selectedPeriod, value)) Reload(); } }

        // ── вкладки «Изменения» / «События модели» ──

        private bool _eventsView;
        /// <summary>Открыта вкладка «Изменения» (записи об элементах).</summary>
        public bool IsChangesView
        {
            get => !_eventsView;
            set { if (value) SetView(false); }
        }

        /// <summary>Открыта вкладка «События модели»: открытие, синхронизация, сохранение, закрытие.</summary>
        public bool IsEventsView
        {
            get => _eventsView;
            set { if (value) SetView(true); }
        }

        private void SetView(bool events)
        {
            if (_eventsView == events) return;
            _eventsView = events;
            OnPropertyChanged(nameof(IsChangesView));
            OnPropertyChanged(nameof(IsEventsView));
            OnPropertyChanged(nameof(HeaderLevelCount));
            OnPropertyChanged(nameof(EmptyHint));
            SelectedRow = null;
            Rows = new List<ChangeRow>();
            _flat = new List<ChangeRow>();
            FiltersChanged();
        }

        // ── «Действие»: какие записи показывать (флажки в выпадающем списке) ──

        private bool _showCreated = true, _showModified = true, _showDeleted = true;
        public bool ShowCreated { get => _showCreated; set { if (Set(ref _showCreated, value)) KindsChanged(); } }
        public bool ShowModified { get => _showModified; set { if (Set(ref _showModified, value)) KindsChanged(); } }
        public bool ShowDeleted { get => _showDeleted; set { if (Set(ref _showDeleted, value)) KindsChanged(); } }

        private bool AllKinds => _showCreated && _showModified && _showDeleted;

        /// <summary>Подпись на кнопке «Действие: …».</summary>
        public string KindSummary => KindFilter.Summary(_showCreated, _showModified, _showDeleted);

        private void KindsChanged()
        {
            OnPropertyChanged(nameof(KindSummary));
            FiltersChanged();
        }

        private void SetKinds(bool created, bool modified, bool deleted)
        {
            _showCreated = created;
            _showModified = modified;
            _showDeleted = deleted;
            foreach (var n in new[] { nameof(ShowCreated), nameof(ShowModified), nameof(ShowDeleted) })
                OnPropertyChanged(n);
            KindsChanged();
        }

        private void SetAllKinds() => SetKinds(true, true, true);

        // ── «Ещё»: редкие фильтры и вид списка ──

        private bool _hideDuplicates = true;
        /// <summary>Скрывать записи «при синхронизации», если то же изменение уже записал сам автор.</summary>
        public bool HideDuplicates { get => _hideDuplicates; set { if (Set(ref _hideDuplicates, value)) FiltersChanged(); } }

        private bool _showNoise;
        public bool ShowNoise
        {
            get => _showNoise;
            set
            {
                if (!Set(ref _showNoise, value)) return;
                OnPropertyChanged(nameof(ShowNoiseOption));
                FiltersChanged();
            }
        }

        /// <summary>Флажок «Служебные элементы» нужен, только если они есть в журнале (их запись включают в общих настройках).</summary>
        public bool ShowNoiseOption => _hasNoiseRecords || _showNoise;

        /// <summary>В одиночном файле хранилища нет — там «не попало» значит «закрыто без сохранения».</summary>
        public string HideVoidLabel => _modelIsWorkshared ? "Скрыть не попавшие в хранилище" : "Скрыть закрытые без сохранения";

        private bool _hideVoid = true;
        /// <summary>
        /// Скрывать изменения, которые не попали в хранилище (модель закрыта без синхронизации). Включено по умолчанию:
        /// этих изменений фактически не было.
        /// </summary>
        public bool HideVoid { get => _hideVoid; set { if (Set(ref _hideVoid, value)) FiltersChanged(); } }

        private bool _hideUndone = true;
        /// <summary>Скрывать пары «правка + её отмена (Ctrl+Z)»: по итогу ничего не изменилось (см. UndoPairs).</summary>
        public bool HideUndone { get => _hideUndone; set { if (Set(ref _hideUndone, value)) FiltersChanged(); } }

        private bool _groupByOperation = true;
        /// <summary>Записи одной операции Revit — одной строкой, которую можно раскрыть.</summary>
        public bool GroupByOperation
        {
            get => _groupByOperation;
            set
            {
                if (!Set(ref _groupByOperation, value)) return;
                _uiState.GroupByOperation = value;
                RequestUiSave?.Invoke();
                ScheduleFilter(0);
            }
        }

        private readonly HashSet<string> _expandedGroups = new HashSet<string>(StringComparer.Ordinal);

        // ── «Сортировка и группировка» — как в спецификации Revit ──

        private List<SortLevel> _sortLevels;
        /// <summary>Свёрнутые заголовки групп (по ключу группы; сохраняются при обновлении списка).</summary>
        private readonly HashSet<string> _collapsedValueGroups = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Окно «Сортировка и группировка» (открывает код окна).</summary>
        public event Action SortDialogRequested;

        public IReadOnlyList<SortLevel> SortLevels => _sortLevels;

        /// <summary>Сколько уровней с заголовком групп (столько уровней группировки в таблице).</summary>
        public int HeaderLevelCount => _eventsView || _previewReasons != null ? 0 : _sortLevels.Count(l => l.Header);

        /// <summary>Подпись на кнопке: «время ↓», «автор ↑, время ↓».</summary>
        public string SortSummary => RowSorting.Summary(_sortLevels, _paramColumnName);

        private static List<SortLevel> LoadSort(List<SortLevelState> saved)
        {
            var levels = new List<SortLevel>();
            foreach (var s in saved ?? new List<SortLevelState>())
            {
                if (!Enum.TryParse<SortField>(s.Field, out var f) || f == SortField.Param) continue; // столбец параметра не сохраняется
                if (levels.Any(l => l.Field == f) || levels.Count >= RowSorting.MaxLevels) continue;
                levels.Add(new SortLevel(f, s.Descending, s.Header));
            }
            return levels.Count > 0 ? levels : RowSorting.Default();
        }

        /// <summary>Применить настройки из окна «Сортировка и группировка».</summary>
        public void SetSort(IEnumerable<SortLevel> levels, bool groupByOperation)
        {
            var list = new List<SortLevel>();
            foreach (var l in levels ?? Enumerable.Empty<SortLevel>())
            {
                if (l == null || list.Any(x => x.Field == l.Field) || list.Count >= RowSorting.MaxLevels) continue;
                if (l.Field == SortField.Param && !HasParamColumn) continue;
                list.Add(l);
            }
            _sortLevels = list.Count > 0 ? list : RowSorting.Default();
            if (_groupByOperation != groupByOperation)
            {
                _groupByOperation = groupByOperation;
                _uiState.GroupByOperation = groupByOperation;
                OnPropertyChanged(nameof(GroupByOperation));
            }
            SortChanged();
        }

        /// <summary>
        /// Щелчок по заголовку столбца: этот столбец становится первым уровнем сортировки
        /// (повторный щелчок меняет направление), остальные уровни сохраняются.
        /// </summary>
        public void SortByColumn(SortField field)
        {
            if (field == SortField.Param && !HasParamColumn) return;
            var list = _sortLevels.ToList();
            var existing = list.FindIndex(l => l.Field == field);
            // уровни с «Заголовком» в начале списка остаются группами; столбец встаёт сразу после них
            var insertAt = 0;
            while (insertAt < list.Count && list[insertAt].Header) insertAt++;
            if (existing >= 0 && (list[existing].Header || existing == insertAt))
            {
                // столбец уже группирует или уже первый после групп — меняем направление на месте
                list[existing] = list[existing].With(descending: !list[existing].Descending);
            }
            else
            {
                if (existing >= 0) list.RemoveAt(existing); // он стоит после групп (до insertAt — только уровни с заголовком)
                list.Insert(Math.Min(insertAt, list.Count), new SortLevel(field, field == SortField.Time, false));
            }
            _sortLevels = list.Take(RowSorting.MaxLevels).ToList();
            SortChanged();
        }

        /// <summary>Уровни для расчёта: по параметру — только если столбец параметра выбран.</summary>
        private List<SortLevel> ActiveSort()
        {
            var list = _sortLevels.Where(l => l.Field != SortField.Param || HasParamColumn).ToList();
            return list.Count > 0 ? list : RowSorting.Default();
        }

        private void SortChanged()
        {
            _uiState.Sort = _sortLevels.Where(l => l.Field != SortField.Param)
                .Select(l => new SortLevelState { Field = l.Field.ToString(), Descending = l.Descending, Header = l.Header })
                .ToList();
            RequestUiSave?.Invoke();
            OnPropertyChanged(nameof(SortLevels));
            OnPropertyChanged(nameof(HeaderLevelCount));
            OnPropertyChanged(nameof(SortSummary));
            ScheduleFilter(0);
        }

        private void ToggleGroup(ChangeRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.GroupKey)) return;
            if (!_expandedGroups.Remove(row.GroupKey)) _expandedGroups.Add(row.GroupKey);
            _pendingSelectRecordId = row.IsGroupHeader ? null : row.Record.RecordId;
            _pendingSelectGroupKey = row.IsGroupHeader ? row.GroupKey : null;
            ScheduleFilter(0);
        }

        /// <summary>Двойной щелчок по строке: операцию — раскрыть или свернуть, запись — показать в модели.</summary>
        private void RowActivated()
        {
            var row = SelectedRow;
            if (row == null) return;
            if (row.IsGroupHeader) ToggleGroup(row);
            else if (row.CanShowInModel) ShowInModel();
        }

        private bool _isMoreOpen;
        /// <summary>Открыта строка «Ещё» с редкими фильтрами.</summary>
        public bool IsMoreOpen { get => _isMoreOpen; set => Set(ref _isMoreOpen, value); }

        private string _moreText = "Ещё";
        /// <summary>«Ещё» или «Ещё (2)» — сколько редких фильтров изменено.</summary>
        public string MoreText { get => _moreText; private set => Set(ref _moreText, value); }

        // ── фильтр: кнопка «Фильтр» и «Равно / Не равно» из меню ячейки ──

        private RecordFilter _filter = RecordFilter.Empty;
        public RecordFilter CurrentFilter => _filter;
        /// <summary>«Фильтр» или «Фильтр · 3» — на кнопке.</summary>
        public string FilterSummary => _filter.IsEmpty ? "Фильтр" : "Фильтр · " + _filter.Conditions.Count;
        public bool HasFilter => !_filter.IsEmpty;
        public event Action FilterDialogRequested;

        public void SetFilter(RecordFilter filter)
        {
            _filter = filter ?? RecordFilter.Empty;
            OnPropertyChanged(nameof(CurrentFilter));
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(HasFilter));
            FiltersChanged();
        }

        /// <summary>Меню ячейки «Равно: Категория «Стены»» / «Не равно: …» — условие фильтра.</summary>
        public void AddCellCondition(FilterField field, string value, bool notEquals)
        {
            if (field == FilterField.Author && !notEquals)
            {
                // «Автор равно» — то же, что выбрать автора в списке
                var name = Authors.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
                if (name != null) { SelectedAuthor = name; return; }
            }
            var c = new FilterCondition(field, notEquals ? FilterOperator.NotEquals : FilterOperator.Equals, value);
            if (!c.IsComplete) return;
            // такое же или противоположное условие по тому же полю и значению заменяется; «равно» — одно на поле
            var list = _filter.Conditions.Where(x => !(x.Field == field &&
                                                       (x.Operator == FilterOperator.Equals || x.Operator == FilterOperator.NotEquals) &&
                                                       (string.Equals(x.Value, c.Value, StringComparison.CurrentCultureIgnoreCase) ||
                                                        (!notEquals && x.Operator == FilterOperator.Equals))))
                .ToList();
            // меню ячейки сужает список — «И»; если фильтр был «ИЛИ», он переключается
            var wasAny = _filter.Any && list.Count > 0;
            list.Add(c);
            SetFilter(new RecordFilter(list, false));
            if (wasAny) Status = "Условие добавлено, фильтр переключён на «И»: так условие из меню сужает список.";
        }

        /// <summary>«Только созданные» / «Кроме удалённых» из меню ячейки «Действие».</summary>
        public void FilterByKind(ChangeKind kind, bool exclude)
        {
            if (kind == ChangeKind.Event) return;
            if (exclude)
                SetKinds(_showCreated && kind != ChangeKind.Created, _showModified && kind != ChangeKind.Modified,
                    _showDeleted && kind != ChangeKind.Deleted);
            else
                SetKinds(kind == ChangeKind.Created, kind == ChangeKind.Modified, kind == ChangeKind.Deleted);
        }

        /// <summary>«Только тип объекта: Виды» из меню ячейки.</summary>
        public void FilterByGroup(ObjectGroup group)
        {
            var option = GroupOptions.FirstOrDefault(o => o.Value == group);
            if (option != null) SelectedGroup = option;
        }

        // ── «Следить за выделением» ──

        private bool _followSelection;
        /// <summary>Список сам показывает историю того, что выделено в Revit.</summary>
        public bool FollowSelection
        {
            get => _followSelection;
            set
            {
                if (!Set(ref _followSelection, value)) return;
                _uiState.FollowSelection = value;
                RequestUiSave?.Invoke();
                if (value) FollowCard();
                else if (_elementFilterFromSelection) SetElementFilter(null, null);
            }
        }

        private bool _elementFilterFromSelection;

        /// <summary>Что панель сама выделила в Revit («Показать в модели», «Выделить все»): это не повод менять список.</summary>
        private HashSet<long> _ownSelection;
        private DateTime _ownSelectionAt;

        private void RememberOwnSelection(IEnumerable<long> ids)
        {
            _ownSelection = new HashSet<long>(ids);
            _ownSelectionAt = DateTime.UtcNow;
        }

        private bool IsOwnSelection(SelectionCard card)
        {
            if (card == null || _ownSelection == null) return false;
            if ((DateTime.UtcNow - _ownSelectionAt).TotalSeconds > 10) return false;
            if (card.Ids.Count == 0 || !card.Ids.All(_ownSelection.Contains)) return false;
            _ownSelection = null; // отметка срабатывает один раз: следующее выделение — уже пользователя
            return true;
        }

        private void FollowCard()
        {
            if (Card == null || Card.Ids.Count == 0)
            {
                if (_elementFilterFromSelection) SetElementFilter(null, null);
                return;
            }
            var label = Card.Count == 1
                ? $"Выделено в Revit: {RecordFilter.Short(Card.Name, 30)} (ID {Card.Id})"
                : $"Выделено в Revit: {Card.Count}";
            SetElementFilter(Card.Ids, label, fromSelection: true);
        }

        // ── включённые фильтры («чипы» с крестиком) ──

        public ObservableCollection<FilterChip> Chips { get; } = new ObservableCollection<FilterChip>();

        private bool _hasChips;
        public bool HasChips { get => _hasChips; private set => Set(ref _hasChips, value); }

        private void FiltersChanged()
        {
            RebuildChips();
            ScheduleFilter(0);
        }

        /// <summary>Пересобрать строку включённых фильтров: каждый можно снять крестиком.</summary>
        private void RebuildChips()
        {
            var list = new List<FilterChip>();
            var changes = !_eventsView;
            if (changes && _elementFilter != null)
                list.Add(new FilterChip(_elementFilterText, _elementFilterFromSelection
                    ? "Перестать следить за выделением и показать все элементы"
                    : "Показать все элементы", () =>
                {
                    if (_elementFilterFromSelection) FollowSelection = false;
                    SetElementFilter(null, null);
                }));
            if (_selectedAuthor != null && _selectedAuthor != AllAuthors)
                list.Add(new FilterChip("Автор: " + _selectedAuthor, null, () => SelectedAuthor = AllAuthors));
            if (changes && !AllKinds)
                list.Add(new FilterChip("Действие: " + KindSummary, null, SetAllKinds));
            if (changes && !_filter.IsEmpty)
            {
                var filter = _filter;
                if (filter.Any && filter.Conditions.Count > 1)
                    list.Add(new FilterChip(RecordFilter.Short("Фильтр: " + string.Join(" ИЛИ ", filter.Conditions.Select(x => x.Text)), 120),
                        "Снять фильтр (условия — в окне «Фильтр»)", () => SetFilter(RecordFilter.Empty)));
                else
                    foreach (var cnd in filter.Conditions)
                        list.Add(new FilterChip(cnd.Text, null, () => SetFilter(_filter.Without(cnd))));
            }
            var more = 0;
            if (changes && _selectedGroup?.Value != null)
            {
                more++;
                list.Add(new FilterChip("Тип объекта: " + _selectedGroup.Label, null, () => SelectedGroup = GroupOptions[0]));
            }
            if (changes && !_hideDuplicates)
            {
                more++;
                list.Add(new FilterChip("С дублями", "Показываются и записи «Синхронизация», которые автор уже записал сам", () => HideDuplicates = true));
            }
            // «Скрыть не попавшие» и «Скрыть отменённые» включены по умолчанию — значок, только когда их выключили
            if (changes && !_hideVoid)
            {
                more++;
                list.Add(new FilterChip(_modelIsWorkshared ? "Показаны не попавшие в хранилище" : "Показаны закрытые без сохранения",
                    "Снова скрыть изменения, которых фактически не было", () => HideVoid = true));
            }
            if (changes && !_hideUndone)
            {
                more++;
                list.Add(new FilterChip("Показаны отменённые (Ctrl+Z)", "Снова скрыть пары «правка + её отмена»", () => HideUndone = true));
            }
            if (changes && _showExcluded)
            {
                more++;
                list.Add(new FilterChip("Показаны убранные", null, () => ShowExcluded = false));
            }
            if (changes && _showNoise)
            {
                more++;
                list.Add(new FilterChip("Служебные элементы", null, () => ShowNoise = false));
            }
            MoreText = more > 0 ? $"Ещё ({more})" : "Ещё";
            if (Chips.Select(c => c.Text).SequenceEqual(list.Select(c => c.Text)))
            {
                HasChips = Chips.Count > 0;
                return;
            }
            Chips.Clear();
            foreach (var c in list) Chips.Add(c);
            HasChips = Chips.Count > 0;
        }

        private HashSet<long> _elementFilter;
        /// <summary>История вида: имя вида-владельца — показать и элементы, нарисованные на нём.</summary>
        private string _elementFilterOwnerView;
        private string _elementFilterText = "";
        public string ElementFilterText { get => _elementFilterText; private set => Set(ref _elementFilterText, value); }
        public bool HasElementFilter => _elementFilter != null;

        private SelectionCard _card;
        public SelectionCard Card
        {
            get => _card;
            private set
            {
                _card = value != null && value.ModelKey == _modelKey ? value : null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCard));
                OnPropertyChanged(nameof(CardTitle));
                OnPropertyChanged(nameof(CardAuthors));
                OnPropertyChanged(nameof(CardHistoryText));
                RaiseCommands();
            }
        }

        public bool HasCard => Card != null;

        public string CardTitle
        {
            get
            {
                if (Card == null) return "";
                var head = Card.Count > 1 ? $"Выделено: {Card.Count}. Первый: " : "Выделен: ";
                return head + $"{Card.Name} — {Card.Category} {(string.IsNullOrEmpty(Card.TypeName) ? "" : "· " + Card.TypeName)} (ID {Card.Id})";
            }
        }

        public string CardAuthors
        {
            get
            {
                if (Card == null) return "";
                if (!Card.IsWorkshared) return "Модель без совместной работы: автор известен только из журнала.";
                return $"Создал: {Dash(Card.Creator)} · Последним синхронизировал: {Dash(Card.LastChangedBy)} · Сейчас занят: {Dash(Card.Owner)}";
            }
        }

        public string CardHistoryText
        {
            get
            {
                if (Card == null) return "";
                var ids = new HashSet<long>(Card.Ids);
                var n = _all.Count(r => ids.Contains(r.ElementId));
                // «записей нет» — только за выбранный период: элемент могли менять раньше
                return n == 0 ? $"За период «{SelectedPeriod?.Label}» записей нет" : $"История в журнале: {n}";
            }
        }

        // команды
        public RelayCommand RefreshCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand ShowInModelCommand { get; }
        public RelayCommand HistoryCommand { get; }
        public RelayCommand CopyIdCommand { get; }
        public RelayCommand SelectAllInModelCommand { get; }
        public RelayCommand CardHistoryCommand { get; }
        public RelayCommand ClearElementFilterCommand { get; }
        public RelayCommand ResetFiltersCommand { get; }
        public RelayCommand SettingsCommand { get; }
        public RelayCommand ActualizeCommand { get; }
        public RelayCommand ConfirmPurgeCommand { get; }
        public RelayCommand CancelPurgeCommand { get; }
        public RelayCommand StopActualizeCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand OpenLogFolderCommand { get; }
        public RelayCommand ShowIn3DCommand { get; }
        public RelayCommand CloseChangesViewCommand { get; }

        // ───────────────────────────── 3D-вид изменений ─────────────────────────────

        /// <summary>Модель, для которой построен 3D-вид изменений (легенда относится к ней).</summary>
        private string _changesViewKey;

        private bool _hasChangesView;
        public bool HasChangesView
        {
            get => _hasChangesView;
            private set { if (Set(ref _hasChangesView, value)) CloseChangesViewCommand?.RaiseCanExecuteChanged(); }
        }

        private string _changesViewTitle = "";
        public string ChangesViewTitle { get => _changesViewTitle; private set => Set(ref _changesViewTitle, value); }

        private string _changesViewNote = "";
        /// <summary>Чего на виде нет и почему: удалённые, «изменили и вернули», сводные строки…</summary>
        public string ChangesViewNote { get => _changesViewNote; private set { if (Set(ref _changesViewNote, value)) OnPropertyChanged(nameof(HasChangesViewNote)); } }
        public bool HasChangesViewNote => !string.IsNullOrEmpty(_changesViewNote);

        private IReadOnlyList<ChangesLegendItem> _changesViewLegend = new List<ChangesLegendItem>();
        public IReadOnlyList<ChangesLegendItem> ChangesViewLegend { get => _changesViewLegend; private set => Set(ref _changesViewLegend, value); }

        /// <summary>
        /// Кнопка «3D»: журнал модели целиком — в фоне (авторы для окна — за всё время, дубли убираются без
        /// границы периода), окно настроек (заполнено фильтрами панели) → итог за период → вид в Revit.
        /// </summary>
        private void ShowIn3D()
        {
            var key = _modelKey;
            if (key == null) return;
            var me = _tracker.CurrentUser ?? "";
            var panelAuthor = _selectedAuthor == null || _selectedAuthor == AllAuthors ? null
                : _selectedAuthor == UnknownAuthor ? "" : _selectedAuthor;
            var period = SelectedPeriod?.Value ?? 7;
            var created = _showCreated;
            var modified = _showModified;
            var root = DataFolders.LogFolder;
            var legacy = ReadKeys;
            var localExcluded = new HashSet<string>(_localExcluded, StringComparer.Ordinal);
            var localRestored = new HashSet<string>(_localRestored, StringComparer.Ordinal);
            var style = (_uiState.ChangesStyle ?? new ChangesViewStyle()).Clone();
            var lastSync = LastMySync(key);
            Status = "3D-вид изменений: читаю журнал модели…";
            Task.Run(() =>
            {
                List<ChangeRecord> records;
                try
                {
                    records = new LogReader(root, key, null, legacy, me).ReadNew();
                }
                catch (Exception ex)
                {
                    Diag.Log("Changes view read failed", ex);
                    Post(() => Status = "Не удалось прочитать журнал: " + ex.Message);
                    return;
                }
                var authors = records.Where(r => r.Kind != ChangeKind.Event).Select(r => r.User ?? "")
                    .Concat(new[] { me })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(u => u, StringComparer.CurrentCultureIgnoreCase)
                    .Select(u => new KeyValuePair<string, string>(string.IsNullOrEmpty(u) ? UnknownAuthor : u, u))
                    .ToList();
                Post(() =>
                {
                    Status = "";
                    _actions.ShowChangesView(key,
                        levels =>
                        {
                            var dialog = new ChangesViewDialogModel(levels, authors, period, panelAuthor, created, modified, style, lastSync);
                            if (AskChangesView == null || !AskChangesView(dialog)) return null;
                            _uiState.ChangesStyle = dialog.ChosenStyle; // цвета запоминаются до следующего раза
                            RequestUiSave?.Invoke();
                            return dialog.Result;
                        },
                        request => ComputeChanges(records, me, lastSync, localExcluded, localRestored, request),
                        msg => Post(() => Status = msg),
                        result => Post(() => OnChangesViewBuilt(key, result)));
                });
            });
        }

        /// <summary>Последняя синхронизация текущего пользователя с моделью (запоминается при синхронизации).</summary>
        private static DateTimeOffset? LastMySync(string modelKey) => LocalState.GetLastCommit(modelKey);

        /// <summary>Запись о том, что текущий пользователь получил при синхронизации (или обновлении) — не его правка.</summary>
        private static bool IsReceivedBy(ChangeRecord r, string me) =>
            (r.Source == ChangeSource.Sync || r.Source == ChangeSource.Reload) &&
            string.Equals(r.RecordedBy, me, StringComparison.OrdinalIgnoreCase);

        /// <summary>Весь журнал модели → итог по элементам за период (фоновый поток, без Revit API).</summary>
        private static PeriodDiffResult ComputeChanges(List<ChangeRecord> records, string me, DateTimeOffset? lastSync,
            HashSet<string> localExcluded, HashSet<string> localRestored, ChangesViewRequest request)
        {
            if (request.SinceMyLastSync)
            {
                // время синхронизации запоминается на этом компьютере; с другого — найдётся по журналу
                var inJournal = records.Where(r => r.Kind == ChangeKind.Event && r.EventType == ModelEventType.Commit &&
                                                   string.Equals(r.User, me, StringComparison.OrdinalIgnoreCase))
                    .Select(r => (DateTimeOffset?)r.Time).DefaultIfEmpty(null).Max();
                var from = new[] { lastSync, inJournal }.Max();
                request.From = from ?? new DateTimeOffset(DateTime.Today);
                request.PeriodText = from.HasValue
                    ? "с вашей синхронизации " + from.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru)
                    : "сегодня (ваших синхронизаций не найдено)";
            }
            // не считаются: убранные при актуализации, не попавшие в хранилище (изменения фактически не было),
            // дубли (то же изменение, полученное коллегой при синхронизации, — со временем получения)
            var statuses = SyncStatusCalculator.Compute(records);
            var excluded = Excluded(records, localExcluded, localRestored);
            var clean = JournalDedup.WithoutDuplicates(records, excluded);
            var sinceSync = request.SinceMyLastSync;
            return PeriodDiff.Compute(clean, request.From, request.To, request.Authors,
                r => (r.RecordId != null && excluded.Contains(r.RecordId)) ||
                     (statuses.TryGetValue(r, out var st) && SyncStatusCalculator.IsVoid(st)) ||
                     // «с моей синхронизации»: полученное при ней старше неё — это не новые изменения
                     (sinceSync && IsReceivedBy(r, me)));
        }

        private void OnChangesViewBuilt(string key, ChangesViewResult result)
        {
            if (!result.Shown)
            {
                Status = result.Message;
                return;
            }
            _changesViewKey = key;
            ChangesViewTitle = "3D-вид изменений · " + result.Title;
            ChangesViewLegend = result.Legend.Select(e => new ChangesLegendItem(e)).ToList();
            var notes = new List<string>();
            if (result.Deleted > 0) notes.Add(string.Format(Ru, "удалено {0:N0} — на виде их не показать", result.Deleted));
            if (result.NoNetHidden > 0) notes.Add(string.Format(Ru, "без отличий в параметрах {0:N0} — не показаны (флажок в окне «3D»)", result.NoNetHidden));
            if (result.Uncertain > 0) notes.Add(string.Format(Ru, "у {0:N0} изменённых «было» неизвестно — показаны как изменённые", result.Uncertain));
            if (result.SeeThrough > 0) notes.Add(string.Format(Ru, "{0:N0} — полупрозрачные: внутри них другие изменения", result.SeeThrough));
            if (result.NotInModel > 0) notes.Add(string.Format(Ru, "{0:N0} уже нет в модели", result.NotInModel));
            if (result.NotDrawable > 0) notes.Add(string.Format(Ru, "{0:N0} — виды, типы, аннотации: в 3D не показываются", result.NotDrawable));
            if (result.Summary > 0) notes.Add(string.Format(Ru, "{0:N0} — в сводных строках массовых операций, по отдельности неизвестны", result.Summary));
            if (result.Capped > 0) notes.Add(string.Format(Ru, "показаны последние {0:N0}, ещё {1:N0} — нет", ChangesView3D.MaxElements, result.Capped));
            ChangesViewNote = (notes.Count == 0 ? "" : char.ToUpper(notes[0][0], Ru) + string.Join(" · ", notes).Substring(1) + ". ") +
                              "Закройте вкладку вида до синхронизации — иначе вид уйдёт в хранилище и его увидят коллеги.";
            HasChangesView = true;
            Status = string.Format(Ru, "3D-вид изменений построен: показано элементов — {0:N0}. Закройте вкладку вида, когда он не нужен, — он удалится.", result.Colored);
        }

        private void CloseChangesView()
        {
            var key = _changesViewKey ?? _modelKey;
            if (key == null) return;
            _actions.CloseChangesView(key, msg => Post(() => Status = msg));
        }

        private void HideChangesView()
        {
            HasChangesView = false;
            _changesViewKey = null;
            ChangesViewLegend = new List<ChangesLegendItem>();
            ChangesViewNote = "";
        }
        /// <summary>«Все» в списке «Действие».</summary>
        public RelayCommand AllKindsCommand { get; }
        /// <summary>Кнопка «Сортировка…».</summary>
        public RelayCommand OpenSortCommand { get; }
        public RelayCommand OpenFilterCommand { get; }
        /// <summary>Двойной щелчок по строке.</summary>
        public RelayCommand RowActivatedCommand { get; }
        /// <summary>▸ / ▾ у операции.</summary>
        public RelayCommand ToggleGroupCommand { get; }
        /// <summary>«Удалён вместе с: …» — к строке удаления родителя.</summary>
        public RelayCommand GoToParentCommand { get; }

        // ───────────────────────────── актуализация журнала ─────────────────────────────

        private bool _isActualizing;
        public bool IsActualizing
        {
            get => _isActualizing;
            private set
            {
                if (!Set(ref _isActualizing, value)) return;
                ActualizeCommand?.RaiseCanExecuteChanged();
                StopActualizeCommand?.RaiseCanExecuteChanged();
                OnAuditModeChanged();
                if (!value) ActualizeText = "";
            }
        }

        /// <summary>
        /// Режим актуализации: показан список несоответствий. Фильтры журнала в это время скрыты — в таблице только найденные записи.
        /// Пока идёт проверка, журнал и фильтры остаются как есть, сверху — полоса хода проверки.
        /// </summary>
        public bool IsAuditMode => HasPurgePreview;
        public bool IsNormalMode => !IsAuditMode;

        private void OnAuditModeChanged()
        {
            OnPropertyChanged(nameof(IsAuditMode));
            OnPropertyChanged(nameof(IsNormalMode));
        }

        private string _confirmPurgeText = "Убрать из журнала";
        /// <summary>«Убрать из журнала (N)».</summary>
        public string ConfirmPurgeText { get => _confirmPurgeText; private set => Set(ref _confirmPurgeText, value); }

        /// <summary>Кнопка «Остановить» во время проверки.</summary>
        private void StopActualize()
        {
            var cts = _actualizeCts;
            if (cts == null) return;
            cts.Cancel();
            FinishActualize(cts, "Актуализация остановлена — журнал не изменён.");
        }

        private string _actualizeText = "";
        public string ActualizeText { get => _actualizeText; private set => Set(ref _actualizeText, value); }

        /// <summary>Предпросмотр: записи, которые не соответствуют модели, и почему.</summary>
        private Dictionary<string, string> _previewReasons;
        private List<ChangeRecord> _previewRecords;
        /// <summary>Записи «✗ не попало в хранилище» в предпросмотре: убираются без сверки с моделью.</summary>
        private HashSet<string> _previewLost = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<ChangeRecord, SyncStatus> _previewStatuses;
        private string _previewModelKey;
        /// <summary>Убранные в этом сеансе (до того, как отметка вернётся из журнала).</summary>
        private readonly HashSet<string> _localExcluded = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Возвращённые в журнал только что (до перечитывания журнала).</summary>
        private readonly HashSet<string> _localRestored = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Растёт при каждом изменении _localExcluded / _localRestored — расчёты по журналу пора обновить.</summary>
        private int _localVersion;

        /// <summary>Убранные при актуализации с учётом убранных и возвращённых только что.</summary>
        private static HashSet<string> Excluded(IEnumerable<ChangeRecord> records, ISet<string> localExcluded, ISet<string> localRestored)
        {
            var excluded = JournalAudit.ExcludedIds(records);
            excluded.UnionWith(localExcluded);
            excluded.ExceptWith(localRestored);
            return excluded;
        }

        /// <summary>Правый щелчок по убранной записи → «Вернуть в журнал»: у всех пользователей.</summary>
        public void RestoreRecords(ChangeRow row)
        {
            var key = _modelKey;
            if (key == null || row == null) return;
            var ids = (row.IsGroupHeader ? row.Members : new List<ChangeRecord> { row.Record })
                .Where(r => r.RecordId != null && _journal?.Excluded.Contains(r.RecordId) == true)
                .Select(r => r.RecordId).Distinct().ToList();
            if (ids.Count == 0) return;
            if (!_tracker.WriteRestore(key, ids))
            {
                Status = "Не удалось вернуть: модель журнала не открыта в Revit.";
                return;
            }
            _localExcluded.ExceptWith(ids);
            _localRestored.UnionWith(ids);
            _localVersion++;
            Status = $"Возвращено в журнал записей — {ids.Count}. Отметка сохранена в журнале: записи вернутся и у коллег.";
            ScheduleFilter(0);
        }

        public bool HasPurgePreview => _previewReasons != null;

        private string _purgeText = "";
        public string PurgeText { get => _purgeText; private set => Set(ref _purgeText, value); }

        private bool _showExcluded;
        /// <summary>Показывать записи, убранные при актуализации.</summary>
        public bool ShowExcluded { get => _showExcluded; set { if (Set(ref _showExcluded, value)) FiltersChanged(); } }

        /// <summary>Кнопка на ленте: актуализировать журнал активной модели.</summary>
        private System.Threading.CancellationTokenSource _actualizeCts;
        private string _actualizeKey;

        /// <summary>Сверить журнал с текущей моделью (кнопка «Актуализировать»).</summary>
        public void StartActualize()
        {
            if (string.IsNullOrEmpty(_modelKey))
            {
                Status = "Откройте модель, журнал которой нужно актуализировать.";
                return;
            }
            if (IsActualizing)
            {
                Status = "Актуализация уже идёт.";
                return;
            }
            if (HasPurgePreview)
            {
                Status = "Список несоответствий уже показан — уберите записи или нажмите «Отмена».";
                return;
            }
            var key = _modelKey;
            // с этого момента изменённые элементы не проверяются: их записи могут не успеть попасть в журнал
            if (!_tracker.BeginAuditWatch(key, out var centralUpToDate))
            {
                Status = "Модель журнала не открыта в Revit — актуализация невозможна.";
                return;
            }
            _actualizeCts?.Cancel();
            var cts = _actualizeCts = new System.Threading.CancellationTokenSource();
            _actualizeKey = key;
            IsActualizing = true;
            ActualizeText = "Актуализация: читаю весь журнал модели…";
            var root = DataFolders.LogFolder;
            var legacyKeys = ReadKeys;
            var store = _tracker.Store;
            var localExcluded = new HashSet<string>(_localExcluded, StringComparer.Ordinal);
            var localRestored = new HashSet<string>(_localRestored, StringComparer.Ordinal);
            var workshared = _modelIsWorkshared;
            Task.Run(() =>
            {
                try
                {
                    store.Flush(); // свои последние записи — в журнал до чтения
                    var all = new LogReader(root, key, null, legacyKeys).ReadNew(); // вся история, не только выбранный период
                    var statuses = SyncStatusCalculator.Compute(all);
                    var ctx = JournalAudit.BuildContext(all, statuses, centralUpToDate);
                    var excluded = Excluded(all, localExcluded, localRestored);
                    // «✗ не попало в хранилище» — без сверки с моделью; сверяются только старые записи без статуса
                    var lost = JournalAudit.VoidFindings(all, ctx, excluded, workshared);
                    var checks = JournalAudit.BuildChecks(all, ctx, excluded);
                    Post(() =>
                    {
                        if (cts.IsCancellationRequested || key != _modelKey) return;
                        if (checks.Count == 0)
                        {
                            OnActualized(key, cts, lost, ctx, 0, lost);
                            return;
                        }
                        ActualizeText = $"Актуализация: сверяю с моделью элементы (0 из {checks.Count})…";
                        var started = _tracker.StartActualize(key, checks, ctx, cts.Token,
                            (i, n) => Post(() =>
                            {
                                if (!cts.IsCancellationRequested) ActualizeText = $"Актуализация: сверяю с моделью элементы ({i} из {n})…";
                            }),
                            findings => Post(() => OnActualized(key, cts, findings == null ? null : lost.Concat(findings).ToList(), ctx,
                                checks.Count, lost)));
                        if (!started) FinishActualize(cts, "Модель журнала не открыта в Revit — актуализация невозможна.");
                    });
                }
                catch (Exception ex)
                {
                    Diag.Log("Actualize failed", ex);
                    Post(() => FinishActualize(cts, "Не удалось актуализировать журнал: " + ex.Message));
                }
            });
        }

        /// <summary>Актуализация закончилась без предпросмотра (нечего убирать, ошибка, отмена).</summary>
        private void FinishActualize(System.Threading.CancellationTokenSource cts, string status)
        {
            if (!ReferenceEquals(cts, _actualizeCts)) return; // уже начата другая или отменена
            _tracker.EndAuditWatch(_actualizeKey, out _);
            _actualizeCts = null;
            _actualizeKey = null;
            IsActualizing = false;
            if (status != null) Status = status;
        }

        /// <summary>Прервать актуализацию и убрать предпросмотр (смена модели).</summary>
        private void AbortActualize()
        {
            if (_actualizeCts != null)
            {
                _actualizeCts.Cancel();
                _tracker.EndAuditWatch(_actualizeKey, out _);
                _actualizeCts = null;
                _actualizeKey = null;
                IsActualizing = false;
            }
            if (_previewModelKey != null) _tracker.EndAuditWatch(_previewModelKey, out _);
            _previewReasons = null;
            _previewRecords = null;
            _previewStatuses = null;
            _previewModelKey = null;
            PurgeText = "";
            OnPropertyChanged(nameof(HasPurgePreview));
            OnAuditModeChanged();
            ActualizeCommand?.RaiseCanExecuteChanged();
            ConfirmPurgeCommand?.RaiseCanExecuteChanged();
            CancelPurgeCommand?.RaiseCanExecuteChanged();
        }

        /// <param name="lost">Записи «✗ не попало в хранилище» (убираются без сверки с моделью).</param>
        private void OnActualized(string key, System.Threading.CancellationTokenSource cts, List<AuditFinding> findings, AuditContext ctx,
            int checkedCount, List<AuditFinding> lost)
        {
            if (!ReferenceEquals(cts, _actualizeCts) || key != _modelKey) return;
            var legacyNote = ctx.LegacySkipped == 0 ? ""
                : ctx.CentralUpToDate == null
                    ? $" Не проверено старых записей без сведений о синхронизации: {ctx.LegacySkipped}. " +
                      "Чтобы проверить их, синхронизируйтесь или обновите модель до последней версии и повторите актуализацию."
                    : $" Не проверено старых записей без сведений о синхронизации: {ctx.LegacySkipped} — " +
                      "их авторы, возможно, ещё не синхронизировали эти изменения.";
            if (findings == null)
            {
                FinishActualize(cts, "Актуализация прервана: модель закрыта или произошла ошибка (подробности — в журнале ошибок, «О плагине»).");
                return;
            }
            findings = findings.Where(f => !string.IsNullOrEmpty(f.Record.RecordId)).ToList();
            if (findings.Count == 0)
            {
                FinishActualize(cts, (checkedCount == 0
                    ? "Журнал соответствует модели: записей, требующих проверки, нет."
                    : $"Журнал соответствует модели: проверено элементов — {checkedCount}, несоответствий нет.") + legacyNote);
                return;
            }
            // предпросмотр: отслеживание изменений продолжается до подтверждения
            _actualizeCts = null;
            _actualizeKey = null;
            IsActualizing = false;
            _previewModelKey = key;
            _previewReasons = findings
                .GroupBy(f => f.Record.RecordId).ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);
            _previewRecords = findings.Select(f => f.Record).Distinct().ToList();
            _previewLost = new HashSet<string>(lost.Select(f => f.Record.RecordId).Where(id => id != null), StringComparer.Ordinal);
            _previewStatuses = ctx.Statuses;
            var lostCount = _previewReasons.Keys.Count(_previewLost.Contains);
            var checkedFound = _previewReasons.Count - lostCount;
            var parts = new List<string>();
            if (lostCount > 0) parts.Add($"не попали в хранилище — {lostCount}");
            if (checkedFound > 0) parts.Add($"не соответствуют модели — {checkedFound}");
            PurgeText = $"Записей об изменениях, которых в модели нет: {string.Join(", ", parts)} (причина — в столбце «Причина»). " +
                        "Убрать их из журнала у всех пользователей? " +
                        "Вернуть можно позже: «Ещё» → «Показать убранные» → правый щелчок → «Вернуть в журнал».";
            ConfirmPurgeText = $"Убрать из журнала ({_previewReasons.Count})";
            if (legacyNote.Length > 0) Status = legacyNote.Trim();
            SetView(false); // несоответствия — это записи об элементах: показываем их на вкладке «Изменения»
            OnPreviewChanged();
        }

        private void ConfirmPurge()
        {
            if (_previewReasons == null) return;
            // элементы, изменённые после проверки, не трогаем: их записи сверялись со старым состоянием
            var touched = _tracker.EndAuditWatch(_previewModelKey, out var mass);
            if (touched == null || mass)
            {
                Status = touched == null
                    ? "Модель журнала закрыта — журнал не изменён."
                    : "Пока список был открыт, в модели прошла массовая операция — журнал не изменён. Повторите актуализацию.";
                ClearPreview(false);
                return;
            }
            // записи «✗» от модели не зависят — их убираем, даже если элемент с тех пор правили
            var ids = _previewRecords.Where(r => _previewLost.Contains(r.RecordId) || !touched.Contains(r.ElementId))
                .Select(r => r.RecordId).Distinct().ToList();
            var skipped = _previewReasons.Count - ids.Count;
            var skippedNote = skipped > 0 ? $" Не тронуто записей — {skipped}: их элементы изменились, пока список был открыт." : "";
            if (ids.Count == 0)
            {
                Status = "Журнал не изменён: все элементы из списка изменились, пока он был открыт. Повторите актуализацию.";
            }
            else if (_tracker.WriteExclusion(_previewModelKey, ids))
            {
                _localRestored.ExceptWith(ids);
                _localExcluded.UnionWith(ids);
                _localVersion++;
                Status = $"Актуализация: убрано из журнала записей — {ids.Count}. Отметка сохранена в журнале. " +
                         "Вернуть можно: «Ещё» → «Показать убранные» → правый щелчок по записи → «Вернуть в журнал»." + skippedNote;
            }
            else
            {
                Status = "Не удалось записать отметку: модель журнала не открыта.";
            }
            ClearPreview(false);
        }

        private void CancelPurge()
        {
            Status = "Актуализация отменена — журнал не изменён.";
            ClearPreview(true);
        }

        private void ClearPreview(bool endWatch)
        {
            if (endWatch && _previewModelKey != null) _tracker.EndAuditWatch(_previewModelKey, out _);
            _previewReasons = null;
            _previewRecords = null;
            _previewStatuses = null;
            _previewModelKey = null;
            PurgeText = "";
            OnPreviewChanged();
        }

        private void OnPreviewChanged()
        {
            OnPropertyChanged(nameof(HasPurgePreview));
            OnPropertyChanged(nameof(HeaderLevelCount));
            OnAuditModeChanged();
            ActualizeCommand?.RaiseCanExecuteChanged();
            ConfirmPurgeCommand?.RaiseCanExecuteChanged();
            CancelPurgeCommand?.RaiseCanExecuteChanged();
            ScheduleFilter(0);
        }

        // ───────────────────────────── модель и чтение ─────────────────────────────

        public void SetPaneVisible(bool visible)
        {
            _paneVisible = visible;
            if (visible)
            {
                if (_reader == null && _tracker.Active != null) SetModel(_tracker.Active);
                else if (_reader != null) RequestRead();
            }
        }

        private void SetModel(DocumentTracker t)
        {
            var key = t?.Model.Key;
            if (key == _modelKey && _reader != null) return;
            _modelKey = key;
            if (_changesViewKey != null && key != _changesViewKey) HideChangesView(); // легенда относится к другой модели
            _legacyKeys = t?.Model.LegacyKeys ?? Array.Empty<string>();
            _linkedKeys = Array.Empty<string>();
            _modelPath = t?.Model.Path;
            JournalCandidate = null;
            AbortActualize(); // актуализация и предпросмотр относятся к прежней модели
            _elementFilter = null; // фильтр «история элементов» относится к прежней модели
            ElementFilterText = "";
            _elementFilterFromSelection = false;
            _expandedGroups.Clear();
            OnPropertyChanged(nameof(HasElementFilter));
            RebuildChips();
            OnPropertyChanged(nameof(ModelKey));
            OnPropertyChanged(nameof(HasModel));
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(EmptyHint));
            if (t == null)
            {
                ModelTitle = "Нет открытой модели";
                ModelInfo = "";
                ModelMode = "";
                _reader = null;
                _all.Clear();
                _unknowns = new UnknownElementResolver();
                _allVersion++;
                Rows = new List<ChangeRow>();
                _flat = new List<ChangeRow>();
                SummaryText = "";
                HasStats = false;
                WarmupText = "";
                Card = null;
                RebuildChips();
                return;
            }
            ModelTitle = t.Model.Title;
            _modelIsWorkshared = t.Model.IsWorkshared;
            OnPropertyChanged(nameof(HideVoidLabel));
            HasNoiseRecords = false;
            _journalParamNames.Clear();
            _listedParamNames.Clear();
            ParamNames.Clear(); // смена модели — список параметров строится заново
            RefreshParamNames();
            UpdateModelInfo(t);
            UpdateWarmupText(t);
            Card = null;
            Reload();
        }

        private void UpdateModelInfo(DocumentTracker t)
        {
            var mode = t.Model.IsWorkshared ? "совместная работа" : "одиночный файл";
            ModelMode = mode;
            ModelInfo = t.Model.Title + " — " + mode + Environment.NewLine + "Журнал: " + DataFolders.LogFolder;
        }

        private void UpdateWarmupText(DocumentTracker t)
        {
            if (t == null || !ReferenceEquals(t, _tracker.Active)) return;
            if (!_settings.SnapshotOnOpen) { WarmupText = ""; return; }
            var pct = WarmupProgress.Percent(t.WarmupPhase, t.WarmupProgress, t.WarmupTotal, t.WarmupDone);
            WarmupPercent = pct;
            IsWarmupDone = t.WarmupDone;
            WarmupText = WarmupProgress.Text(pct, t.WarmupDone);
        }

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        /// <summary>Конец периода «Вчера» (не включая); null — до сейчас.</summary>
        private DateTimeOffset? _periodTo;
        /// <summary>«С моей последней синхронизации»: полученное при ней — не новые изменения, не показывается.</summary>
        private bool _periodSinceSync;
        /// <summary>Пояснение к периоду в строке состояния: «с вашей синхронизации 03.10 17:42».</summary>
        private string _periodNote = "";
        /// <summary>Живые записи раньше начала периода — для «Без дублей» (LogReader.LiveBefore).</summary>
        private List<string> _liveBefore = new List<string>();
        private List<long> _liveDeletedBefore = new List<long>();

        private string _folderProblem;
        /// <summary>Общая папка журналов недоступна: постоянное предупреждение под названием модели (null — всё в порядке).</summary>
        public string FolderProblem
        {
            get => _folderProblem;
            private set { if (Set(ref _folderProblem, value)) OnPropertyChanged(nameof(HasFolderProblem)); }
        }
        public bool HasFolderProblem => !string.IsNullOrEmpty(_folderProblem);

        private bool _hasNoiseRecords;
        /// <summary>В журнале есть служебные элементы — только тогда флажок «Служебные элементы» что-то меняет.</summary>
        public bool HasNoiseRecords
        {
            get => _hasNoiseRecords;
            private set { if (Set(ref _hasNoiseRecords, value)) OnPropertyChanged(nameof(ShowNoiseOption)); }
        }

        /// <summary>Полная перезагрузка журнала (смена модели / периода / настроек).</summary>
        public void Reload()
        {
            if (_tracker.Active != null && _tracker.Active.Model.Key != _modelKey)
            {
                SetModel(_tracker.Active);
                return;
            }
            if (string.IsNullOrEmpty(_modelKey)) return;
            if (_tracker.Active != null) UpdateModelInfo(_tracker.Active);
            var period = SelectedPeriod?.Value ?? 7;
            var today = new DateTimeOffset(DateTime.Today);
            DateTimeOffset? since;
            _periodTo = null;
            _periodSinceSync = false;
            _periodNote = "";
            switch (period)
            {
                case PeriodYesterday:
                    since = today.AddDays(-1);
                    _periodTo = today;
                    break;
                case PeriodSinceMySync:
                    var sync = LastMySync(_modelKey);
                    since = sync ?? today;
                    _periodSinceSync = true;
                    _periodNote = sync.HasValue
                        ? "с вашей синхронизации " + sync.Value.ToLocalTime().ToString("dd.MM HH:mm", Ru)
                        : "ваших синхронизаций не найдено — с начала дня";
                    break;
                default:
                    since = period > 0 ? today.AddDays(-(period - 1)) : (DateTimeOffset?)null;
                    break;
            }
            _reader = new LogReader(DataFolders.LogFolder, _modelKey, since, _legacyKeys, _tracker.CurrentUser);
            _all.Clear();
            _unknowns = new UnknownElementResolver();
            _liveBefore = new List<string>();
            _liveDeletedBefore = new List<long>();
            _allVersion++;
            Rows = new List<ChangeRow>();
            _flat = new List<ChangeRow>();
            SelectedRow = null;
            RebuildAuthors();
            RequestRead();
        }

        /// <summary>Дочитать новые строки журналов (в фоне).</summary>
        public void RequestRead()
        {
            if (_reader == null) return;
            if (Interlocked.Exchange(ref _readPending, 1) == 1) return; // чтение уже запрошено
            Task.Run(async () =>
            {
                await _readGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    Interlocked.Exchange(ref _readPending, 0);
                    // читатель берём ПОСЛЕ ожидания: за это время мог смениться период или модель
                    var reader = _reader;
                    if (reader == null) return;
                    var initial = !reader.HasRead;
                    if (initial) Post(() => IsLoading = true);
                    if (initial) PrepareLinks(reader);
                    var fresh = reader.ReadNew();
                    var error = reader.LastError ?? _tracker.Store.LastError;
                    reader.LiveBefore(out var liveBefore, out var liveDeletedBefore);
                    Post(() =>
                    {
                        if (initial) IsLoading = false;
                        if (!ReferenceEquals(reader, _reader)) return; // период или модель сменились
                        FolderProblem = error; // держится, пока связи с общей папкой нет; null — всё в порядке
                        if (fresh.Count == 0 && !initial) return;
                        _unknowns.Add(fresh); // до показа: «нет сведений» — из прежних записей журнала
                        _all.AddRange(fresh);
                        _employees.Add(fresh);
                        _liveBefore = liveBefore;
                        _liveDeletedBefore = liveDeletedBefore;
                        if (!_hasNoiseRecords && fresh.Any(r => r.IsNoise == true)) HasNoiseRecords = true;
                        _allVersion++;
                        CollectParamNames(fresh);
                        RebuildAuthors();
                        RefreshParamNames();
                        ScheduleFilter(0);
                        OnPropertyChanged(nameof(CardHistoryText));
                    });
                }
                catch (Exception ex)
                {
                    Diag.Log("Read failed", ex);
                    Post(() => { IsLoading = false; Status = "Ошибка чтения журнала: " + ex.Message; });
                }
                finally
                {
                    _readGate.Release();
                }
            });
        }

        private bool _rebuildingAuthors;

        private void RebuildAuthors()
        {
            var names = _all.Select(r => string.IsNullOrEmpty(r.User) ? UnknownAuthor : r.User)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = _selectedAuthor ?? AllAuthors;
            // выбранного автора сохраняем, даже если за новый период у него нет записей
            if (selected != AllAuthors && !names.Contains(selected, StringComparer.OrdinalIgnoreCase)) names.Add(selected);
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            if (Authors.Skip(1).SequenceEqual(names)) return;

            _rebuildingAuthors = true;
            try
            {
                Authors.Clear();
                Authors.Add(AllAuthors);
                foreach (var n in names) Authors.Add(n);
            }
            finally
            {
                _rebuildingAuthors = false;
            }
            _selectedAuthor = Authors.FirstOrDefault(a => string.Equals(a, selected, StringComparison.OrdinalIgnoreCase)) ?? AllAuthors;
            OnPropertyChanged(nameof(SelectedAuthor));
            RebuildChips();
        }

        // ───────────────────────────── фильтрация ─────────────────────────────

        private void ScheduleFilter(int delayMs = 300)
        {
            _filterTimer?.Dispose();
            if (delayMs <= 0)
            {
                ApplyFilter();
                return;
            }
            _filterTimer = new Timer(_ => Post(ApplyFilter), null, delayMs, Timeout.Infinite);
        }

        /// <summary>Выбрать эту запись после ближайшего обновления списка (например, после раскрытия операции).</summary>
        private string _pendingSelectRecordId;
        private string _pendingSelectGroupKey;
        /// <summary>Статусы и убранные записи последнего расчёта — для вкладки «История».</summary>
        private Dictionary<ChangeRecord, SyncStatus> _lastStatuses;
        private HashSet<string> _lastExcluded;

        private void ApplyFilter()
        {
            var version = Interlocked.Increment(ref _filterVersion);
            if (_previewReasons != null)
            {
                // предпросмотр актуализации: только найденные несоответствия, по одной записи
                var ctx = MakeRowContext();
                ctx.Statuses = _previewStatuses;
                ctx.PreviewReasons = _previewReasons;
                var keep = SelectedRow?.Record.RecordId;
                var ordered = _previewRecords.OrderByDescending(r => r.Time).ToList();
                var preview = new FilterResult
                {
                    Flat = ordered.Select(r => new ChangeRow(r, ctx)).ToList(),
                    Entries = ordered.Select(r => new RowEntry { Record = r }).ToList()
                };
                var map = preview.Flat.ToDictionary(x => x.Record);
                var rows = BuildRows(preview, ActiveSort().Select(l => l.With(header: false)).ToList(), ctx, _collapsedValueGroups,
                    rec => map.TryGetValue(rec, out var row) ? row.ParamValue : "");
                _flat = preview.Flat;
                _filtered = ordered;
                _filteredContext = ctx;
                Rows = rows;
                SelectedRow = keep == null ? null : Rows.FirstOrDefault(r => r.Record.RecordId == keep);
                SummaryText = string.Format(Ru, "Предпросмотр актуализации: не соответствуют модели записей — {0:N0}", Rows.Count);
                HasStats = false;
                return;
            }
            var journal = _journal;
            if (journal == null || journal.Version != _allVersion || journal.LocalVersion != _localVersion)
                _journal = journal = new JournalData(_all.ToArray(), _allVersion, _localVersion, _localExcluded, _localRestored,
                    _liveBefore, _liveDeletedBefore);
            var criteria = new FilterCriteria
            {
                EventsView = _eventsView,
                Text = SearchText?.Trim(),
                Kinds = KindFilter.Allowed(_showCreated, _showModified, _showDeleted),
                Group = SelectedGroup?.Value,
                Author = SelectedAuthor == AllAuthors ? null : SelectedAuthor,
                Filter = _filter,
                HideUndone = HideUndone,
                HideDuplicates = HideDuplicates,
                To = _periodTo,
                ReceivedBy = _periodSinceSync ? _tracker.CurrentUser : null,
                ShowNoise = ShowNoise,
                HideVoid = HideVoid,
                ShowExcluded = ShowExcluded,
                Elements = _elementFilter,
                OwnerView = _elementFilterOwnerView,
                MaxRows = _settings.MaxDisplayedRows,
                ByOperation = _groupByOperation && !_eventsView,
                Expanded = new HashSet<string>(_expandedGroups, StringComparer.Ordinal),
                Sort = ActiveSort(),
                Collapsed = _collapsedValueGroups,
                Context = MakeRowContext()
            };
            Task.Run(() =>
            {
                var result = Filter(journal, criteria);
                Post(() =>
                {
                    if (version != _filterVersion) return;
                    // что выбрать: то, что просили (раскрытие операции, переход из «Истории»), иначе текущая строка
                    var current = SelectedRow;
                    var requested = _pendingSelectRecordId != null || _pendingSelectGroupKey != null;
                    var selectedId = _pendingSelectRecordId ?? (current != null && !current.IsGroupHeader ? current.Record.RecordId : null);
                    var selectedGroup = _pendingSelectGroupKey ?? (current?.IsGroupHeader == true ? current.GroupKey : null);
                    _pendingSelectRecordId = null;
                    _pendingSelectGroupKey = null;
                    _lastStatuses = criteria.Context.Statuses;
                    _lastExcluded = criteria.Context.Excluded;
                    _filterBase = result.Base;
                    _filterBaseContext = criteria.Context;
                    if (result.NeedsModelFilter)
                    {
                        var filtered = KeepByFilter(result.Base, criteria.Filter, criteria.Context, out var journalOnly);
                        var baseList = result.Base;
                        result = Finish(filtered, criteria.Context.Statuses, criteria);
                        result.Base = baseList;
                        if (journalOnly)
                            Status = string.Format(Ru, "Параметры элемента в фильтре — из модели не больше чем для {0:N0} записей; " +
                                                       "учтены только значения из журнала. Сузьте период или другие фильтры.", MaxParamSortRows);
                    }
                    if (result.NeedsUiSort)
                    {
                        var ctx = criteria.Context;
                        var sort = criteria.Sort;
                        if (result.Flat.Count > MaxParamSortRows)
                        {
                            // значение параметра читается из снимков модели — для очень длинного списка это заметная пауза
                            sort = sort.Where(l => l.Field != SortField.Param).ToList();
                            Status = string.Format(Ru, "Сортировка по параметру — не больше {0:N0} строк. Сузьте период или фильтры.", MaxParamSortRows);
                        }
                        var byRecord = new Dictionary<ChangeRecord, ChangeRow>(result.Flat.Count);
                        foreach (var row in result.Flat) byRecord[row.Record] = row;
                        result.Rows = BuildRows(result, sort, ctx, criteria.Collapsed,
                            rec => byRecord.TryGetValue(rec, out var row) ? row.ParamValue : new ChangeRow(rec, ctx).ParamValue);
                    }
                    _flat = result.Flat;
                    _filtered = result.All ?? new List<ChangeRecord>();
                    _filteredContext = criteria.Context;
                    Rows = result.Rows;
                    SelectedRow = (selectedGroup != null ? result.Rows.FirstOrDefault(r => r.IsGroupHeader && r.GroupKey == selectedGroup) : null)
                                  ?? (selectedId != null ? result.Rows.FirstOrDefault(r => !r.IsGroupHeader && r.Record.RecordId == selectedId) : null);
                    if (requested && SelectedRow != null) RequestScroll(SelectedRow);
                    if (criteria.EventsView)
                    {
                        SummaryText = string.Format(Ru, "событий: {0:N0}", result.Total);
                        HasStats = false;
                        return;
                    }
                    SummaryText = (result.Flat.Count < result.Total
                        ? string.Format(Ru, "показаны последние {0:N0} из {1:N0}", result.Flat.Count, result.Total)
                        : string.Format(Ru, "записей: {0:N0}", result.Total))
                        + (string.IsNullOrEmpty(_periodNote) ? "" : " · " + _periodNote);
                    StatCreated = string.Format(Ru, "создано {0:N0}", result.Stats[0]);
                    StatModified = string.Format(Ru, "изменено {0:N0}", result.Stats[1]);
                    StatDeleted = string.Format(Ru, "удалено {0:N0}", result.Stats[2]);
                    HasStats = true;
                    RaiseCommands();
                });
            });
        }

        private sealed class FilterCriteria
        {
            /// <summary>Вкладка «События модели» (иначе — «Изменения»).</summary>
            public bool EventsView;
            public string Text;
            /// <summary>Какие действия показывать на вкладке «Изменения».</summary>
            public HashSet<ChangeKind> Kinds;
            public ObjectGroup? Group;
            public string Author;
            /// <summary>Окно «Фильтр» и «Равно / Не равно» из меню ячейки.</summary>
            public RecordFilter Filter;
            /// <summary>Скрывать пары «правка + её отмена».</summary>
            public bool HideUndone;
            public bool HideDuplicates;
            /// <summary>Конец периода (не включая): «Вчера»; null — до сейчас.</summary>
            public DateTimeOffset? To;
            /// <summary>«С моей последней синхронизации»: не показывать полученное этим пользователем при синхронизации.</summary>
            public string ReceivedBy;
            public bool ShowNoise;
            public bool HideVoid;
            public bool ShowExcluded;
            public HashSet<long> Elements;
            /// <summary>История вида: и элементы, нарисованные на нём (вид-владелец).</summary>
            public string OwnerView;
            public int MaxRows;
            /// <summary>Записи одной операции Revit — одной строкой.</summary>
            public bool ByOperation;
            public HashSet<string> Expanded;
            /// <summary>Уровни «Сортировка и группировка».</summary>
            public List<SortLevel> Sort;
            /// <summary>Свёрнутые заголовки групп (общий набор, меняется только в потоке Revit).</summary>
            public HashSet<string> Collapsed;
            public RowContext Context;
        }

        /// <summary>Сортировка по столбцу параметра — только для списков не длиннее этого (значения читаются из снимков).</summary>
        private const int MaxParamSortRows = 20000;

        /// <summary>
        /// Расчёты по всему журналу, не зависящие от фильтров: статусы, убранные записи, записи без дублей.
        /// Считаются один раз на версию журнала (при первом обращении, в фоновом потоке фильтра), а не на каждое
        /// нажатие в поиске или раскрытие операции. Посчитанное не меняется — его можно читать из любого потока.
        /// </summary>
        private sealed class JournalData
        {
            private readonly object _sync = new object();
            private readonly HashSet<string> _localExcluded;
            private readonly HashSet<string> _localRestored;
            private Dictionary<ChangeRecord, SyncStatus> _statuses;
            private HashSet<string> _excluded;
            private List<ChangeRecord> _withoutDuplicates;
            private readonly List<string> _liveBefore;
            private readonly List<long> _liveDeletedBefore;

            /// <param name="liveBefore">Живые записи раньше начала периода — чтобы «Без дублей» узнавал вчерашнюю
            /// правку автора, полученную коллегой при синхронизации сегодня.</param>
            public JournalData(ChangeRecord[] records, int version, int localVersion, HashSet<string> localExcluded,
                HashSet<string> localRestored, List<string> liveBefore, List<long> liveDeletedBefore)
            {
                Records = records;
                Version = version;
                LocalVersion = localVersion;
                _localExcluded = new HashSet<string>(localExcluded, StringComparer.Ordinal);
                _localRestored = new HashSet<string>(localRestored, StringComparer.Ordinal);
                _liveBefore = liveBefore;
                _liveDeletedBefore = liveDeletedBefore;
            }

            public ChangeRecord[] Records { get; }
            public int Version { get; }
            /// <summary>Версия убранных и возвращённых только что (до перечитывания журнала).</summary>
            public int LocalVersion { get; }

            public Dictionary<ChangeRecord, SyncStatus> Statuses
            {
                get { lock (_sync) return _statuses ??= SyncStatusCalculator.Compute(Records); }
            }

            /// <summary>Убранные при актуализации: отметки в журнале и убранные только что.</summary>
            public HashSet<string> Excluded
            {
                get
                {
                    lock (_sync)
                    {
                        if (_excluded == null)
                        {
                            _excluded = Excluded(Records, _localExcluded, _localRestored);
                        }
                        return _excluded;
                    }
                }
            }

            public List<ChangeRecord> WithoutDuplicates
            {
                get { lock (_sync) return _withoutDuplicates ??= JournalDedup.WithoutDuplicates(Records, Excluded, _liveBefore, _liveDeletedBefore); }
            }

            private HashSet<ChangeRecord> _undone;
            /// <summary>Пары «правка + её отмена (Ctrl+Z)».</summary>
            public HashSet<ChangeRecord> Undone
            {
                get { lock (_sync) return _undone ??= UndoPairs.Find(Records); }
            }
        }

        private sealed class FilterResult
        {
            public List<RowEntry> Entries;
            /// <summary>Сортировка по параметру: строки соберёт поток Revit.</summary>
            public bool NeedsUiSort;
            public List<ChangeRow> Rows;
            public List<ChangeRow> Flat;
            /// <summary>Весь отфильтрованный список (в таблице — не больше «Максимум строк»): для экспорта и выделения.</summary>
            public List<ChangeRecord> All;
            public int Total;
            public int[] Stats;
            /// <summary>Список до условий окна «Фильтр» (после поиска, периода и т. п.) — для счётчика и значений в окне.</summary>
            public List<ChangeRecord> Base;
            /// <summary>В фильтре есть параметры элемента: условия проверит поток Revit (значения из снимков модели).</summary>
            public bool NeedsModelFilter;
        }

        private static FilterResult Filter(JournalData journal, FilterCriteria c)
        {
            IEnumerable<ChangeRecord> q = journal.Records;
            // статус «в хранилище / нет» считается по всем записям сеансов, до фильтров
            var statuses = journal.Statuses;
            if (c.Context != null) c.Context.Statuses = statuses;

            // убранные при актуализации (отметки в журнале + убранные только что)
            var excluded = journal.Excluded;
            if (c.Context != null) c.Context.Excluded = excluded;

            if (c.EventsView)
            {
                q = q.Where(r => r.Kind == ChangeKind.Event);
            }
            else
            {
                if (c.HideDuplicates) q = journal.WithoutDuplicates;
                q = q.Where(r => r.Kind != ChangeKind.Event);
                // «С моей последней синхронизации»: полученное при ней старше неё — это не новые изменения
                if (c.ReceivedBy != null) q = q.Where(r => !IsReceivedBy(r, c.ReceivedBy));
                if (c.HideVoid) q = q.Where(r => !statuses.TryGetValue(r, out var st) || !SyncStatusCalculator.IsVoid(st));
                if (!c.ShowExcluded && excluded.Count > 0) q = q.Where(r => r.RecordId == null || !excluded.Contains(r.RecordId));
                if (c.Kinds != null) q = q.Where(r => c.Kinds.Contains(r.Kind));
                if (!c.ShowNoise) q = q.Where(r => r.IsNoise != true);
                if (c.Group.HasValue) q = q.Where(r => r.Group == c.Group.Value);
                if (c.HideUndone)
                {
                    var undone = journal.Undone;
                    if (undone.Count > 0) q = q.Where(r => !undone.Contains(r));
                }
                if (c.Elements != null)
                    q = q.Where(r => c.Elements.Contains(r.ElementId) ||
                                     (c.OwnerView != null && string.Equals(r.OwnerView, c.OwnerView, StringComparison.Ordinal)));
            }
            if (c.To.HasValue) q = q.Where(r => r.Time < c.To.Value); // «Вчера»
            if (c.Author != null)
            {
                q = c.Author == UnknownAuthor
                    ? q.Where(r => string.IsNullOrEmpty(r.User))
                    : q.Where(r => string.Equals(r.User, c.Author, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(c.Text))
            {
                var t = c.Text;
                var isId = long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue);
                q = q.Where(r => (isId && r.ElementId == idValue) || Matches(r, t));
            }

            var list = q.OrderByDescending(r => r.Time).ToList();
            var baseList = list;
            if (!c.EventsView && c.Filter != null && !c.Filter.IsEmpty)
            {
                // параметры элемента у изменённых — из снимков модели: это только поток Revit (ApplyFilter → FinishFilter)
                if (c.Filter.NeedsModel) return new FilterResult { Base = baseList, NeedsModelFilter = true };
                var ctx = c.Context;
                list = list.Where(r => c.Filter.Keep(r, (x, f) => FieldValue(x, f, ctx), JournalParam)).ToList();
            }
            var result = Finish(list, statuses, c);
            result.Base = baseList;
            return result;
        }

        /// <summary>Значение поля записи или элемента для условия фильтра — как в таблице.</summary>
        private static string FieldValue(ChangeRecord r, FilterField f, RowContext ctx)
        {
            switch (f)
            {
                case FilterField.Kind: return new ChangeRow(r, ctx).KindText;
                case FilterField.Author: return string.IsNullOrEmpty(r.User) ? UnknownAuthor : r.User;
                case FilterField.Machine: return r.Machine;
                case FilterField.Transaction: return r.Transaction;
                case FilterField.Status: return new ChangeRow(r, ctx).StatusLabel;
                case FilterField.Source: return ChangeRow.SourceLabel(r.Source);
                case FilterField.Category: return r.Category;
                case FilterField.Group: return ElementDescriber.GroupDisplayName(r.Group);
                case FilterField.Name: return r.Name;
                case FilterField.TypeName: return r.TypeName;
                case FilterField.Id: return r.ElementId > 0 ? r.ElementId.ToString(CultureInfo.InvariantCulture) : "";
                case FilterField.Level: return r.Level;
                case FilterField.Workset: return r.Workset;
                case FilterField.View: return r.OwnerView;
                case FilterField.Creator: return r.Creator;
                case FilterField.LastChangedBy: return r.LastChangedBy;
                default: return "";
            }
        }

        /// <summary>
        /// Значение параметра по журналу: изменён в записи — «стало», у созданного и удалённого — из записи.
        /// null — в журнале нет (у изменённых журнал хранит только изменившиеся параметры).
        /// </summary>
        private static string JournalParam(ChangeRecord r, string name)
        {
            var changed = r.Params?.FirstOrDefault(p => p != null && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (changed != null) return changed.New ?? "";
            return r.State?.FirstOrDefault(p => p != null && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))?.Value;
        }

        // кэш значений параметров для фильтра: по имени параметра → по элементу (поток Revit)
        private readonly Dictionary<string, Dictionary<long, string>> _filterParamCache =
            new Dictionary<string, Dictionary<long, string>>(StringComparer.CurrentCultureIgnoreCase);
        private string _filterParamCacheModel;
        private int _filterParamCacheStamp = -1;

        /// <summary>Значение параметра для фильтра: из журнала, иначе текущее из снимка модели. Только поток Revit.</summary>
        private string ModelParam(ChangeRecord r, string name)
        {
            var v = JournalParam(r, name);
            if (v != null || r.ElementId <= 0) return v;
            if (_filterParamCacheStamp != _tracker.ChangeStamp || _filterParamCacheModel != _modelKey)
            {
                _filterParamCache.Clear();
                _filterParamCacheStamp = _tracker.ChangeStamp;
                _filterParamCacheModel = _modelKey;
            }
            if (!_filterParamCache.TryGetValue(name, out var byId)) _filterParamCache[name] = byId = new Dictionary<long, string>();
            if (byId.TryGetValue(r.ElementId, out var cached)) return cached;
            try { v = _tracker.LookupParam(_modelKey, r.ElementId, name); }
            catch (Exception) { v = null; }
            byId[r.ElementId] = v;
            return v;
        }

        /// <summary>
        /// Условия фильтра в потоке Revit. Параметры элемента из модели — не больше MaxParamSortRows записей
        /// (иначе заметная пауза); сверх этого — только значения из журнала (journalOnly).
        /// </summary>
        private List<ChangeRecord> KeepByFilter(List<ChangeRecord> list, RecordFilter filter, RowContext ctx, out bool journalOnly)
        {
            journalOnly = false;
            if (list == null) return new List<ChangeRecord>();
            if (filter == null || filter.IsEmpty) return list;
            journalOnly = filter.NeedsModel && list.Count > MaxParamSortRows;
            Func<ChangeRecord, string, string> param = journalOnly ? (Func<ChangeRecord, string, string>)JournalParam : ModelParam;
            return list.Where(r => filter.Keep(r, (x, f) => FieldValue(x, f, ctx), param)).ToList();
        }

        // список до условий окна «Фильтр» (последний расчёт) — для счётчика и значений в окне
        private List<ChangeRecord> _filterBase;
        private RowContext _filterBaseContext;

        /// <summary>«Подходит записей: 148 из 2 652» — для окна «Фильтр». Поток Revit.</summary>
        public string FilterPreviewText(RecordFilter filter)
        {
            var list = _filterBase;
            if (list == null || _eventsView) return "";
            var kept = KeepByFilter(list, filter, _filterBaseContext, out var journalOnly);
            var text = string.Format(Ru, "Подходит записей: {0:N0} из {1:N0}", kept.Count, list.Count);
            return journalOnly ? text + " (параметры элемента — только из журнала: записей больше " + MaxParamSortRows.ToString("N0", Ru) + ")" : text;
        }

        /// <summary>Значения поля в списке (до условий окна) с числом записей — для выпадающего списка «Значение». Поток Revit.</summary>
        public List<FilterValueOption> FilterValues(FilterField field, string paramName)
        {
            var list = _filterBase ?? new List<ChangeRecord>();
            var ctx = _filterBaseContext;
            var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Count(string v)
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                v = v.Trim();
                counts[v] = counts.TryGetValue(v, out var n) ? n + 1 : 1;
            }
            var lookups = 0;
            foreach (var r in list)
            {
                switch (field)
                {
                    case FilterField.ChangedParam:
                        if (r.Params != null) foreach (var p in r.Params) Count(p?.Name);
                        break;
                    case FilterField.Before:
                    case FilterField.After:
                        var ch = r.Params?.FirstOrDefault(p => p != null && string.Equals(p.Name, paramName, StringComparison.CurrentCultureIgnoreCase));
                        if (ch != null) Count(field == FilterField.Before ? ch.Old : ch.New);
                        break;
                    case FilterField.Param:
                        if (string.IsNullOrEmpty(paramName)) break;
                        var v = JournalParam(r, paramName);
                        // из модели — не больше 5 000 элементов: список значений должен открываться сразу
                        if (v == null && r.ElementId > 0 && lookups < 5000) { lookups++; v = ModelParam(r, paramName); }
                        Count(v);
                        break;
                    default:
                        Count(FieldValue(r, field, ctx));
                        break;
                }
            }
            var cmp = StringComparer.Create(Ru, true);
            return counts.OrderBy(kv => kv.Key, cmp).Take(500)
                .Select(kv => new FilterValueOption(kv.Key, kv.Value)).ToList();
        }

        /// <summary>Итоги и строки таблицы по готовому списку.</summary>
        private static FilterResult Finish(List<ChangeRecord> list, Dictionary<ChangeRecord, SyncStatus> statuses, FilterCriteria c)
        {
            var stats = new int[3];
            foreach (var r in list)
            {
                // в итоги не идёт то, чего фактически не было (не попало в хранилище) — как и на 3D-виде
                if (statuses.TryGetValue(r, out var status) && SyncStatusCalculator.IsVoid(status)) continue;
                if (r.Kind == ChangeKind.Created) stats[0] += r.SummaryCount ?? 1;
                else if (r.Kind == ChangeKind.Modified) stats[1] += r.SummaryCount ?? 1;
                else if (r.Kind == ChangeKind.Deleted) stats[2] += r.SummaryCount ?? 1;
            }
            var shown = list.Count > c.MaxRows ? list.Take(c.MaxRows).ToList() : list;
            var flat = shown.Select(r => new ChangeRow(r, c.Context)).ToList();
            var entries = c.ByOperation
                ? OperationGrouping.Build(shown, c.Expanded)
                : shown.Select(r => new RowEntry { Record = r }).ToList();
            var result = new FilterResult { Flat = flat, Total = list.Count, Stats = stats, Entries = entries, All = list };
            // сортировка по значению параметра читает снимки модели — её делаем в потоке Revit (см. ApplyFilter)
            result.NeedsUiSort = !c.EventsView && c.Sort.Any(l => l.Field == SortField.Param);
            if (!result.NeedsUiSort) result.Rows = BuildRows(result, c.EventsView ? null : c.Sort, c.Context, c.Collapsed, null);
            return result;
        }

        /// <summary>Отсортировать строки по уровням «Сортировка и группировка» и разложить по группам с заголовками.</summary>
        private static List<ChangeRow> BuildRows(FilterResult r, IReadOnlyList<SortLevel> sort, RowContext ctx, ISet<string> collapsed,
            Func<ChangeRecord, string> param)
        {
            var byRecord = new Dictionary<ChangeRecord, ChangeRow>(r.Flat.Count);
            foreach (var row in r.Flat) byRecord[row.Record] = row;
            var entries = r.Entries;
            var custom = sort != null && !RowSorting.IsDefault(sort);
            if (custom)
            {
                entries = RowSorting.Sort(entries, sort, (m, f) => SortKey(m, f, ctx, param));
                // экспорт и «Выделить все» — по одной строке на запись, но в том же порядке, что на экране
                r.Flat = RowSorting.Sort(r.Flat.Select(x => new RowEntry { Record = x.Record }).ToList(), sort,
                        (m, f) => SortKey(m, f, ctx, param))
                    .Select(e => byRecord[e.Record]).ToList();
            }
            var rows = entries.Select(e => e.IsHeader || e.IsChild ? new ChangeRow(e, ctx) : byRecord[e.Record]).ToList();
            var groups = custom
                ? RowSorting.Groups(entries, sort, (m, f) => SortTitle(m, f, ctx, param), (m, f) => SortKey(m, f, ctx, param))
                : null;
            if (groups != null)
            {
                // один объект на группу: по нему WPF объединяет строки под заголовком и хранит свёрнутость
                var shared = new Dictionary<string, RowGroup>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++)
                {
                    var info = groups[i];
                    var g = new RowGroup[info.Length];
                    for (int lvl = 0; lvl < info.Length; lvl++)
                    {
                        if (!shared.TryGetValue(info[lvl].Key, out var group))
                            shared[info[lvl].Key] = group = new RowGroup(info[lvl].Key, info[lvl].Title, lvl, collapsed);
                        g[lvl] = group;
                    }
                    rows[i].SetGroups(g);
                }
            }
            else
            {
                foreach (var row in rows) row.SetGroups(null);
            }
            return rows;
        }

        private static string Common(IReadOnlyList<ChangeRecord> m, Func<ChangeRecord, string> get)
        {
            var first = get(m[0]) ?? "";
            for (int i = 1; i < m.Count; i++)
                if (!string.Equals(get(m[i]) ?? "", first, StringComparison.CurrentCultureIgnoreCase)) return "(разные)";
            return first;
        }

        /// <summary>Ключ сортировки строки (для операции — по всем её записям: общее значение или «(разные)»).</summary>
        private static object SortKey(IReadOnlyList<ChangeRecord> m, SortField f, RowContext ctx, Func<ChangeRecord, string> param)
        {
            var r = m[0];
            switch (f)
            {
                case SortField.Time: return r.Time;
                case SortField.Kind: return m.Any(x => x.Kind != r.Kind) ? 9 : (int)r.Kind;
                case SortField.Id: return r.ElementId;
                case SortField.Status:
                    // в режиме актуализации столбец показывает причину несоответствия — по ней и сортируем
                    if (r.RecordId != null && ctx?.PreviewReasons != null && ctx.PreviewReasons.TryGetValue(r.RecordId, out var why)) return why;
                    return ctx?.Statuses != null && ctx.Statuses.TryGetValue(r, out var st) ? (int)st : -1;
                default: return SortTitle(m, f, ctx, param);
            }
        }

        /// <summary>Значение для заголовка группы: «Иванов», «28.09.2026», «Стены».</summary>
        private static string SortTitle(IReadOnlyList<ChangeRecord> m, SortField f, RowContext ctx, Func<ChangeRecord, string> param)
        {
            var r = m[0];
            switch (f)
            {
                case SortField.Time: return r.Time.LocalDateTime.ToString("dd.MM.yyyy", Ru);
                case SortField.Kind: return m.Any(x => x.Kind != r.Kind) ? "Разные" : ChangeRow.KindLabel(r.Kind);
                case SortField.Author: return Common(m, x => string.IsNullOrEmpty(x.User) ? UnknownAuthor : x.User);
                case SortField.Name: return Common(m, x => x.Name);
                case SortField.Category: return Common(m, x => x.Category);
                case SortField.Group: return Common(m, x => ElementDescriber.GroupDisplayName(x.Group));
                case SortField.TypeName: return Common(m, x => x.TypeName);
                case SortField.Id: return r.ElementId > 0 ? r.ElementId.ToString(CultureInfo.InvariantCulture) : "";
                case SortField.Transaction: return Common(m, x => x.Transaction);
                case SortField.Level: return Common(m, x => x.Level);
                case SortField.Workset: return Common(m, x => x.Workset);
                case SortField.View: return Common(m, x => x.OwnerView);
                case SortField.Source: return Common(m, x => ChangeRow.SourceLabel(x.Source));
                case SortField.Machine: return Common(m, x => x.Machine);
                case SortField.Creator: return Common(m, x => x.Creator);
                case SortField.Status:
                {
                    var st = ctx?.Statuses != null && ctx.Statuses.TryGetValue(r, out var s1) ? s1 : SyncStatus.Unknown;
                    var text = ChangeRow.StatusShort(SyncStatusCalculator.Text(st, ctx?.IsWorkshared ?? true));
                    return string.IsNullOrEmpty(text) ? "нет сведений" : text;
                }
                case SortField.Param: return param?.Invoke(r) ?? "";
                default: return "";
            }
        }

        private static bool Matches(ChangeRecord r, string t)
        {
            return Has(r.Name, t) || Has(r.Category, t) || Has(r.TypeName, t) || Has(r.User, t) ||
                   Has(r.Transaction, t) || Has(r.Level, t) || Has(r.Workset, t) || Has(r.OwnerView, t) ||
                   Has(r.UniqueId, t) || Has(r.ClassName, t) ||
                   (r.Params != null && r.Params.Any(p => Has(p.Name, t) || Has(p.Old, t) || Has(p.New, t))) ||
                   (r.State != null && r.State.Any(p => Has(p.Value, t)));
        }

        private static bool Has(string s, string t) => s != null && s.IndexOf(t, StringComparison.CurrentCultureIgnoreCase) >= 0;

        // ───────────────────────────── действия ─────────────────────────────

        /// <param name="ownerView">История вида: ещё и записи об элементах, нарисованных на этом виде (вид-владелец).</param>
        public void SetElementFilter(IEnumerable<long> ids, string label, bool fromSelection = false, string ownerView = null)
        {
            _elementFilterOwnerView = ids == null ? null : ownerView;
            if (ids == null)
            {
                _elementFilter = null;
                ElementFilterText = "";
                _elementFilterFromSelection = false;
            }
            else
            {
                _elementFilter = new HashSet<long>(ids);
                ElementFilterText = label ?? $"История элементов: {_elementFilter.Count}";
                _elementFilterFromSelection = fromSelection;
            }
            OnPropertyChanged(nameof(HasElementFilter));
            OnPropertyChanged(nameof(EmptyHint));
            FiltersChanged();
        }

        /// <summary>Показать историю выделенных элементов (команда на ленте, ссылка «История в журнале»).</summary>
        public void ShowHistoryOf(ICollection<long> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                Status = "Сначала выделите элементы в модели.";
                return;
            }
            if (_followSelection)
            {
                // явный запрос истории: дальше список не должен меняться вслед за выделением
                _followSelection = false;
                _uiState.FollowSelection = false;
                RequestUiSave?.Invoke();
                OnPropertyChanged(nameof(FollowSelection));
            }
            SetView(false);
            string label;
            if (ids.Count == 1)
            {
                var id = ids.First();
                var name = _all.LastOrDefault(r => r.ElementId == id && !string.IsNullOrEmpty(r.Name))?.Name;
                label = name != null ? $"История элемента «{name}» (ID {id})" : $"История элемента ID {id}";
            }
            else label = $"История выделенных элементов: {ids.Count}";
            SetElementFilter(ids, label);
        }

        /// <summary>
        /// Кнопка «Журнал»: вся модель — отбор «История элемента / вида», оставшийся от кнопки «История», снимается (разработчик,
        /// 23.10.2026: «мне нужна история всей модели», а журнал показывал историю вида).
        /// </summary>
        public void ShowWholeModel()
        {
            if (_elementFilter == null) return;
            SetElementFilter(null, null);
            Status = "";
        }

        /// <summary>«История выделенного», когда ничего не выделено: открытый вид и элементы, нарисованные на нём.</summary>
        public void ShowViewHistory(long viewId, string viewName)
        {
            ShowHistoryOf(new[] { viewId });
            SetElementFilter(new[] { viewId }, $"История вида «{viewName}» и элементов на нём", ownerView: viewName);
            Status = $"Ничего не выделено — показана история вида «{viewName}»: сам вид и нарисованное на нём (размеры, марки, аннотации). " +
                     "Чтобы увидеть историю элементов, выделите их.";
        }

        private void ShowRowHistory()
        {
            var r = SelectedRow?.Record;
            if (r == null || r.ElementId <= 0 || SelectedRow.IsGroupHeader) return;
            if (_followSelection) FollowSelection = false;
            SetElementFilter(new[] { r.ElementId }, $"История: {RecordFilter.Short(r.Name, 30)} (ID {r.ElementId})");
        }

        private void ShowCardHistory()
        {
            if (Card == null) return;
            ShowHistoryOf(Card.Ids);
        }

        /// <summary>Прокрутить к строке, раскрыв свёрнутые группы, в которых она лежит.</summary>
        private void RequestScroll(ChangeRow row)
        {
            foreach (var g in new[] { row.Group1, row.Group2, row.Group3, row.Group4 })
                if (g != null) g.IsExpanded = true;
            ScrollRequested?.Invoke(row);
        }

        /// <summary>«Удалён вместе с: …» → строка удаления родителя (раскрыть операцию, если свёрнута).</summary>
        private void GoToParent(ChangeRow row)
        {
            var r = row?.Record;
            if (r == null || string.IsNullOrEmpty(r.ParentRecord)) return;
            var parent = _all.FirstOrDefault(x => x.RecordId == r.ParentRecord);
            if (parent == null)
            {
                Status = "Запись об удалении «" + (r.ParentName ?? "элемента ID " + r.ParentId) +
                         "» не найдена в выбранном периоде — увеличьте период.";
                return;
            }
            SelectRecord(parent);
        }

        /// <summary>Выбрать запись в списке; если она внутри свёрнутой операции — раскрыть операцию.</summary>
        public void SelectRecord(ChangeRecord record)
        {
            if (record == null) return;
            var row = Rows.FirstOrDefault(r => !r.IsGroupHeader && ReferenceEquals(r.Record, record));
            if (row != null)
            {
                SelectedRow = row;
                RequestScroll(row);
                return;
            }
            var inFlat = _flat.Any(r => ReferenceEquals(r.Record, record));
            var key = OperationGrouping.KeyOf(record);
            if (inFlat && _groupByOperation && key != null)
            {
                _expandedGroups.Add(key);
                _pendingSelectRecordId = record.RecordId;
                ScheduleFilter(0);
                return;
            }
            Status = "Эта запись скрыта фильтрами — снимите их, чтобы увидеть её в списке.";
        }

        private void ShowInModel()
        {
            var row = SelectedRow;
            if (row == null || _modelKey == null) return;
            if (row.IsGroupHeader)
            {
                var ids = row.ModelIds;
                if (ids.Count == 0) { Status = "Элементов этой операции в модели нет (удалены)."; return; }
                RememberOwnSelection(ids);
                _actions.SelectElements(_modelKey, ids, msg => Post(() => Status = msg));
                return;
            }
            RememberOwnSelection(new[] { row.Record.ElementId });
            _actions.ShowElement(_modelKey, row.Record.ElementId, msg => Post(() => Status = msg));
        }

        /// <summary>Больше Revit выделяет очень медленно.</summary>
        private const int MaxSelect = 20000;

        private void SelectAllInModel()
        {
            if (_modelKey == null) return;
            var all = _filtered.Where(r => r.ElementId > 0 && r.Kind != ChangeKind.Deleted && r.Kind != ChangeKind.Event)
                .Select(r => r.ElementId).Distinct().ToList();
            if (all.Count == 0)
            {
                Status = "В списке нет элементов, которые можно выделить.";
                return;
            }
            var ids = all.Take(MaxSelect).ToList();
            var cut = all.Count > ids.Count
                ? string.Format(Ru, " Выделены первые {0:N0} из {1:N0}: больше Revit выделяет очень медленно — сузьте фильтры.", ids.Count, all.Count)
                : "";
            RememberOwnSelection(ids);
            _actions.SelectElements(_modelKey, ids, msg => Post(() => Status = msg + cut));
        }

        private void CopyId()
        {
            var row = SelectedRow;
            if (row == null) return;
            if (row.IsGroupHeader)
            {
                var all = row.Members.Where(m => m.ElementId > 0).Select(m => m.ElementId).Distinct().ToList();
                CopyToClipboard?.Invoke(string.Join(", ", all.Select(x => x.ToString(CultureInfo.InvariantCulture))));
                Status = $"Скопировано ID: {all.Count}.";
                return;
            }
            CopyToClipboard?.Invoke(row.Record.ElementId.ToString(CultureInfo.InvariantCulture));
            Status = "ID " + row.Record.ElementId + " скопирован в буфер обмена.";
        }

        private void Export()
        {
            var path = SaveFileDialog?.Invoke(CsvExporter.DefaultFileName(ModelTitle));
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                // по одной строке на запись, даже если операции свёрнуты; весь отфильтрованный список, а не только
                // показанные в таблице строки (их не больше «Максимум строк»)
                var rows = _filtered.Count > _flat.Count
                    ? _filtered.Select(r => new ChangeRow(r, _filteredContext)).ToList()
                    : _flat;
                CsvExporter.Export(path, rows, ParamColumns.ToList());
                Status = string.Format(Ru, "Экспортировано строк: {0:N0} → {1}", rows.Count, path);
            }
            catch (Exception ex)
            {
                Status = "Не удалось сохранить файл: " + ex.Message;
            }
        }

        /// <summary>«Сбросить все»: снять все фильтры, кроме периода. Столбец параметра остаётся.</summary>
        private void ResetFilters()
        {
            if (_followSelection)
            {
                _followSelection = false;
                _uiState.FollowSelection = false;
                RequestUiSave?.Invoke();
                OnPropertyChanged(nameof(FollowSelection));
            }
            _searchText = "";
            _selectedGroup = GroupOptions[0];
            _selectedAuthor = AllAuthors;
            _showNoise = false;
            _hideVoid = true;
            _hideUndone = true;
            _showExcluded = false;
            _showCreated = _showModified = _showDeleted = true;
            _hideDuplicates = true;
            _filter = RecordFilter.Empty;
            _elementFilter = null;
            _elementFilterText = "";
            _elementFilterFromSelection = false;
            foreach (var n in new[]
                     {
                         nameof(SearchText), nameof(HasSearchText), nameof(SelectedGroup), nameof(SelectedAuthor),
                         nameof(ShowNoise), nameof(HideVoid), nameof(ShowExcluded), nameof(ShowCreated), nameof(ShowModified),
                         nameof(ShowDeleted), nameof(KindSummary), nameof(HideDuplicates), nameof(HideUndone),
                         nameof(CurrentFilter), nameof(FilterSummary), nameof(HasFilter),
                         nameof(ElementFilterText), nameof(HasElementFilter), nameof(EmptyHint)
                     })
                OnPropertyChanged(n);
            FiltersChanged();
        }

        /// <summary>Открыть в проводнике папку журнала этой модели (или общую папку, если записей ещё нет).</summary>
        // ───────────────────── связь журналов (DECISIONS.md модуля, «Связь журналов») ─────────────────────

        public RelayCommand LinkCandidateCommand { get; }
        public RelayCommand RejectCandidateCommand { get; }
        public RelayCommand JournalLinksCommand { get; }

        private JournalInfo _journalCandidate;
        /// <summary>Найден журнал прежней истории этой модели (то же место на Revit Server, другой номер хранилища) — полоса в панели.</summary>
        public JournalInfo JournalCandidate
        {
            get => _journalCandidate;
            private set
            {
                if (!Set(ref _journalCandidate, value)) return;
                OnPropertyChanged(nameof(HasJournalCandidate));
                OnPropertyChanged(nameof(JournalCandidateText));
            }
        }
        public bool HasJournalCandidate => _journalCandidate != null;
        public string JournalCandidateText => _journalCandidate == null ? "" :
            "Найдена прежняя история этой модели: журнал «" + _journalCandidate.Title + "»" +
            (string.IsNullOrEmpty(_journalCandidate.Period) ? "" : ", " + _journalCandidate.Period) +
            (string.IsNullOrEmpty(_journalCandidate.Users) ? "" : " (" + _journalCandidate.Users + ")") +
            ". У хранилища сменился номер (например, его восстанавливали из копии), и журнал начался заново. «Связать» — записи прежнего журнала будут видны здесь, у всех.";

        /// <summary>Модель, для которой открыто окно «Связанные журналы»: ключ, прежние и связанные ключи.</summary>
        public string LinksModelKey => _modelKey;
        public IReadOnlyList<string> LinksOwnKeys => new[] { _modelKey }.Concat(_legacyKeys).ToList();
        public IReadOnlyList<string> LinkedKeys => _linkedKeys;

        /// <summary>В фоне, перед первым чтением: связанные журналы — в чтение, журнал прежней истории — в полосу.</summary>
        private void PrepareLinks(LogReader reader)
        {
            var key = reader.ModelKey;
            var legacy = _legacyKeys;
            var path = _modelPath;
            try
            {
                (List<string> Linked, List<JournalInfo> Candidates) links;
                lock (_linkCache)
                {
                    if (!_linkCache.TryGetValue(key, out links))
                    {
                        var linked = JournalLinkStore.LinkedKeys(key, legacy);
                        var reading = new HashSet<string>(legacy.Concat(linked), StringComparer.OrdinalIgnoreCase) { key };
                        links = (linked, JournalLinkStore.FindCandidates(key, path, reading));
                        _linkCache[key] = links;
                    }
                }
                reader.AddKeys(links.Linked);
                Post(() =>
                {
                    if (!ReferenceEquals(reader, _reader)) return;
                    _linkedKeys = links.Linked;
                    JournalCandidate = links.Candidates.FirstOrDefault();
                });
            }
            catch (Exception ex)
            {
                Diag.Log("Journal links failed", ex);
            }
        }

        /// <summary>Связать журнал прежней истории: links.json, событие в журнал, проверка «хранилище заменено копией», перечитать.</summary>
        public void LinkJournal(JournalInfo info) => ChangeLink(info, link: true, reject: false);

        /// <summary>Отвязать журнал (окно «Связанные журналы»).</summary>
        public void UnlinkJournal(JournalInfo info) => ChangeLink(info, link: false, reject: false);

        /// <summary>«Это другая модель»: больше не предлагать этот журнал (у всех).</summary>
        public void RejectJournal(JournalInfo info) => ChangeLink(info, link: false, reject: true);

        /// <summary>Связь поменялась (окно «Связанные журналы» перечитывает список).</summary>
        public event Action LinksChanged;

        private void ChangeLink(JournalInfo info, bool link, bool reject)
        {
            var key = _modelKey;
            if (key == null || info == null) return;
            var entry = new JournalLinkEntry { Key = info.Key, Title = info.Title, By = _tracker.CurrentUser, At = DateTimeOffset.Now };
            Task.Run(() =>
            {
                try
                {
                    var file = JournalLinkStore.Read(key);
                    if (link) JournalLinks.Link(file, entry);
                    else JournalLinks.Unlink(file, entry, reject);
                    JournalLinkStore.Write(key, file);
                }
                catch (Exception ex)
                {
                    Diag.Log("Journal link change failed", ex);
                    Post(() => Status = "Не удалось записать связь журналов: " + ex.Message);
                    return;
                }
                if (!reject)
                {
                    _actions.Run(app =>
                    {
                        _tracker.WriteLinkEvent(key, entry, link);
                        if (link) _tracker.CheckStorageReplaced(key, "link"); // пропало ли что-то при восстановлении
                    });
                }
                lock (_linkCache) _linkCache.Remove(key);
                Post(() =>
                {
                    if (ReferenceEquals(info, _journalCandidate)) JournalCandidate = null;
                    Status = link ? "Журнал «" + info.Title + "» связан: его записи видны вместе с журналом модели."
                        : reject ? "Журнал «" + info.Title + "» больше не предлагается."
                        : "Связь с журналом «" + info.Title + "» убрана.";
                    LinksChanged?.Invoke();
                    if (!reject && key == _modelKey) Reload();
                });
            });
        }

        private void OpenLogFolder()
        {
            var root = DataFolders.LogFolder;
            var key = _modelKey;
            Task.Run(() =>
            {
                try
                {
                    // сетевая папка может отвечать долго — проверяем не в потоке интерфейса
                    var dir = key != null && System.IO.Directory.Exists(System.IO.Path.Combine(root, key))
                        ? System.IO.Path.Combine(root, key)
                        : root;
                    System.IO.Directory.CreateDirectory(dir);
                    Post(() => RevealFolder?.Invoke(dir));
                }
                catch (Exception ex)
                {
                    Post(() => Status = "Не удалось открыть папку журнала: " + ex.Message);
                }
            });
        }

        private void RaiseCommands()
        {
            foreach (var c in new[]
                     {
                         ExportCommand, ShowInModelCommand, HistoryCommand, CopyIdCommand, SelectAllInModelCommand,
                         CardHistoryCommand, ActualizeCommand, ShowIn3DCommand
                     })
                c?.RaiseCanExecuteChanged();
        }

        private void Post(Action a)
        {
            _ui.BeginInvoke(a);
        }

        private static string Dash(string s) => string.IsNullOrEmpty(s) ? "—" : s;
    }
}
