using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Записи журнала по одному элементу — от новых к старым.</summary>
    public sealed class ElementCheck
    {
        public long ElementId { get; set; }
        public List<ChangeRecord> NewestFirst { get; set; } = new List<ChangeRecord>();
        /// <summary>Нужны текущие значения параметров (есть кандидат «Изменён»).</summary>
        public bool NeedsValues { get; set; }
    }

    /// <summary>Текущее состояние элемента в модели.</summary>
    public sealed class ElementState
    {
        public bool Exists { get; set; }
        /// <summary>Элемента нет, но проверить нельзя (например, в модели есть закрытые рабочие наборы).</summary>
        public bool Unverifiable { get; set; }
        /// <summary>UniqueId найденного элемента (под тем же ID может оказаться другой элемент).</summary>
        public string UniqueId { get; set; }
        /// <summary>Имя параметра → текущее значение (null — нет такого параметра).</summary>
        public Func<string, string> CurrentValue { get; set; }
    }

    /// <summary>Запись, которая не соответствует модели.</summary>
    public sealed class AuditFinding
    {
        public ChangeRecord Record { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Что известно о журнале при актуализации: статусы записей и какие старые записи можно проверять.</summary>
    public sealed class AuditContext
    {
        public Dictionary<ChangeRecord, SyncStatus> Statuses { get; set; }
        /// <summary>Записи старых версий плагина (без сведений о синхронизации), которые можно проверять.</summary>
        public HashSet<ChangeRecord> LegacyEligible { get; set; } = new HashSet<ChangeRecord>();
        /// <summary>Сколько старых записей пропущено, потому что проверить их нельзя.</summary>
        public int LegacySkipped { get; set; }
        /// <summary>До какого момента открытая модель содержит всё из хранилища (null — в этом сеансе не обновлялась).</summary>
        public DateTimeOffset? CentralUpToDate { get; set; }
        /// <summary>Последняя массовая операция (сводная запись без списка элементов).</summary>
        public DateTimeOffset? LastMassOperation { get; set; }

        public SyncStatus StatusOf(ChangeRecord r) =>
            Statuses != null && Statuses.TryGetValue(r, out var s) ? s : SyncStatus.Unknown;
    }

    /// <summary>
    /// Актуализация журнала: какие записи описывают изменения, которых в модели сейчас нет.
    ///
    /// Убираются только записи, которые НЕ подтверждены хранилищем: изменение не синхронизировано
    /// и модель закрыта без сохранения или локальная копия заменена. Синхронизированные записи и
    /// полученные из хранилища — это факты истории; если модель с тех пор изменилась, это не делает их неверными.
    /// Изменения коллег, которые ещё работают и не синхронизировались, тоже не трогаются.
    ///
    /// Записи со статусом «✗» (по событиям сеанса известно, что изменения пропали) убираются все, без сверки
    /// с моделью (VoidFindings): сверка не всегда может это доказать — правка без параметров, «изменил и вернул».
    ///
    /// Записи старых версий плагина (без сведений о синхронизации) проверяются осторожнее: только если
    /// после них автор уже синхронизировался или закрыл модель, а открытая модель с тех пор обновлена
    /// из хранилища. Иначе расхождение может означать лишь то, что открытая копия устарела.
    ///
    /// По каждому элементу записи проверяются от новой к старой: пока последняя запись противоречит
    /// модели, она убирается; на первой согласованной записи проверка останавливается.
    /// </summary>
    public static class JournalAudit
    {
        /// <param name="centralUpToDate">
        /// Момент, до которого открытая модель содержит всё из хранилища (последняя синхронизация или
        /// обновление в этом сеансе; для модели без совместной работы — сейчас). null — старые записи не проверяются.
        /// </param>
        public static AuditContext BuildContext(IReadOnlyCollection<ChangeRecord> records, Dictionary<ChangeRecord, SyncStatus> statuses,
            DateTimeOffset? centralUpToDate)
        {
            var ctx = new AuditContext { Statuses = statuses, CentralUpToDate = centralUpToDate };
            ctx.LastMassOperation = records.Where(r => r.IsSummary).Select(r => (DateTimeOffset?)r.Time).DefaultIfEmpty(null).Max();

            // события модели по каждому автору (компьютеру): синхронизация, закрытие и т. п.
            var events = records.Where(EndsWork)
                .GroupBy(AuthorKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Time).ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var r in records)
            {
                if (!IsBasicCandidate(r) || ctx.StatusOf(r) != SyncStatus.Unknown) continue;
                if (centralUpToDate == null || r.Time >= centralUpToDate.Value)
                {
                    ctx.LegacySkipped++;
                    continue;
                }
                // после изменения автор синхронизировался или закрыл модель — и это было до обновления открытой модели
                var ok = events.TryGetValue(AuthorKey(r), out var times) &&
                         times.Any(t => t > r.Time && t <= centralUpToDate.Value);
                if (ok) ctx.LegacyEligible.Add(r);
                else ctx.LegacySkipped++;
            }
            return ctx;
        }

        private static string AuthorKey(ChangeRecord r) => (r.User ?? "") + "@" + (r.Machine ?? "");

        /// <summary>
        /// Событие, после которого несинхронизированных изменений автора в работе уже нет:
        /// синхронизация, обновление, закрытие, повторное открытие. Сохранение локальной копии работу не завершает.
        /// </summary>
        private static bool EndsWork(ChangeRecord r)
        {
            if (r.Kind != ChangeKind.Event) return false;
            switch (r.EventType)
            {
                case ModelEventType.SaveLocal:
                case ModelEventType.Exclude:
                case ModelEventType.Restore: // отметки актуализации — правка журнала, а не конец работы автора
                    return false;
                case ModelEventType.None: // события старых версий плагина — без типа, только название
                    return r.Name == null || !r.Name.StartsWith("Сохранение", StringComparison.OrdinalIgnoreCase);
                default:
                    return true;
            }
        }

        private static bool IsBasicCandidate(ChangeRecord r) =>
            r.Source == ChangeSource.Live && r.ElementId > 0 && !r.IsSummary && !string.IsNullOrEmpty(r.RecordId) &&
            (r.Kind == ChangeKind.Created || r.Kind == ChangeKind.Modified || r.Kind == ChangeKind.Deleted);

        /// <summary>Старая запись, которую нужно сверить с моделью (у записей «✗» сверять нечего — см. VoidFindings).</summary>
        public static bool IsCandidate(ChangeRecord r, AuditContext ctx) =>
            IsBasicCandidate(r) && ctx.StatusOf(r) == SyncStatus.Unknown && ctx.LegacyEligible.Contains(r);

        /// <summary>
        /// Записи «✗ не попало в хранилище»: по событиям сеанса уже известно, что изменения пропали (модель закрыта
        /// без синхронизации и без сохранения, локальная копия заменена), — убираются все, без сверки с моделью.
        /// </summary>
        public static List<AuditFinding> VoidFindings(IEnumerable<ChangeRecord> records, AuditContext ctx, HashSet<string> excluded,
            bool workshared)
        {
            var result = new List<AuditFinding>();
            foreach (var r in records)
            {
                if (!IsBasicCandidate(r) || (excluded != null && excluded.Contains(r.RecordId))) continue;
                var status = ctx.StatusOf(r);
                if (status == SyncStatus.Discarded)
                    result.Add(new AuditFinding
                    {
                        Record = r,
                        Reason = workshared ? "не попало в хранилище: модель закрыта без синхронизации и без сохранения" : "модель закрыта без сохранения"
                    });
                else if (status == SyncStatus.LostLocal)
                    result.Add(new AuditFinding { Record = r, Reason = "не попало в хранилище: локальная копия заменена новой" });
            }
            return result;
        }

        private static bool IsVoid(SyncStatus s) => s == SyncStatus.Discarded || s == SyncStatus.LostLocal;

        /// <summary>Id записей, уже убранных из журнала при прошлых актуализациях.</summary>
        public static HashSet<string> ExcludedIds(IEnumerable<ChangeRecord> records)
        {
            // по времени: «убрано» добавляет, «возвращено в журнал» снимает (убрать можно снова)
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records.Where(x => x.Kind == ChangeKind.Event && x.Excludes != null &&
                                                 (x.EventType == ModelEventType.Exclude || x.EventType == ModelEventType.Restore))
                         .OrderBy(x => x.Time))
            {
                if (r.EventType == ModelEventType.Exclude) set.UnionWith(r.Excludes);
                else set.ExceptWith(r.Excludes);
            }
            return set;
        }

        /// <summary>Элементы, по которым есть что проверять.</summary>
        public static List<ElementCheck> BuildChecks(IEnumerable<ChangeRecord> records, AuditContext ctx, HashSet<string> excluded)
        {
            var result = new List<ElementCheck>();
            // записи «✗» убираются отдельно (VoidFindings) — для сверки их уже нет, как и убранных
            var byElement = records
                .Where(r => r.ElementId > 0 && r.Kind != ChangeKind.Event && !r.IsSummary && !IsVoid(ctx.StatusOf(r)) &&
                            (r.RecordId == null || excluded == null || !excluded.Contains(r.RecordId)))
                .GroupBy(r => r.ElementId);
            foreach (var g in byElement)
            {
                var list = g.OrderByDescending(r => r.Time).ToList();
                if (!IsCandidate(list[0], ctx)) continue; // последняя запись подтверждена — проверять нечего
                result.Add(new ElementCheck
                {
                    ElementId = g.Key,
                    NewestFirst = list,
                    NeedsValues = list.TakeWhile(r => IsCandidate(r, ctx)).Any(r => r.Kind == ChangeKind.Modified && r.HasParams)
                });
            }
            return result;
        }

        /// <summary>Сравнить записи элемента с его текущим состоянием (одно состояние на все записи).</summary>
        public static List<AuditFinding> Evaluate(ElementCheck check, ElementState state, AuditContext ctx) =>
            Evaluate(check, _ => state, ctx);

        /// <summary>Сравнить записи элемента с текущим состоянием (оно определяется по каждой записи: UniqueId у записей может различаться).</summary>
        public static List<AuditFinding> Evaluate(ElementCheck check, Func<ChangeRecord, ElementState> stateOf, AuditContext ctx)
        {
            var findings = new List<AuditFinding>();
            foreach (var r in check.NewestFirst)
            {
                if (!IsCandidate(r, ctx)) break;
                var state = stateOf(r);
                if (state == null) break;
                var reason = Contradiction(r, state);
                if (reason == null) break;
                if (ctx.StatusOf(r) == SyncStatus.Unknown)
                {
                    // отсутствие элемента ничего не доказывает, если после записи была массовая операция без списка элементов
                    if (!state.Exists && ctx.LastMassOperation.HasValue && ctx.LastMassOperation.Value > r.Time) break;
                    reason += " (старая запись без сведений о синхронизации)";
                }
                findings.Add(new AuditFinding { Record = r, Reason = reason });
            }
            return findings;
        }

        /// <summary>Чем запись противоречит модели (null — не противоречит или проверить нельзя).</summary>
        public static string Contradiction(ChangeRecord r, ElementState state)
        {
            // под тем же ID может быть уже другой элемент — сравниваем и UniqueId, если он известен
            var exists = state.Exists &&
                         (string.IsNullOrEmpty(r.UniqueId) || string.IsNullOrEmpty(state.UniqueId) ||
                          string.Equals(r.UniqueId, state.UniqueId, StringComparison.OrdinalIgnoreCase));
            var missing = !exists && !(state.Unverifiable && !state.Exists);
            switch (r.Kind)
            {
                case ChangeKind.Deleted:
                    return exists ? "удаление не попало в модель: элемент существует" : null;

                case ChangeKind.Created:
                    if (!missing) return null;
                    return state.Exists
                        ? "создание не попало в модель: под этим ID сейчас другой элемент"
                        : "создание не попало в модель: элемента нет";

                case ChangeKind.Modified:
                    if (missing) return "изменение не попало в модель: элемента нет";
                    if (!exists || !r.HasParams || state.CurrentValue == null) return null;
                    int compared = 0, matchesNew = 0, matchesOld = 0;
                    foreach (var p in r.Params)
                    {
                        string cur;
                        try { cur = state.CurrentValue(p.Name); }
                        catch { cur = null; }
                        if (cur == null) continue;
                        compared++;
                        if (Same(cur, p.New)) matchesNew++;
                        else if (Same(cur, p.Old)) matchesOld++;
                    }
                    // в модели осталось «было» и ни одно «стало» не совпало — изменения в модели нет
                    if (compared > 0 && matchesNew == 0 && matchesOld > 0)
                        return "изменение не попало в модель: значения параметров остались прежними";
                    return null;
            }
            return null;
        }

        private static bool Same(string a, string b) =>
            string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }
}
