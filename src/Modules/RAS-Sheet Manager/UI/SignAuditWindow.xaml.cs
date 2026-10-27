using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.UI
{
    /// <summary>Находка в окне «Актуализировать»: галочка — исправить; у текста для штампа ГИП без инициала — поле для инициала.</summary>
    public sealed class SignAuditItem : INotifyPropertyChanged
    {
        private bool _checked;
        private string _initial;
        private readonly string _baseText;

        public SignAuditItem(SignIssue issue)
        {
            Issue = issue;
            _checked = issue.Fixable;
            NeedsInitial = issue.Kind == SignIssueKind.Display && !issue.Fixable;
            _baseText = issue.Text;
        }

        public SignIssue Issue { get; }
        public bool NeedsInitial { get; }
        public string Name => string.IsNullOrEmpty(Issue.Name) ? "Семейство" : Issue.Name;
        public string Text => NeedsInitial && Issue.Value != null ? Lead + " — вписать «" + Issue.Value + "»" : _baseText;
        private string Lead => _baseText.IndexOf(" — ", System.StringComparison.Ordinal) is var i && i > 0 ? _baseText.Substring(0, i) : _baseText;
        public bool Fixable => Issue.Fixable;
        /// <summary>Только сведения: исправить нельзя, поле инициала не нужно.</summary>
        public bool Info => !Issue.Fixable && !NeedsInitial;
        public Visibility CheckVisibility => Info ? Visibility.Hidden : Visibility.Visible;
        public Visibility InitialVisibility => NeedsInitial ? Visibility.Visible : Visibility.Collapsed;

        public bool Checked
        {
            get => _checked;
            set { _checked = value && Issue.Fixable; OnChanged(); Changed?.Invoke(); }
        }

        /// <summary>Вписали инициал — текст для штампа готов, находку можно исправить.</summary>
        public string Initial
        {
            get => _initial;
            set
            {
                _initial = value;
                Issue.Value = SignAudit.Display(Issue.Name, value);
                Issue.Fixable = Issue.Value != null;
                _checked = Issue.Fixable;
                OnChanged();
                OnChanged(nameof(Checked));
                OnChanged(nameof(Fixable));
                OnChanged(nameof(Text));
                Changed?.Invoke();
            }
        }

        public System.Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// «Актуализировать»: что не так в семействе подписей (SignAudit) — списком; что можно исправить, отмечено галочкой.
    /// Result — отмеченные находки (исправит окно «Подпись» одной группой действий), null — закрыли.
    /// </summary>
    public partial class SignAuditWindow : Window
    {
        private readonly List<SignAuditItem> _items;

        public SignAuditWindow(string heading, List<SignIssue> issues, string note)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            Heading.Text = heading;
            Note.Text = note ?? "";
            // сначала то, что можно исправить; сведения — в конце
            _items = issues.Select(i => new SignAuditItem(i)).OrderBy(i => i.Info).ToList();
            foreach (var i in _items) i.Changed = Counts;
            List.ItemsSource = _items;
            Counts();
        }

        public List<SignIssue> Result { get; private set; }

        private void Counts()
        {
            var n = _items.Count(i => i.Checked && i.Fixable);
            FixButton.Content = n > 0 ? "Исправить отмеченные (" + n + ")" : "Исправить отмеченные";
            FixButton.IsEnabled = n > 0;
        }

        private void Fix_Click(object sender, RoutedEventArgs e)
        {
            Result = _items.Where(i => i.Checked && i.Fixable).Select(i => i.Issue).ToList();
            DialogResult = true;
        }
    }
}
