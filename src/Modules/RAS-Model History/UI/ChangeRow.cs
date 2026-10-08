using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Общие для всех строк сведения: какой параметр показать в столбце и где взять его текущее значение.</summary>
    public sealed class RowContext
    {
        /// <summary>Параметр, по которому сортируют (столбец параметра, по заголовку которого щёлкнули); null — нет.</summary>
        public string ParamName { get; set; }
        /// <summary>(id элемента, имя параметра) → последнее известное значение по снимку модели.</summary>
        public Func<long, string, string> Lookup { get; set; }
        /// <summary>Дошло ли изменение до хранилища — по событиям сеансов.</summary>
        public Dictionary<ChangeRecord, SyncStatus> Statuses { get; set; }
        /// <summary>Модель с совместной работой (от этого зависят подписи статуса).</summary>
        public bool IsWorkshared { get; set; }
        /// <summary>Записи, убранные из журнала при актуализации.</summary>
        public HashSet<string> Excluded { get; set; }
        /// <summary>Предпросмотр актуализации: id записи → чем она не соответствует модели.</summary>
        public Dictionary<string, string> PreviewReasons { get; set; }
    }

    /// <summary>
    /// Строка таблицы — обёртка записи журнала с готовыми для показа строками.
    /// Бывает трёх видов: обычная запись, заголовок операции Revit (несколько записей одной транзакции)
    /// и запись внутри раскрытой операции.
    /// </summary>
    public sealed class ChangeRow
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private readonly RowContext _ctx;
        /// <summary>Значения столбцов параметров — читаются из снимков один раз.</summary>
        private Dictionary<string, string> _paramValues;

        public ChangeRow(ChangeRecord r, RowContext ctx = null)
        {
            Record = r;
            _ctx = ctx;
        }

        public ChangeRow(RowEntry entry, RowContext ctx)
            : this(entry.Record, ctx)
        {
            IsGroupHeader = entry.IsHeader;
            IsGroupChild = entry.IsChild;
            IsExpanded = entry.Expanded;
            GroupKey = entry.Key;
            Members = entry.Members;
        }

        /// <summary>Для заголовка операции — первая (самая новая) запись операции.</summary>
        public ChangeRecord Record { get; }

        // ── операция (группа записей одной транзакции) ──

        public bool IsGroupHeader { get; }
        public bool IsGroupChild { get; }
        public bool IsExpanded { get; }
        public string GroupKey { get; }
        /// <summary>Записи операции (только у заголовка).</summary>
        public IReadOnlyList<ChangeRecord> Members { get; }

        /// <summary>Сколько элементов затронула операция (сводные записи считаются по числу элементов в них).</summary>
        public int GroupCount => Members?.Sum(m => m.SummaryCount ?? 1) ?? 0;

        // ── группы «Сортировки и группировки» (заголовки на всю ширину таблицы) ──

        private RowGroup[] _groups;

        /// <summary>Группы строки сверху вниз (по уровням с «Заголовком»); null — без групп.</summary>
        public void SetGroups(RowGroup[] groups) => _groups = groups;

        public RowGroup Group1 => Group(0);
        public RowGroup Group2 => Group(1);
        public RowGroup Group3 => Group(2);
        public RowGroup Group4 => Group(3);

        private RowGroup Group(int i) => _groups != null && i < _groups.Length ? _groups[i] : null;

        /// <summary>▸ / ▾ у заголовка операции.</summary>
        public string ExpandGlyph => !IsGroupHeader ? "" : IsExpanded ? "" : "";

        // ── статус ──

        public SyncStatus Status =>
            _ctx?.Statuses != null && _ctx.Statuses.TryGetValue(Record, out var st) ? st : SyncStatus.Unknown;

        /// <summary>Чем запись не соответствует модели (предпросмотр актуализации), иначе null.</summary>
        public string PreviewReason =>
            Record.RecordId != null && _ctx?.PreviewReasons != null && _ctx.PreviewReasons.TryGetValue(Record.RecordId, out var why)
                ? why : null;

        /// <summary>Запись убрана из журнала при актуализации (видна только с флажком «Показать убранные»).</summary>
        public bool IsExcluded => Record.RecordId != null && _ctx?.Excluded != null && _ctx.Excluded.Contains(Record.RecordId);

        public string StatusText
        {
            get
            {
                var why = PreviewReason;
                if (why != null) return "⚠ " + why;
                if (IsExcluded) return "⊘ убрано при актуализации";
                return SyncStatusCalculator.Text(Status, _ctx?.IsWorkshared ?? true);
            }
        }

        public string StatusExplanation
        {
            get
            {
                var why = PreviewReason;
                if (why != null) return "Не соответствует модели: " + why + ".";
                if (IsExcluded) return "Запись убрана из журнала при актуализации: изменение не соответствовало модели.";
                return SyncStatusCalculator.Explain(Status, _ctx?.IsWorkshared ?? true);
            }
        }

        /// <summary>Для значка статуса: ok / pending / void / preview / excluded / пусто.</summary>
        public string StatusKey
        {
            get
            {
                if (PreviewReason != null) return "preview";
                if (IsExcluded) return "excluded";
                switch (Status)
                {
                    case SyncStatus.InCentral: return "ok";
                    case SyncStatus.NotSynced:
                    case SyncStatus.SavedLocally: return "pending";
                    case SyncStatus.Discarded:
                    case SyncStatus.LostLocal: return "void";
                    default: return "";
                }
            }
        }

        /// <summary>Значок статуса (шрифт значков Windows): ✓, часы, ✕, ⚠, ⊘.</summary>
        public string StatusGlyph
        {
            get
            {
                switch (StatusKey)
                {
                    case "ok": return "";
                    case "pending": return "";
                    case "void": return "";
                    case "preview": return "";
                    case "excluded": return "";
                    default: return "";
                }
            }
        }

        /// <summary>Текст рядом со значком статуса: «в хранилище», «не синхронизировано», в режиме актуализации — причина.</summary>
        public string StatusLabel
        {
            get
            {
                var why = PreviewReason;
                if (why != null) return why;
                if (IsExcluded) return "убрано при актуализации";
                return StatusShort(SyncStatusCalculator.Text(Status, _ctx?.IsWorkshared ?? true));
            }
        }

        /// <summary>«✓ в хранилище» → «в хранилище»: символ заменяет цветной значок.</summary>
        public static string StatusShort(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var space = text.IndexOf(' ');
            return space > 0 && space <= 2 && !char.IsLetterOrDigit(text[0]) ? text.Substring(space + 1) : text;
        }

        /// <summary>Подсказка к значку статуса.</summary>
        public string StatusTip
        {
            get
            {
                var text = StatusText;
                var explain = StatusExplanation;
                if (string.IsNullOrEmpty(text)) return "Статус неизвестен: запись сделана старой версией плагина.";
                return string.IsNullOrEmpty(explain) ? text : text + Environment.NewLine + explain;
            }
        }

        /// <summary>Изменения фактически не было: не попало в хранилище или убрано при актуализации.</summary>
        public bool IsVoid => IsExcluded || SyncStatusCalculator.IsVoid(Status);

        /// <summary>Значение параметра, по которому сортируют (RowContext.ParamName).</summary>
        public string ParamValue => ParamValueOf(_ctx?.ParamName);

        /// <summary>
        /// Значение параметра для столбца параметра:
        /// изменён в этой записи — «было → стало»; удалён/создан — значение из записи журнала;
        /// иначе — последнее известное значение по снимку модели.
        /// </summary>
        public string ParamValueOf(string name)
        {
            if (string.IsNullOrEmpty(name) || Record.Kind == ChangeKind.Event || IsGroupHeader) return "";
            _paramValues ??= new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            if (_paramValues.TryGetValue(name, out var cached)) return cached;
            string v = null;
            var changed = Record.Params?.FirstOrDefault(p => Same(p.Name, name));
            if (changed != null) v = Short(changed.Old, 60) + " → " + Short(changed.New, 60);
            if (v == null && Record.State != null)
                v = Record.State.FirstOrDefault(p => Same(p.Name, name))?.Value;
            if (v == null && Record.ElementId > 0)
            {
                try { v = _ctx?.Lookup?.Invoke(Record.ElementId, name); }
                catch (Exception) { v = null; }
            }
            return _paramValues[name] = v ?? "";
        }

        /// <summary>Удалён вместе с элементом, от которого зависел: в «Что изменилось» — ссылка на запись его удаления.</summary>
        public bool HasParentLink => !IsGroupHeader && Record.Kind == ChangeKind.Deleted && Record.ParentId.HasValue;

        /// <summary>«Удалён вместе с: Стены · Базовая стена».</summary>
        public string ParentText =>
            !HasParentLink ? "" : "Удалён вместе с: " + (string.IsNullOrEmpty(Record.ParentName) ? "элемент ID " + Record.ParentId : Record.ParentName);

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

        // ── время ──

        /// <summary>Полная дата и время (подробности, CSV, подсказка).</summary>
        public string TimeText => Record.Time.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", Ru);

        /// <summary>Для таблицы: «28.09.2026 14:32» (секунды — в подсказке и подробностях).</summary>
        public string TimeShort => FormatShort(Record.Time.LocalDateTime);

        public static string FormatShort(DateTime t) => t.ToString("dd.MM.yyyy HH:mm", Ru);

        // ── действие ──

        /// <summary>Операция, в которой есть и созданные, и изменённые, и удалённые элементы.</summary>
        public bool IsMixed => IsGroupHeader && Members != null && Members.Select(m => m.Kind).Distinct().Count() > 1;

        /// <summary>«Создан» / «Изменён» / «Удалён» — без отметки отмены или повтора.</summary>
        public static string KindLabel(ChangeKind kind)
        {
            switch (kind)
            {
                case ChangeKind.Created: return "Создан";
                case ChangeKind.Modified: return "Изменён";
                case ChangeKind.Deleted: return "Удалён";
                default: return "Событие";
            }
        }

        public string KindText
        {
            get
            {
                if (IsMixed) return "Разные";
                var k = KindLabel(Record.Kind);
                // «Удалён (отмена)» читалось как «удаление отменено», а значит обратное — пишем, что именно отменили
                switch (Record.Operation)
                {
                    case OperationKind.Undo:
                        return Record.Kind == ChangeKind.Created ? k + " — отмена удаления"
                            : Record.Kind == ChangeKind.Deleted ? k + " — отмена создания"
                            : k + " — отмена правки";
                    case OperationKind.Redo: return k + " — повтор";
                    case OperationKind.Rollback: return k + " — откат группы";
                }
                return k;
            }
        }

        /// <summary>Для цвета действия: created / modified / deleted / event / void / mixed.</summary>
        public string KindKey
        {
            get
            {
                if (IsMixed) return "mixed";
                if (IsVoid) return "void";
                switch (Record.Kind)
                {
                    case ChangeKind.Created: return "created";
                    case ChangeKind.Modified: return "modified";
                    case ChangeKind.Deleted: return "deleted";
                    default:
                        switch (Record.EventType)
                        {
                            case ModelEventType.Commit: return "created";
                            case ModelEventType.Lost: return "deleted";
                            case ModelEventType.Exclude:
                            case ModelEventType.Restore: return "modified";
                            default: return "event";
                        }
                }
            }
        }


        /// <summary>Тип события модели — для вкладки «События модели».</summary>
        public string EventTypeText
        {
            get
            {
                switch (Record.EventType)
                {
                    case ModelEventType.Open: return "Открытие";
                    case ModelEventType.Close: return "Закрытие";
                    case ModelEventType.Commit: return (_ctx?.IsWorkshared ?? true) ? "Синхронизация" : "Сохранение";
                    case ModelEventType.SaveLocal: return "Сохранение локальной копии";
                    case ModelEventType.Resume: return "Продолжение работы";
                    case ModelEventType.Lost: return "Изменения потеряны";
                    case ModelEventType.Reload: return "Обновление из хранилища";
                    case ModelEventType.Exclude: return "Актуализация журнала";
                    case ModelEventType.Restore: return "Возврат в журнал";
                    default: return "Событие";
                }
            }
        }

        public string AuthorText => string.IsNullOrEmpty(Record.User) ? "(неизвестно)" : Record.User;

        public string SourceText => Record.Source == ChangeSource.Live ? SourceLabel(Record.Source) : SourceLabel(Record.Source) + RecordedBySuffix;

        /// <summary>«В сеансе», «Синхронизация», «Обновление», «С прошлого сеанса».</summary>
        public static string SourceLabel(ChangeSource source)
        {
            switch (source)
            {
                case ChangeSource.Sync: return "Синхронизация";
                case ChangeSource.Reload: return "Обновление";
                case ChangeSource.Open: return "С прошлого сеанса";
                default: return "В сеансе";
            }
        }

        private string RecordedBySuffix =>
            string.IsNullOrEmpty(Record.RecordedBy) ? "" : " (обнаружил " + Record.RecordedBy + ")";

        public string GroupText => Record.Kind == ChangeKind.Event ? "Модель" : ElementDescriber.GroupDisplayName(Record.Group);

        public string IdText => IsGroupHeader || Record.ElementId <= 0 ? "" : Record.ElementId.ToString(CultureInfo.InvariantCulture);

        // ── объект: имя крупно, категория и ID второй строкой ──

        public string ObjectTitle
        {
            get
            {
                if (IsGroupHeader)
                {
                    var tx = string.IsNullOrEmpty(Record.Transaction) ? "Операция" : Record.Transaction;
                    return tx + " · " + OperationGrouping.Elements(GroupCount);
                }
                return string.IsNullOrEmpty(Record.Name) ? "(без имени)" : Record.Name;
            }
        }

        public string ObjectSubtitle
        {
            get
            {
                if (IsGroupHeader) return CategorySummary(3);
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Record.Category)) parts.Add(Record.Category);
                if (Record.ElementId > 0) parts.Add("ID " + Record.ElementId.ToString(CultureInfo.InvariantCulture));
                if (Record.IsSummary) parts.Add("сводная запись");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>«Стены 8, Двери 4, …» — для заголовка операции.</summary>
        public string CategorySummary(int top)
        {
            if (Members == null) return Record.Category ?? "";
            var groups = Members
                .GroupBy(m => string.IsNullOrEmpty(m.Category) ? "без категории" : m.Category)
                .Select(g => new { Name = g.Key, Count = g.Sum(m => m.SummaryCount ?? 1) })
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var text = string.Join(", ", groups.Take(top).Select(g => g.Name + " " + g.Count));
            return groups.Count > top ? text + ", …" : text;
        }

        public string Name => IsGroupHeader ? ObjectTitle : Record.Name;
        public string Category => IsGroupHeader ? CategorySummary(3) : Record.Category;
        public string TypeName => IsGroupHeader ? "" : Record.TypeName;
        public string Transaction => Record.Transaction;
        public string Level => IsGroupHeader ? "" : Record.Level;
        public string Workset => IsGroupHeader ? "" : Record.Workset;
        public string OwnerView => IsGroupHeader ? "" : Record.OwnerView;
        public string Creator => IsGroupHeader ? "" : Record.Creator;
        public string LastChangedBy => Record.LastChangedBy;
        public string Machine => Record.Machine;

        public string ParamsText
        {
            get
            {
                if (IsGroupHeader)
                {
                    var names = Members.Where(m => m.HasParams).SelectMany(m => m.Params.Select(p => p.Name))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                    if (names.Count == 0) return "";
                    var head = string.Join(", ", names.Take(4));
                    return names.Count > 4 ? head + $" …ещё {names.Count - 4}" : head;
                }
                if (HasParentLink) return ParentText;
                if (!Record.HasParams) return "";
                var parts = Record.Params.Take(4).Select(p => $"{p.Name}: {Short(p.Old)} → {Short(p.New)}");
                var text = string.Join("; ", parts);
                if (Record.Params.Count > 4) text += $"; …ещё {Record.Params.Count - 4}";
                return text;
            }
        }

        public IReadOnlyList<ParamChangeRow> ParamRows =>
            !IsGroupHeader && Record.HasParams
                ? Record.Params.Select(p => new ParamChangeRow(p)).ToList()
                : (IReadOnlyList<ParamChangeRow>)new List<ParamChangeRow>();

        /// <summary>Элементы, которые можно показать или выделить в модели (у операции — все её существующие элементы).</summary>
        public IReadOnlyList<long> ModelIds =>
            (IsGroupHeader ? Members.AsEnumerable() : new[] { Record })
            .Where(m => m.ElementId > 0 && m.Kind != ChangeKind.Deleted)
            .Select(m => m.ElementId).Distinct().ToList();

        public bool CanShowInModel => IsGroupHeader ? ModelIds.Count > 0 : Record.ElementId > 0 && Record.Kind != ChangeKind.Deleted;

        /// <summary>Запись об одном элементе (не операция и не событие модели).</summary>
        public bool IsElementRecord => !IsGroupHeader && Record.ElementId > 0 && Record.Kind != ChangeKind.Event;

        private static string Short(string s, int max = 40)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }

    /// <summary>Строка вкладки «История»: другая запись о том же элементе.</summary>
    public sealed class HistoryItem
    {
        public HistoryItem(ChangeRow row, bool isCurrent)
        {
            Row = row;
            IsCurrent = isCurrent;
        }

        public ChangeRow Row { get; }
        public bool IsCurrent { get; }
        public string TimeText => Row.TimeShort;
        public string TimeTip => Row.TimeText;
        public string KindText => Row.KindText;
        public string KindKey => Row.KindKey;
        public string AuthorText => Row.AuthorText;
        /// <summary>Что изменилось или откуда запись.</summary>
        public string Summary
        {
            get
            {
                var p = Row.ParamsText;
                if (!string.IsNullOrEmpty(p)) return p;
                return string.IsNullOrEmpty(Row.Record.Transaction) ? Row.SourceText : Row.Record.Transaction;
            }
        }
        public string Weight => IsCurrent ? "SemiBold" : "Normal";
    }

    /// <summary>Включённый фильтр под строкой фильтров: подпись и крестик, который его снимает.</summary>
    public sealed class FilterChip
    {
        public FilterChip(string text, string toolTip, Action remove)
        {
            Text = text;
            ToolTip = toolTip ?? "Снять этот фильтр";
            RemoveCommand = new RelayCommand(remove);
        }

        public string Text { get; }
        public string ToolTip { get; }
        public RelayCommand RemoveCommand { get; }
    }

    /// <summary>Поле в подробностях записи: «Автор — Иванов · PC-012».</summary>
    public sealed class DetailField
    {
        public DetailField(string label, string value, string toolTip = null)
        {
            Label = label;
            Value = value;
            ToolTip = string.IsNullOrEmpty(toolTip) ? null : toolTip;
        }

        public string Label { get; }
        public string Value { get; }
        public string ToolTip { get; }
    }

    /// <summary>Строка вкладки «Все параметры».</summary>
    public sealed class ParamValueRow
    {
        public ParamValueRow(string name, string value, bool changed)
        {
            Name = name;
            Value = value;
            Changed = changed;
        }

        public string Name { get; }
        public string Value { get; }
        /// <summary>Параметр изменился в выбранной записи.</summary>
        public bool Changed { get; }
        public string Weight => Changed ? "SemiBold" : "Normal";
    }

    public sealed class ParamChangeRow
    {
        public ParamChangeRow(ParamChange p)
        {
            Name = p.Name;
            Old = string.IsNullOrEmpty(p.Old) ? "—" : p.Old;
            New = string.IsNullOrEmpty(p.New) ? "—" : p.New;
        }

        public string Name { get; }
        public string Old { get; }
        public string New { get; }
    }
}
