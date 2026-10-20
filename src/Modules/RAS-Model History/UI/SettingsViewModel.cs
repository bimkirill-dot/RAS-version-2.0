using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly SpySettings _edit;

        public SettingsViewModel(SpySettings current)
        {
            _edit = current.Clone();
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
            ThemeOptions = new List<Option<string>>
            {
                new Option<string>("Как в Revit", "Auto"),
                new Option<string>("Светлая", "Light"),
                new Option<string>("Тёмная", "Dark")
            };
            _selectedTheme = ThemeOptions.FirstOrDefault(o => string.Equals(o.Value, _edit.UiTheme, StringComparison.OrdinalIgnoreCase))
                             ?? ThemeOptions[0];
        }

        /// <summary>Тема панели и окна настроек.</summary>
        public IReadOnlyList<Option<string>> ThemeOptions { get; }

        private Option<string> _selectedTheme;
        public Option<string> SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (value == null) return;
                _selectedTheme = value;
                _edit.UiTheme = value.Value;
                OnPropertyChanged();
            }
        }

        /// <summary>true — сохранить, false — отмена.</summary>
        public event Action<bool> RequestClose;

        public SpySettings Result => _edit;

        public bool TrackParameters { get => _edit.TrackParameters; set { _edit.TrackParameters = value; OnPropertyChanged(); } }
        public bool SnapshotOnOpen { get => _edit.SnapshotOnOpen; set { _edit.SnapshotOnOpen = value; OnPropertyChanged(); } }
        public bool SnapshotAllElements { get => _edit.SnapshotAllElements; set { _edit.SnapshotAllElements = value; OnPropertyChanged(); } }
        public bool PersistSnapshots { get => _edit.PersistSnapshots; set { _edit.PersistSnapshots = value; OnPropertyChanged(); } }
        public string SnapshotCacheDays { get => Str(_edit.SnapshotCacheDays); set => _edit.SnapshotCacheDays = Int(value, _edit.SnapshotCacheDays); }
        public bool DetectRemoteChanges { get => _edit.DetectRemoteChanges; set { _edit.DetectRemoteChanges = value; OnPropertyChanged(); } }
        public bool RecordNoise { get => _edit.RecordNoise; set { _edit.RecordNoise = value; OnPropertyChanged(); } }
        public bool TrackFamilyDocuments { get => _edit.TrackFamilyDocuments; set { _edit.TrackFamilyDocuments = value; OnPropertyChanged(); } }

        public string MaxRecordsPerTransaction { get => Str(_edit.MaxRecordsPerTransaction); set => _edit.MaxRecordsPerTransaction = Int(value, _edit.MaxRecordsPerTransaction); }
        public string MaxParamDiffPerTransaction { get => Str(_edit.MaxParamDiffPerTransaction); set => _edit.MaxParamDiffPerTransaction = Int(value, _edit.MaxParamDiffPerTransaction); }
        public string MaxSelectionSnapshot { get => Str(_edit.MaxSelectionSnapshot); set => _edit.MaxSelectionSnapshot = Int(value, _edit.MaxSelectionSnapshot); }
        public string MaxSnapshotElements { get => Str(_edit.MaxSnapshotElements); set => _edit.MaxSnapshotElements = Int(value, _edit.MaxSnapshotElements); }
        public string DefaultPeriodDays { get => Str(_edit.DefaultPeriodDays); set => _edit.DefaultPeriodDays = Int(value, _edit.DefaultPeriodDays); }
        public string MaxDisplayedRows { get => Str(_edit.MaxDisplayedRows); set => _edit.MaxDisplayedRows = Int(value, _edit.MaxDisplayedRows); }

        public RelayCommand SaveCommand { get; }
        public RelayCommand CancelCommand { get; }

        private void Save()
        {
            _edit.Normalize();
            RequestClose?.Invoke(true);
        }

        private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static int Int(string s, int fallback) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
