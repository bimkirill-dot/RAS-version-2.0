using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>По чему фильтровать: поле записи, изменённый параметр или значение параметра элемента.</summary>
    public enum FilterField
    {
        // запись
        Kind,
        Author,
        Machine,
        Transaction,
        Status,
        Source,
        // элемент (на момент записи)
        Category,
        Group,
        Name,
        TypeName,
        Id,
        Level,
        Workset,
        View,
        Creator,
        LastChangedBy,
        // изменения
        /// <summary>Имена параметров, изменённых в записи.</summary>
        ChangedParam,
        /// <summary>«Было» выбранного параметра в записи.</summary>
        Before,
        /// <summary>«Стало» выбранного параметра в записи.</summary>
        After,
        /// <summary>Значение параметра элемента: из записи или, если в ней его нет, текущее из модели.</summary>
        Param
    }

    /// <summary>Условия — как в правилах фильтров Revit.</summary>
    public enum FilterOperator
    {
        Equals,
        NotEquals,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
        Contains,
        NotContains,
        BeginsWith,
        NotBeginsWith,
        EndsWith,
        NotEndsWith,
        HasValue,
        HasNoValue
    }

    /// <summary>Одно условие: «Категория равно Стены», «Стало (Марка) содержит К-1».</summary>
    public sealed class FilterCondition
    {
        public FilterCondition(FilterField field, FilterOperator op, string value, string paramName = null)
        {
            Field = field;
            Operator = op;
            Value = RecordFilter.NeedsValue(op) ? (value ?? "").Trim() : "";
            ParamName = RecordFilter.NeedsParam(field) ? (paramName ?? "").Trim() : null;
        }

        public FilterField Field { get; }
        public FilterOperator Operator { get; }
        public string Value { get; }
        /// <summary>Для «Было», «Стало» и «Параметр элемента» — имя параметра.</summary>
        public string ParamName { get; }

        /// <summary>
        /// Условие заполнено: есть параметр (если нужен) и значение (если нужно). Незаполненное не действует — иначе
        /// добавленная и забытая строка «равно (пусто)» скрыла бы почти всё. Пустое значение — «не имеет значения».
        /// </summary>
        public bool IsComplete =>
            (!RecordFilter.NeedsParam(Field) || !string.IsNullOrEmpty(ParamName)) &&
            (!RecordFilter.NeedsValue(Operator) || Value.Length > 0);

        /// <summary>«Категория равно «Стены»» — для значка над таблицей.</summary>
        public string Text
        {
            get
            {
                var head = RecordFilter.FieldText(Field, ParamName) + " " + RecordFilter.OperatorLabel(Operator);
                return RecordFilter.NeedsValue(Operator) ? head + " «" + RecordFilter.Short(Value) + "»" : head;
            }
        }

        public bool SameAs(FilterCondition o) =>
            o != null && o.Field == Field && o.Operator == Operator &&
            string.Equals(o.Value, Value, StringComparison.CurrentCultureIgnoreCase) &&
            string.Equals(o.ParamName ?? "", ParamName ?? "", StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Фильтр списка — набор условий, соединённых «И» (все выполняются) или «ИЛИ» (хотя бы одно).
    /// Неизменяемый: каждое изменение — новый объект (фильтрация идёт в фоне). Без WPF и Revit — проверяется отдельно.
    /// </summary>
    public sealed class RecordFilter
    {
        public const string UnknownAuthor = "(неизвестно)";

        public static readonly RecordFilter Empty = new RecordFilter(new List<FilterCondition>(), false);

        public RecordFilter(IEnumerable<FilterCondition> conditions, bool any)
        {
            Conditions = (conditions ?? Enumerable.Empty<FilterCondition>()).Where(c => c != null && c.IsComplete).ToList();
            Any = any;
        }

        public IReadOnlyList<FilterCondition> Conditions { get; }
        /// <summary>true — «ИЛИ»: запись подходит, если выполняется хотя бы одно условие.</summary>
        public bool Any { get; }
        public bool IsEmpty => Conditions.Count == 0;

        /// <summary>Есть условия по параметрам элемента: значения изменённых элементов берутся из модели (поток Revit).</summary>
        public bool NeedsModel => Conditions.Any(c => c.Field == FilterField.Param);

        public RecordFilter With(FilterCondition c) => new RecordFilter(Conditions.Concat(new[] { c }), Any);
        public RecordFilter Without(FilterCondition c) => new RecordFilter(Conditions.Where(x => !ReferenceEquals(x, c)), Any);

        /// <param name="field">Значение поля записи или элемента (Kind … LastChangedBy).</param>
        /// <param name="param">Значение параметра элемента по имени (null — нет такого параметра или значение неизвестно).</param>
        public bool Keep(ChangeRecord r, Func<ChangeRecord, FilterField, string> field, Func<ChangeRecord, string, string> param)
        {
            if (Conditions.Count == 0) return true;
            foreach (var c in Conditions)
            {
                var ok = Matches(c, r, field, param);
                if (Any && ok) return true;
                if (!Any && !ok) return false;
            }
            return !Any;
        }

        public static bool Matches(FilterCondition c, ChangeRecord r, Func<ChangeRecord, FilterField, string> field,
            Func<ChangeRecord, string, string> param)
        {
            switch (c.Field)
            {
                case FilterField.ChangedParam:
                    return MatchesNames(c, r.Params);
                case FilterField.Before:
                case FilterField.After:
                    var p = r.Params?.FirstOrDefault(x => x != null && Same(x.Name, c.ParamName));
                    // параметр в этой записи не менялся — «было/стало» нет (как у элемента без параметра в Revit)
                    return Compare(p == null ? null : (c.Field == FilterField.Before ? p.Old : p.New) ?? "", c.Operator, c.Value);
                case FilterField.Param:
                    return Compare(param?.Invoke(r, c.ParamName), c.Operator, c.Value);
                default:
                    return Compare(field?.Invoke(r, c.Field) ?? "", c.Operator, c.Value);
            }
        }

        /// <summary>
        /// «Изменён параметр»: «равно / содержит / …» — хотя бы один изменённый параметр подходит,
        /// «не равно / не содержит / …» — ни один не подходит (в том числе если параметры не менялись).
        /// </summary>
        private static bool MatchesNames(FilterCondition c, IReadOnlyCollection<ParamChange> changes)
        {
            var names = changes?.Where(x => x != null && !string.IsNullOrEmpty(x.Name)).Select(x => x.Name).ToList() ?? new List<string>();
            switch (c.Operator)
            {
                case FilterOperator.HasValue: return names.Count > 0;
                case FilterOperator.HasNoValue: return names.Count == 0;
            }
            var negative = IsNegative(c.Operator);
            var positive = negative ? Positive(c.Operator) : c.Operator;
            var any = names.Any(n => Compare(n, positive, c.Value));
            return negative ? !any : any;
        }

        /// <summary>
        /// Сравнить значение с образцом. null — параметра нет: подходит только «не имеет значения».
        /// Числа («3833», «7325,31 см³», «-150 мм») сравниваются как числа, текст — без учёта регистра.
        /// </summary>
        public static bool Compare(string value, FilterOperator op, string target)
        {
            if (op == FilterOperator.HasNoValue) return string.IsNullOrWhiteSpace(value);
            if (value == null) return false;
            value = value.Trim();
            if (op == FilterOperator.HasValue) return value.Length > 0;
            target = (target ?? "").Trim();
            switch (op)
            {
                case FilterOperator.Equals: return AreEqual(value, target);
                case FilterOperator.NotEquals: return !AreEqual(value, target);
                case FilterOperator.Greater: return Order(value, target) > 0;
                case FilterOperator.GreaterOrEqual: return Order(value, target) >= 0;
                case FilterOperator.Less: return Order(value, target) < 0;
                case FilterOperator.LessOrEqual: return Order(value, target) <= 0;
                case FilterOperator.Contains: return Cmp.IndexOf(value, target, Opt) >= 0;
                case FilterOperator.NotContains: return Cmp.IndexOf(value, target, Opt) < 0;
                case FilterOperator.BeginsWith: return Cmp.IsPrefix(value, target, Opt);
                case FilterOperator.NotBeginsWith: return !Cmp.IsPrefix(value, target, Opt);
                case FilterOperator.EndsWith: return Cmp.IsSuffix(value, target, Opt);
                case FilterOperator.NotEndsWith: return !Cmp.IsSuffix(value, target, Opt);
            }
            return false;
        }

        private static readonly CompareInfo Cmp = CultureInfo.GetCultureInfo("ru-RU").CompareInfo;
        private const CompareOptions Opt = CompareOptions.IgnoreCase;

        private static bool AreEqual(string a, string b)
        {
            if (BothNumbers(a, b, out var x, out var y)) return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
            return Cmp.Compare(a, b, Opt) == 0;
        }

        private static int Order(string a, string b)
        {
            if (BothNumbers(a, b, out var x, out var y)) return x.CompareTo(y);
            return Cmp.Compare(a, b, Opt);
        }

        private static bool BothNumbers(string a, string b, out double x, out double y)
        {
            y = 0;
            return TryNumber(a, out x) && TryNumber(b, out y) && IsPlainNumber(a) && IsPlainNumber(b);
        }

        // число в начале значения: «3833», «7325,31 см³», «1 200 мм», «-150»
        private static readonly Regex NumberRx = new Regex(
            @"^\s*([-+−]?(?:\d{1,3}(?:[   ]\d{3})+|\d+)(?:[.,]\d+)?)", RegexOptions.Compiled);

        public static bool TryNumber(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            var m = NumberRx.Match(s);
            if (!m.Success) return false;
            var text = m.Groups[1].Value.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Replace('−', '-');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// «Равно 12» совпадает с «12 мм», но не с «12_А500» или «2А»: числом значение считается, только если после
        /// числа ничего нет или идут единицы измерения через пробел. Иначе — сравнение как текст.
        /// </summary>
        private static bool IsPlainNumber(string s)
        {
            var m = NumberRx.Match(s);
            if (!m.Success) return false;
            var rest = s.Substring(m.Length);
            return rest.Length == 0 || char.IsWhiteSpace(rest[0]);
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

        public static bool IsNegative(FilterOperator op) =>
            op == FilterOperator.NotEquals || op == FilterOperator.NotContains || op == FilterOperator.NotBeginsWith || op == FilterOperator.NotEndsWith;

        private static FilterOperator Positive(FilterOperator op)
        {
            switch (op)
            {
                case FilterOperator.NotEquals: return FilterOperator.Equals;
                case FilterOperator.NotContains: return FilterOperator.Contains;
                case FilterOperator.NotBeginsWith: return FilterOperator.BeginsWith;
                case FilterOperator.NotEndsWith: return FilterOperator.EndsWith;
                default: return op;
            }
        }

        public static bool NeedsValue(FilterOperator op) => op != FilterOperator.HasValue && op != FilterOperator.HasNoValue;

        public static bool NeedsParam(FilterField f) => f == FilterField.Before || f == FilterField.After || f == FilterField.Param;

        // ───────────────────────────── подписи ─────────────────────────────

        /// <summary>Поля в порядке списка окна, с заголовками групп.</summary>
        public static IReadOnlyList<KeyValuePair<string, FilterField[]>> FieldGroups { get; } = new[]
        {
            new KeyValuePair<string, FilterField[]>("Запись", new[]
            {
                FilterField.Kind, FilterField.Author, FilterField.Machine, FilterField.Transaction, FilterField.Status, FilterField.Source
            }),
            new KeyValuePair<string, FilterField[]>("Элемент", new[]
            {
                FilterField.Category, FilterField.Group, FilterField.Name, FilterField.TypeName, FilterField.Id, FilterField.Level,
                FilterField.Workset, FilterField.View, FilterField.Creator, FilterField.LastChangedBy
            }),
            new KeyValuePair<string, FilterField[]>("Изменения", new[] { FilterField.ChangedParam, FilterField.Before, FilterField.After }),
            new KeyValuePair<string, FilterField[]>("Параметры элемента", new[] { FilterField.Param })
        };

        public static string FieldLabel(FilterField f)
        {
            switch (f)
            {
                case FilterField.Kind: return "Действие";
                case FilterField.Author: return "Автор";
                case FilterField.Machine: return "Компьютер";
                case FilterField.Transaction: return "Команда Revit";
                case FilterField.Status: return "Статус";
                case FilterField.Source: return "Источник";
                case FilterField.Category: return "Категория";
                case FilterField.Group: return "Тип объекта";
                case FilterField.Name: return "Имя";
                case FilterField.TypeName: return "Тип / семейство";
                case FilterField.Id: return "ID";
                case FilterField.Level: return "Уровень";
                case FilterField.Workset: return "Рабочий набор";
                case FilterField.View: return "Вид";
                case FilterField.Creator: return "Создатель элемента";
                case FilterField.LastChangedBy: return "Последним синхронизировал";
                case FilterField.ChangedParam: return "Изменён параметр";
                case FilterField.Before: return "Было";
                case FilterField.After: return "Стало";
                case FilterField.Param: return "Параметр элемента";
                default: return f.ToString();
            }
        }

        /// <summary>«Категория», «Стало (Марка)», «Марка».</summary>
        public static string FieldText(FilterField f, string paramName)
        {
            switch (f)
            {
                case FilterField.Before:
                case FilterField.After: return FieldLabel(f) + " (" + paramName + ")";
                case FilterField.Param: return paramName;
                default: return FieldLabel(f);
            }
        }

        public static IReadOnlyList<FilterOperator> Operators { get; } = (FilterOperator[])Enum.GetValues(typeof(FilterOperator));

        public static string OperatorLabel(FilterOperator op)
        {
            switch (op)
            {
                case FilterOperator.Equals: return "равно";
                case FilterOperator.NotEquals: return "не равно";
                case FilterOperator.Greater: return "больше";
                case FilterOperator.GreaterOrEqual: return "больше или равно";
                case FilterOperator.Less: return "меньше";
                case FilterOperator.LessOrEqual: return "меньше или равно";
                case FilterOperator.Contains: return "содержит";
                case FilterOperator.NotContains: return "не содержит";
                case FilterOperator.BeginsWith: return "начинается с";
                case FilterOperator.NotBeginsWith: return "не начинается с";
                case FilterOperator.EndsWith: return "заканчивается на";
                case FilterOperator.NotEndsWith: return "не заканчивается на";
                case FilterOperator.HasValue: return "имеет значение";
                case FilterOperator.HasNoValue: return "не имеет значения";
                default: return op.ToString();
            }
        }

        public static string Short(string s, int max = 40) =>
            string.IsNullOrEmpty(s) ? "—" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
