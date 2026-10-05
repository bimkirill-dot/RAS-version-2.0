using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Значение в выпадающем списке «Значение»: «Стены» и сколько записей с ним.</summary>
    public sealed class FilterValueOption
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public FilterValueOption(string value, int count)
        {
            Value = value;
            Count = count;
        }

        public string Value { get; }
        public int Count { get; }
        public string CountText => Count.ToString("N0", Ru);
        public override string ToString() => Value;
    }

    /// <summary>Поле в списке «Поле» с названием группы («Запись», «Элемент»…).</summary>
    public sealed class FilterFieldOption
    {
        public FilterFieldOption(FilterField field, string group)
        {
            Field = field;
            Group = group;
            Label = RecordFilter.FieldLabel(field);
        }

        public FilterField Field { get; }
        public string Group { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>Строка окна «Фильтр»: [поле] [параметр] [условие] [значение] ✕.</summary>
    public sealed class FilterRowViewModel : ObservableObject
    {
        private readonly FilterViewModel _owner;

        public FilterRowViewModel(FilterViewModel owner, FilterCondition c)
        {
            _owner = owner;
            // у каждой строки свой вид списка: общий синхронизировал бы выбор во всех строках
            var view = new ListCollectionView(owner.FieldOptions.ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FilterFieldOption.Group)));
            Fields = view;
            _field = owner.FieldOptions.FirstOrDefault(f => f.Field == (c?.Field ?? FilterField.Category)) ?? owner.FieldOptions[0];
            _paramName = c?.ParamName ?? "";
            _operator = OperatorOptions.First(o => o.Value == (c?.Operator ?? FilterOperator.Equals));
            _value = c?.Value ?? "";
            RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        }

        public static IReadOnlyList<Option<FilterOperator>> OperatorOptions { get; } =
            RecordFilter.Operators.Select(o => new Option<FilterOperator>(RecordFilter.OperatorLabel(o), o)).ToList();

        public ICollectionView Fields { get; }
        public IReadOnlyList<Option<FilterOperator>> Operators => OperatorOptions;
        public ObservableCollection<string> ParamNames => _owner.ParamNames;

        private FilterFieldOption _field;
        public FilterFieldOption Field
        {
            get => _field;
            set
            {
                if (value == null || !Set(ref _field, value)) return;
                OnPropertyChanged(nameof(NeedsParam));
                OnPropertyChanged(nameof(ValueToolTip));
                ResetSuggestions();
                _owner.Changed();
            }
        }

        /// <summary>«Было», «Стало» и «Параметр элемента» — нужен выбор параметра.</summary>
        public bool NeedsParam => RecordFilter.NeedsParam(_field.Field);

        private string _paramName;
        public string ParamName
        {
            get => _paramName;
            set
            {
                if (!Set(ref _paramName, value ?? "")) return;
                ResetSuggestions();
                _owner.Changed();
            }
        }

        private Option<FilterOperator> _operator;
        public Option<FilterOperator> Operator
        {
            get => _operator;
            set
            {
                if (value == null || !Set(ref _operator, value)) return;
                OnPropertyChanged(nameof(NeedsValue));
                _owner.Changed();
            }
        }

        /// <summary>«имеет значение» / «не имеет значения» — без значения.</summary>
        public bool NeedsValue => RecordFilter.NeedsValue(_operator.Value);

        private string _value;
        public string Value
        {
            get => _value;
            set { if (Set(ref _value, value ?? "")) _owner.Changed(); }
        }

        public string ValueToolTip => _field.Field == FilterField.ChangedParam
            ? "Имя параметра. «равно» — параметр менялся в записи, «не равно» — не менялся."
            : "Значение из списка (в скобках — сколько записей) или своё. Числа сравниваются как числа: «больше 3000» подходит и к «3500 мм».";

        /// <summary>Значения для выпадающего списка — заполняются при открытии (подсчёт по всему списку).</summary>
        public ObservableCollection<FilterValueOption> Suggestions { get; } = new ObservableCollection<FilterValueOption>();
        private bool _suggestionsLoaded;

        public void LoadSuggestions()
        {
            if (_suggestionsLoaded) return;
            _suggestionsLoaded = true;
            Suggestions.Clear();
            foreach (var v in _owner.Values(_field.Field, _paramName)) Suggestions.Add(v);
        }

        private void ResetSuggestions()
        {
            _suggestionsLoaded = false;
            Suggestions.Clear();
        }

        public RelayCommand RemoveCommand { get; }

        public FilterCondition ToCondition() => new FilterCondition(_field.Field, _operator.Value, _value, _paramName);
    }

    /// <summary>Окно «Фильтр» — как правила фильтров Revit: строки условий, «И» / «ИЛИ».</summary>
    public sealed class FilterViewModel : ObservableObject
    {
        private readonly Func<FilterField, string, List<FilterValueOption>> _values;
        private readonly Func<RecordFilter, string> _preview;
        private readonly DispatcherTimer _previewTimer;

        public FilterViewModel(RecordFilter current, ObservableCollection<string> paramNames,
            Func<FilterField, string, List<FilterValueOption>> values, Func<RecordFilter, string> preview)
        {
            _values = values;
            _preview = preview;
            ParamNames = paramNames ?? new ObservableCollection<string>();
            FieldOptions = RecordFilter.FieldGroups.SelectMany(g => g.Value.Select(f => new FilterFieldOption(f, g.Key))).ToList();
            current ??= RecordFilter.Empty;
            _isOr = current.Any;
            foreach (var c in current.Conditions) Rows.Add(new FilterRowViewModel(this, c));
            if (Rows.Count == 0) Rows.Add(new FilterRowViewModel(this, null));

            AddCommand = new RelayCommand(() =>
            {
                Rows.Add(new FilterRowViewModel(this, null));
                Changed();
            });
            ResetCommand = new RelayCommand(() =>
            {
                Rows.Clear();
                Rows.Add(new FilterRowViewModel(this, null));
                Changed();
            });
            OkCommand = new RelayCommand(() => RequestClose?.Invoke(true));
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                UpdatePreview();
            };
            UpdatePreview();
        }

        public IReadOnlyList<FilterFieldOption> FieldOptions { get; }
        public ObservableCollection<string> ParamNames { get; }
        public ObservableCollection<FilterRowViewModel> Rows { get; } = new ObservableCollection<FilterRowViewModel>();

        private bool _isOr;
        /// <summary>«ИЛИ»: запись подходит, если выполняется хотя бы одно условие.</summary>
        public bool IsOr
        {
            get => _isOr;
            set
            {
                if (!Set(ref _isOr, value)) return;
                OnPropertyChanged(nameof(IsAnd));
                Changed();
            }
        }

        public bool IsAnd
        {
            get => !_isOr;
            set { if (value) IsOr = false; }
        }

        private string _previewText = "";
        public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }

        private string _incompleteText = "";
        /// <summary>Незаполненные строки не действуют — об этом стоит сказать.</summary>
        public string IncompleteText { get => _incompleteText; private set => Set(ref _incompleteText, value); }

        public RelayCommand AddCommand { get; }
        public RelayCommand ResetCommand { get; }
        public RelayCommand OkCommand { get; }
        public RelayCommand CancelCommand { get; }

        /// <summary>true — применить, false — отмена.</summary>
        public event Action<bool> RequestClose;

        public RecordFilter Result => new RecordFilter(Rows.Select(r => r.ToCondition()), _isOr);

        internal List<FilterValueOption> Values(FilterField field, string paramName)
        {
            if (RecordFilter.NeedsParam(field) && string.IsNullOrWhiteSpace(paramName)) return new List<FilterValueOption>();
            try { return _values?.Invoke(field, paramName?.Trim()) ?? new List<FilterValueOption>(); }
            catch (Exception) { return new List<FilterValueOption>(); }
        }

        internal void Remove(FilterRowViewModel row)
        {
            Rows.Remove(row);
            if (Rows.Count == 0) Rows.Add(new FilterRowViewModel(this, null));
            Changed();
        }

        /// <summary>Условия поменялись — пересчитать «Подходит записей» (с задержкой, пока человек печатает).</summary>
        internal void Changed()
        {
            if (_previewTimer == null) return; // ещё в конструкторе
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview()
        {
            var incomplete = Rows.Count(r => !r.ToCondition().IsComplete);
            // единственная пустая строка нового фильтра — не «незаполненная», а приглашение
            IncompleteText = incomplete == 0 || (Rows.Count == 1 && string.IsNullOrEmpty(Rows[0].Value) && Rows[0].NeedsValue)
                ? ""
                : "Незаполненные условия не учитываются: " + incomplete + ".";
            try { PreviewText = _preview?.Invoke(Result) ?? ""; }
            catch (Exception) { PreviewText = ""; }
        }
    }
}
