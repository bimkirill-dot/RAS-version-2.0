using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Окно «Настройки» — только у BIM-специалистов (кнопка с «доступна=» в ribbon.txt): всё, кроме темы, — для всей команды,
    /// сохраняется в общий файл на сервере (SettingsFiles).
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly SpySettings _edit;

        /// <param name="defaultFolder">Папка журналов по умолчанию (когда поле пустое).</param>
        /// <param name="serverUnavailable">Сервер не ответил: показаны последние известные настройки, сохранить не получится.</param>
        public SettingsViewModel(SpySettings current, string defaultFolder, bool serverUnavailable)
        {
            _edit = current.Clone();
            DefaultFolder = defaultFolder;
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
            ResetFolderCommand = new RelayCommand(() => JournalFolder = "");
            BrowseFolderCommand = new RelayCommand(BrowseFolder);
            ThemeOptions = new List<Option<string>>
            {
                new Option<string>("Как в Revit", "Auto"),
                new Option<string>("Светлая", "Light"),
                new Option<string>("Тёмная", "Dark")
            };
            _selectedTheme = ThemeOptions.FirstOrDefault(o => string.Equals(o.Value, _edit.UiTheme, StringComparison.OrdinalIgnoreCase))
                             ?? ThemeOptions[0];
            ChangedNote = string.IsNullOrEmpty(current.ChangedBy)
                ? "Общие настройки ещё не меняли — действуют исходные."
                : "Последнее изменение: " + current.ChangedBy + (string.IsNullOrEmpty(current.ChangedAt) ? "" : ", " + current.ChangedAt) + ".";
            if (serverUnavailable)
                ServerNote = "Сервер не ответил — показаны последние известные настройки. Сохранить сейчас не получится.";
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

        public string DefaultFolder { get; }
        public string ChangedNote { get; }
        public string ServerNote { get; }
        public bool HasServerNote => ServerNote != null;

        /// <summary>true — сохранить, false — отмена.</summary>
        public event Action<bool> RequestClose;

        public SpySettings Result => _edit;

        public string JournalFolder { get => _edit.JournalFolder; set { _edit.JournalFolder = value ?? ""; OnPropertyChanged(); } }

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
        public RelayCommand ResetFolderCommand { get; }
        public RelayCommand BrowseFolderCommand { get; }

        private void Save()
        {
            _edit.Normalize();
            RequestClose?.Invoke(true);
        }

        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Папка журналов RAS-Model History",
                InitialDirectory = string.IsNullOrWhiteSpace(JournalFolder) ? DefaultFolder : JournalFolder
            };
            if (dialog.ShowDialog() == true) JournalFolder = dialog.FolderName;
        }

        private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static int Int(string s, int fallback) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
