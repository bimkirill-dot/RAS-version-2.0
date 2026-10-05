using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Итог элемента за период: каким он был в начале периода и каким стал к концу.</summary>
    public enum NetChange
    {
        /// <summary>Создан за период (сколько бы его потом ни правили).</summary>
        Created,
        /// <summary>Существовал раньше, параметры отличаются от начала периода (или «было» неизвестно).</summary>
        Modified,
        /// <summary>
        /// Revit отмечал «изменён», но отличий в параметрах к концу периода нет: изменили и вернули, Revit только
        /// пересчитал (соединения, помещения), элемент лишь заняли для правки, или старая запись без сведений «было».
        /// </summary>
        NoNetChange,
        /// <summary>Существовал раньше и удалён.</summary>
        Deleted
    }

    public sealed class ElementNet
    {
        /// <summary>ID по последней записи (после синхронизации Revit может перенумеровать новый элемент).</summary>
        public long Id;
        /// <summary>Постоянный UniqueId (null — в записях его нет): по нему элемент ищется в модели в первую очередь.</summary>
        public string UniqueId;
        public NetChange Change;
        /// <summary>Изменён, но «было» неизвестно (снимок не был готов) — отличие не проверить, считаем изменённым.</summary>
        public bool Uncertain;
        /// <summary>Создатель (Created) или автор последней правки за период.</summary>
        public string Author;
        public DateTimeOffset Last;
    }

    public sealed class PeriodDiffResult
    {
        /// <summary>Ключ — UniqueId элемента (или «#ID», если в записях его нет).</summary>
        public Dictionary<string, ElementNet> Elements { get; } = new Dictionary<string, ElementNet>(StringComparer.Ordinal);
        /// <summary>Элементы из сводных строк массовых операций: по отдельности их нет в журнале.</summary>
        public int SummaryElements { get; set; }

        public int Count(NetChange change) => Elements.Values.Count(e => e.Change == change);
    }

    /// <summary>
    /// Итог за период по журналу — без хранения состояний модели. Первое «было» параметра за период — это его
    /// значение в начале периода, последнее «стало» — в конце; одинаковые — параметр по итогу не менялся.
    /// Положение и поворот записываются как параметры, поэтому «подвинули и вернули» тоже не изменение.
    /// Чистая логика: без Revit API и WPF.
    /// </summary>
    public static class PeriodDiff
    {
        /// <summary>
        /// «Редактирует» — кто занял элемент; меняется, когда элемент просто взяли для правки. Новые снимки его
        /// не пишут, в старых записях журнала он есть — отличием не считается.
        /// </summary>
        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Редактирует", "Edited by" };

        /// <param name="records">Записи журнала модели в любом порядке; учитываются попавшие в [from, to).</param>
        /// <param name="to">null — до сейчас.</param>
        /// <param name="authors">Чьи правки учитывать (без учёта регистра); null — всех.</param>
        /// <param name="skip">Запись не учитывать: убрана при актуализации, изменение не попало в хранилище.</param>
        public static PeriodDiffResult Compute(IEnumerable<ChangeRecord> records, DateTimeOffset? from, DateTimeOffset? to,
            ICollection<string> authors, Func<ChangeRecord, bool> skip = null)
        {
            var result = new PeriodDiffResult();
            var who = authors == null ? null : new HashSet<string>(authors, StringComparer.OrdinalIgnoreCase);
            // по UniqueId: ID нового элемента Revit может сменить при синхронизации, а UniqueId постоянен
            var byElement = new Dictionary<string, List<ChangeRecord>>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (r == null || r.Kind == ChangeKind.Event || r.IsNoise == true) continue;
                if (from.HasValue && r.Time < from.Value) continue;
                if (to.HasValue && r.Time >= to.Value) continue;
                if (who != null && !who.Contains(r.User ?? "")) continue;
                if (skip != null && skip(r)) continue;
                if (r.IsSummary || r.ElementId <= 0)
                {
                    result.SummaryElements += r.SummaryCount ?? 1;
                    continue;
                }
                var key = string.IsNullOrEmpty(r.UniqueId) ? "#" + r.ElementId : r.UniqueId;
                if (!byElement.TryGetValue(key, out var list)) byElement[key] = list = new List<ChangeRecord>();
                list.Add(r);
            }
            foreach (var kv in byElement)
            {
                var net = Fold(kv.Value.OrderBy(r => r.Time).ToList());
                if (net != null) result.Elements[kv.Key] = net;
            }
            return result;
        }

        /// <summary>Записи одного элемента по времени → итог (null — показывать нечего: создан и удалён за период).</summary>
        private static ElementNet Fold(List<ChangeRecord> ordered)
        {
            // первая запись решает, был ли элемент в начале периода: «создан» — не было, иначе — был.
            // «Создан» отменой (Ctrl+Z удаления) — элемент вернули, он существовал и раньше
            var first = ordered[0];
            var existedAtStart = first.Kind != ChangeKind.Created || first.Operation == OperationKind.Undo;
            var alive = true;
            string creator = null;
            string lastAuthor = null;
            var uncertain = false;
            var startValue = new Dictionary<string, string>(StringComparer.Ordinal);
            var endValue = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in ordered)
            {
                switch (r.Kind)
                {
                    case ChangeKind.Created:
                        alive = true; // в том числе отмена удаления
                        if (!existedAtStart && creator == null) creator = r.User;
                        break;
                    case ChangeKind.Deleted:
                        alive = false;
                        break;
                    case ChangeKind.Modified:
                        alive = true;
                        lastAuthor = r.User;
                        if (r.HasParams)
                        {
                            foreach (var p in r.Params)
                            {
                                if (p?.Name == null || Ignored.Contains(p.Name)) continue;
                                if (!startValue.ContainsKey(p.Name)) startValue[p.Name] = p.Old;
                                endValue[p.Name] = p.New;
                            }
                        }
                        // «было» не знали — отличие не проверить. Старые записи (без отметки) так не считаются:
                        // в них «без параметров» почти всегда значит «Revit только пересчитал»
                        else if (r.BeforeKnown == false)
                        {
                            uncertain = true;
                        }
                        break;
                }
            }

            var last = ordered[ordered.Count - 1];
            var net = new ElementNet
            {
                Id = last.ElementId,
                UniqueId = ordered.Select(r => r.UniqueId).LastOrDefault(u => !string.IsNullOrEmpty(u)),
                Last = last.Time
            };
            if (!alive)
            {
                if (!existedAtStart) return null; // создан и удалён за период — по итогу ничего не было
                net.Change = NetChange.Deleted;
                net.Author = last.User;
                return net;
            }
            if (!existedAtStart)
            {
                net.Change = NetChange.Created;
                net.Author = creator ?? last.User;
                return net;
            }
            var changed = startValue.Any(kv => endValue.TryGetValue(kv.Key, out var end) && !string.Equals(kv.Value, end, StringComparison.Ordinal));
            net.Author = lastAuthor ?? last.User;
            if (changed) net.Change = NetChange.Modified;
            else if (uncertain)
            {
                net.Change = NetChange.Modified;
                net.Uncertain = true;
            }
            else net.Change = NetChange.NoNetChange;
            return net;
        }
    }
}
