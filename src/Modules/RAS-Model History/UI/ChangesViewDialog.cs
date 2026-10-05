using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Строка легенды 3D-вида изменений в панели. Цвета — те, что на виде в Revit: это данные, а не оформление
    /// панели, поэтому не кисти темы Spy.*.
    /// </summary>
    public sealed class ChangesLegendItem
    {
        public ChangesLegendItem(LegendEntry e)
        {
            Label = e.Label;
            CountText = e.Count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
            Fill = Frozen(e.FillR, e.FillG, e.FillB);
            Line = Frozen(e.LineR, e.LineG, e.LineB);
        }

        public string Label { get; }
        public string CountText { get; }
        public Brush Fill { get; }
        public Brush Line { get; }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Цвет на выбор в окне «Показать в 3D…»: подпись и образец (цвет на виде — данные, не кисть темы).</summary>
    public sealed class ColorChoice
    {
        public ColorChoice(string label, uint rgb)
        {
            Label = label;
            Rgb = rgb;
            var brush = new SolidColorBrush(Color.FromRgb(ChangesPalette.R(rgb), ChangesPalette.G(rgb), ChangesPalette.B(rgb)));
            brush.Freeze();
            Swatch = brush;
        }

        public string Label { get; }
        public uint Rgb { get; }
        public Brush Swatch { get; }
    }

    /// <summary>Автор в списке окна «Показать в 3D…».</summary>
    public sealed class AuthorChoice : ObservableObject
    {
        public AuthorChoice(string label, string user, bool isChecked)
        {
            Label = label;
            User = user;
            _isChecked = isChecked;
        }

        public string Label { get; }
        /// <summary>Как автор записан в журнале («» — неизвестный).</summary>
        public string User { get; }

        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    }

    /// <summary>Окно «Показать в 3D…»: период, авторы, что показать, цвет, подрезка. Заполняется фильтрами панели.</summary>
    public sealed class ChangesViewDialogModel : ObservableObject
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private const string DateFormat = "dd.MM.yyyy";

        /// <param name="authors">Авторы журнала за всё время: (подпись, как записан в журнале).</param>
        /// <param name="panelPeriod">Период панели: дней (0 — всё время) или MainViewModel.PeriodYesterday / PeriodSinceMySync.</param>
        /// <param name="panelAuthor">Автор, выбранный в панели (как записан в журнале); null — все.</param>
        /// <param name="lastSync">Последняя синхронизация пользователя — показывается в названии периода.</param>
        public ChangesViewDialogModel(IReadOnlyList<LevelInfo> levels, IEnumerable<KeyValuePair<string, string>> authors,
            int panelPeriod, string panelAuthor, bool showCreated, bool showModified, ChangesViewStyle style = null,
            DateTimeOffset? lastSync = null)
        {
            ColorChoices = ChangesPalette.Colors.Select(c => new ColorChoice(c.Key, c.Value)).ToList();
            BackgroundOptions = new List<Option<int>>
            {
                new Option<int>("заметнее (прозрачность 40 %)", 40),
                new Option<int>("обычно (65 %)", 65),
                new Option<int>("бледнее (85 %)", 85),
                new Option<int>("скрыть — показать только изменения", ChangesPalette.HideBackground)
            };
            ApplyStyle(style ?? new ChangesViewStyle());
            ResetColorsCommand = new RelayCommand(() => ApplyStyle(new ChangesViewStyle()));

            // те же периоды, что в панели, плюс «Свой период»
            Periods = new List<Option<string>>
            {
                new Option<string>("Сегодня", "today"),
                new Option<string>("Вчера", "yesterday"),
                new Option<string>("3 дня", "3"),
                new Option<string>("7 дней", "7"),
                new Option<string>("30 дней", "30"),
                new Option<string>("90 дней", "90"),
                new Option<string>("Год", "365"),
                new Option<string>("Всё время", "all"),
                new Option<string>("С моей последней синхронизации" +
                                   (lastSync.HasValue ? " (" + lastSync.Value.ToLocalTime().ToString("dd.MM HH:mm", Ru) + ")" : ""), "sync"),
                new Option<string>("Свой период", "custom")
            };
            var key = panelPeriod == 1 ? "today"
                : panelPeriod == MainViewModel.PeriodYesterday ? "yesterday"
                : panelPeriod == MainViewModel.PeriodSinceMySync ? "sync"
                : panelPeriod == 0 ? "all"
                : Periods.Any(p => p.Value == panelPeriod.ToString(CultureInfo.InvariantCulture)) ? panelPeriod.ToString(CultureInfo.InvariantCulture)
                : "custom";
            _selectedPeriod = Periods.First(p => p.Value == key);
            if (key == "custom") _fromText = DateTime.Today.AddDays(-(Math.Max(1, panelPeriod) - 1)).ToString(DateFormat, Ru);

            Authors = authors.Select(a => new AuthorChoice(a.Key, a.Value,
                panelAuthor == null || string.Equals(a.Value, panelAuthor, StringComparison.OrdinalIgnoreCase))).ToList();
            _allAuthors = panelAuthor == null;

            _showCreated = showCreated || !showModified; // в панели могли оставить только «удалённые» — тогда показываем всё
            _showModified = showModified || !showCreated;

            Levels = levels.Select(l => new Option<long>(l.Name, l.Id)).ToList();
            _levelFrom = Levels.FirstOrDefault();
            _levelTo = Levels.FirstOrDefault();

            OkCommand = new RelayCommand(Ok);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        }

        public event Action<bool> RequestClose;
        public RelayCommand OkCommand { get; }
        public RelayCommand CancelCommand { get; }
        /// <summary>Что показать (после «Показать»).</summary>
        public ChangesViewRequest Result { get; private set; }

        // ── период ──

        public IReadOnlyList<Option<string>> Periods { get; }

        private Option<string> _selectedPeriod;
        public Option<string> SelectedPeriod
        {
            get => _selectedPeriod;
            set
            {
                if (value == null || !Set(ref _selectedPeriod, value)) return;
                OnPropertyChanged(nameof(IsCustom));
                if (IsCustom && string.IsNullOrWhiteSpace(_fromText)) FromText = DateTime.Today.ToString(DateFormat, Ru);
            }
        }

        public bool IsCustom => _selectedPeriod?.Value == "custom";

        private string _fromText = "";
        public string FromText { get => _fromText; set => Set(ref _fromText, value); }

        private string _toText = "";
        /// <summary>Пусто — до сейчас.</summary>
        public string ToText { get => _toText; set => Set(ref _toText, value); }

        // ── авторы ──

        public IReadOnlyList<AuthorChoice> Authors { get; }

        private bool _allAuthors;
        public bool AllAuthors
        {
            get => _allAuthors;
            set
            {
                if (!Set(ref _allAuthors, value)) return;
                OnPropertyChanged(nameof(PickAuthors));
                if (!value && Authors.All(a => a.IsChecked)) foreach (var a in Authors) a.IsChecked = false;
            }
        }

        public bool PickAuthors => !_allAuthors;

        // ── что и как показать ──

        private bool _showCreated, _showModified, _showNoNet;
        public bool ShowCreated { get => _showCreated; set => Set(ref _showCreated, value); }
        public bool ShowModified { get => _showModified; set => Set(ref _showModified, value); }
        public bool ShowNoNetChange { get => _showNoNet; set => Set(ref _showNoNet, value); }

        private bool _colorByAuthor;
        public bool ColorByAction { get => !_colorByAuthor; set { if (value) ColorByAuthor = false; } }
        public bool ColorByAuthor
        {
            get => _colorByAuthor;
            set
            {
                if (!Set(ref _colorByAuthor, value)) return;
                OnPropertyChanged(nameof(ColorByAction));
            }
        }

        // ── цвета ──

        public IReadOnlyList<ColorChoice> ColorChoices { get; }
        public IReadOnlyList<Option<int>> BackgroundOptions { get; }
        public RelayCommand ResetColorsCommand { get; }

        private ColorChoice _createdColor, _modifiedColor, _noNetColor;
        public ColorChoice CreatedColor { get => _createdColor; set { if (value != null) Set(ref _createdColor, value); } }
        public ColorChoice ModifiedColor { get => _modifiedColor; set { if (value != null) Set(ref _modifiedColor, value); } }
        public ColorChoice NoNetColor { get => _noNetColor; set { if (value != null) Set(ref _noNetColor, value); } }

        private Option<int> _background;
        public Option<int> Background { get => _background; set { if (value != null) Set(ref _background, value); } }

        private bool _shaded;
        /// <summary>С тенями — объёмнее; без теней («Согласованные цвета») — цвет ровный и ярче.</summary>
        public bool Shaded { get => _shaded; set { if (Set(ref _shaded, value)) OnPropertyChanged(nameof(Flat)); } }
        public bool Flat { get => !_shaded; set { if (value) Shaded = false; } }

        /// <summary>Что выбрано сейчас — запомнить до следующего раза.</summary>
        public ChangesViewStyle ChosenStyle => new ChangesViewStyle
        {
            Created = _createdColor.Rgb,
            Modified = _modifiedColor.Rgb,
            NoNet = _noNetColor.Rgb,
            Rest = _background.Value,
            Shaded = _shaded
        };

        private void ApplyStyle(ChangesViewStyle c)
        {
            CreatedColor = Choice(c.Created, ChangesPalette.Created);
            ModifiedColor = Choice(c.Modified, ChangesPalette.Modified);
            NoNetColor = Choice(c.NoNet, ChangesPalette.NoNet);
            Background = BackgroundOptions.FirstOrDefault(o => o.Value == c.Rest)
                         ?? BackgroundOptions.First(o => o.Value == ChangesPalette.Background);
            Shaded = c.Shaded;
        }

        /// <summary>Цвет из палитры; сохранённого в ней больше нет — цвет по умолчанию.</summary>
        private ColorChoice Choice(uint rgb, uint fallback) =>
            ColorChoices.FirstOrDefault(x => x.Rgb == rgb) ?? ColorChoices.First(x => x.Rgb == fallback);

        // ── подрезка ──

        private ChangesCrop _crop = ChangesCrop.Changes;
        public bool CropChanges { get => _crop == ChangesCrop.Changes; set { if (value) SetCrop(ChangesCrop.Changes); } }
        public bool CropLevels { get => _crop == ChangesCrop.Levels; set { if (value) SetCrop(ChangesCrop.Levels); } }
        public bool CropNone { get => _crop == ChangesCrop.None; set { if (value) SetCrop(ChangesCrop.None); } }

        private void SetCrop(ChangesCrop crop)
        {
            _crop = crop;
            OnPropertyChanged(nameof(CropChanges));
            OnPropertyChanged(nameof(CropLevels));
            OnPropertyChanged(nameof(CropNone));
        }

        public bool HasLevels => Levels.Count > 0;
        public IReadOnlyList<Option<long>> Levels { get; }

        private Option<long> _levelFrom, _levelTo;
        public Option<long> LevelFrom { get => _levelFrom; set => Set(ref _levelFrom, value); }
        public Option<long> LevelTo { get => _levelTo; set => Set(ref _levelTo, value); }

        private bool _hideFloors, _hideRoofs, _hideCeilings;
        public bool HideFloors { get => _hideFloors; set => Set(ref _hideFloors, value); }
        public bool HideRoofs { get => _hideRoofs; set => Set(ref _hideRoofs, value); }
        public bool HideCeilings { get => _hideCeilings; set => Set(ref _hideCeilings, value); }

        private string _error = "";
        public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrEmpty(_error);

        private void Ok()
        {
            var r = new ChangesViewRequest
            {
                ShowCreated = _showCreated,
                ShowModified = _showModified,
                ShowNoNetChange = _showNoNet,
                ColorByAuthor = _colorByAuthor,
                Style = ChosenStyle,
                Crop = _crop,
                LevelFrom = _levelFrom?.Value,
                LevelTo = _levelTo?.Value,
                HideFloors = _hideFloors,
                HideRoofs = _hideRoofs,
                HideCeilings = _hideCeilings
            };
            if (!_showCreated && !_showModified && !_showNoNet)
            {
                Error = "Отметьте, что показать: созданные или изменённые.";
                return;
            }
            if (!_allAuthors)
            {
                r.Authors = Authors.Where(a => a.IsChecked).Select(a => a.User).ToList();
                if (r.Authors.Count == 0)
                {
                    Error = "Выберите хотя бы одного автора или отметьте «Все авторы».";
                    return;
                }
            }
            if (_crop == ChangesCrop.Levels && (_levelFrom == null || _levelTo == null))
            {
                Error = "Выберите этажи: с какого и по какой уровень.";
                return;
            }

            var today = new DateTimeOffset(DateTime.Today);
            switch (_selectedPeriod.Value)
            {
                case "today": r.From = today; r.PeriodText = "сегодня"; break;
                case "yesterday": r.From = today.AddDays(-1); r.To = today; r.PeriodText = "вчера"; break;
                case "3": r.From = today.AddDays(-2); r.PeriodText = "3 дня"; break;
                case "7": r.From = today.AddDays(-6); r.PeriodText = "7 дней"; break;
                case "30": r.From = today.AddDays(-29); r.PeriodText = "30 дней"; break;
                case "90": r.From = today.AddDays(-89); r.PeriodText = "90 дней"; break;
                case "365": r.From = today.AddDays(-364); r.PeriodText = "год"; break;
                case "sync": r.SinceMyLastSync = true; r.PeriodText = "с вашей последней синхронизации"; break;
                case "all": r.PeriodText = "всё время"; break;
                default:
                    if (!TryDate(_fromText, out var from))
                    {
                        Error = "Дата «с» — в виде ДД.ММ.ГГГГ, например " + DateTime.Today.ToString(DateFormat, Ru) + ".";
                        return;
                    }
                    r.From = new DateTimeOffset(from);
                    r.PeriodText = "с " + from.ToString(DateFormat, Ru);
                    if (!string.IsNullOrWhiteSpace(_toText))
                    {
                        if (!TryDate(_toText, out var to))
                        {
                            Error = "Дата «по» — в виде ДД.ММ.ГГГГ или пусто (до сейчас).";
                            return;
                        }
                        if (to < from)
                        {
                            Error = "Дата «по» раньше даты «с».";
                            return;
                        }
                        r.To = new DateTimeOffset(to.AddDays(1)); // «по» включительно
                        r.PeriodText += " по " + to.ToString(DateFormat, Ru);
                    }
                    break;
            }
            if (r.Authors != null) r.AuthorsText = string.Join(", ", Authors.Where(a => a.IsChecked).Select(a => a.Label));
            Error = "";
            Result = r;
            RequestClose?.Invoke(true);
        }

        private static bool TryDate(string text, out DateTime date) =>
            DateTime.TryParseExact((text ?? "").Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy" }, Ru, DateTimeStyles.None, out date);
    }
}
