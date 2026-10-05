using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Строка окна «Сортировка и группировка»: «Сортировать по / Затем по», направление, заголовок.</summary>
    public sealed class SortLevelViewModel : ObservableObject
    {
        public SortLevelViewModel(string caption, IReadOnlyList<Option<SortField?>> fields, SortLevel level, bool allowNone)
        {
            Caption = caption;
            Fields = allowNone ? fields : fields.Where(f => f.Value != null).ToList();
            _field = Fields.FirstOrDefault(f => level != null && f.Value == level.Field) ?? Fields[0];
            _descending = level?.Descending ?? false;
            _header = level?.Header ?? false;
            GroupName = "SpySortDir" + Guid.NewGuid().ToString("N");
        }

        public string Caption { get; }
        public IReadOnlyList<Option<SortField?>> Fields { get; }
        /// <summary>Своя группа переключателей направления у каждого уровня.</summary>
        public string GroupName { get; }

        private Option<SortField?> _field;
        public Option<SortField?> Field
        {
            get => _field;
            set
            {
                if (value == null || !Set(ref _field, value)) return;
                OnPropertyChanged(nameof(HasField));
            }
        }

        public bool HasField => _field?.Value != null;

        private bool _descending;
        public bool Descending
        {
            get => _descending;
            set { if (Set(ref _descending, value)) OnPropertyChanged(nameof(Ascending)); }
        }

        public bool Ascending
        {
            get => !_descending;
            set { if (value) Descending = false; }
        }

        private bool _header;
        /// <summary>«Заголовок»: строка над каждой группой одинаковых значений.</summary>
        public bool Header { get => _header; set => Set(ref _header, value); }

        public SortLevel ToLevel() => HasField ? new SortLevel(_field.Value.Value, _descending, _header) : null;

        public void Load(SortLevel level)
        {
            Field = Fields.FirstOrDefault(f => level != null && f.Value == level.Field) ?? Fields[0];
            Descending = level?.Descending ?? false;
            Header = level?.Header ?? false;
        }
    }

    /// <summary>Окно «Сортировка и группировка» — как вкладка «Сортировка/группирование» в свойствах спецификации Revit.</summary>
    public sealed class SortViewModel : ObservableObject
    {
        public SortViewModel(IReadOnlyList<SortLevel> current, bool groupByOperation, string paramName)
        {
            var fields = new List<Option<SortField?>> { new Option<SortField?>("(нет)", null) };
            foreach (SortField f in Enum.GetValues(typeof(SortField)))
            {
                if (f == SortField.Param)
                {
                    if (!string.IsNullOrEmpty(paramName)) fields.Add(new Option<SortField?>("Параметр «" + paramName + "»", f));
                    continue;
                }
                fields.Add(new Option<SortField?>(RowSorting.Label(f), f));
            }
            Levels = Enumerable.Range(0, RowSorting.MaxLevels)
                .Select(i => new SortLevelViewModel(i == 0 ? "Сортировать по:" : "Затем по:", fields,
                    current != null && i < current.Count ? current[i] : null, allowNone: i > 0))
                .ToList();
            _groupByOperation = groupByOperation;
            OkCommand = new RelayCommand(() => RequestClose?.Invoke(true));
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
            ResetCommand = new RelayCommand(Reset);
        }

        public IReadOnlyList<SortLevelViewModel> Levels { get; }

        private bool _groupByOperation;
        /// <summary>Записи одной операции Revit — одной строкой (раскрывается ▸).</summary>
        public bool GroupByOperation { get => _groupByOperation; set => Set(ref _groupByOperation, value); }

        public RelayCommand OkCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ResetCommand { get; }

        /// <summary>true — применить, false — отмена.</summary>
        public event Action<bool> RequestClose;

        /// <summary>Выбранные уровни сверху вниз (пустые и повторяющиеся поля пропускаются).</summary>
        public List<SortLevel> Result =>
            Levels.Select(l => l.ToLevel()).Where(l => l != null)
                .GroupBy(l => l.Field).Select(g => g.First())
                .ToList();

        /// <summary>«Сбросить»: по времени, новые сверху, без заголовков; операции — одной строкой.</summary>
        private void Reset()
        {
            var defaults = RowSorting.Default();
            for (int i = 0; i < Levels.Count; i++) Levels[i].Load(i < defaults.Count ? defaults[i] : null);
            GroupByOperation = true;
        }
    }
}
