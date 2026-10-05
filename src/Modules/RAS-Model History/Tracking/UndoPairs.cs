using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Пары «правка + её отмена (Ctrl+Z)»: по итогу ничего не изменилось, флажок «Скрыть отменённые» прячет обе записи.
    /// Отмену видит только сам автор, поэтому пары ищутся среди «живых» записей одного сеанса (одно открытие модели):
    /// после синхронизации Revit очищает историю отмен, и номер элемента внутри сеанса не меняется.
    /// Чистая логика: без Revit API и WPF.
    /// </summary>
    public static class UndoPairs
    {
        public static HashSet<ChangeRecord> Find(IEnumerable<ChangeRecord> records)
        {
            var hidden = new HashSet<ChangeRecord>();
            var groups = records
                .Where(r => r != null && r.Source == ChangeSource.Live && r.Kind != ChangeKind.Event && !r.IsSummary && r.ElementId > 0)
                .GroupBy(r => (r.User ?? "") + "|" + (r.Machine ?? "") + "|" + (r.Session ?? "") + "|" + r.ElementId,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                // неотменённые записи элемента, от старой к новой (OrderBy сохраняет порядок журнала при равном времени)
                var done = new List<ChangeRecord>();
                foreach (var r in g.OrderBy(x => x.Time))
                {
                    switch (r.Operation)
                    {
                        case OperationKind.Undo:
                            // отменяется последняя правка этой же команды; не нашлась (правка до установки плагина,
                            // запись сверх лимита) — отмена остаётся видна
                            for (int i = done.Count - 1; i >= 0; i--)
                            {
                                var d = done[i];
                                if (!string.Equals(d.Transaction ?? "", r.Transaction ?? "", StringComparison.Ordinal) || !Reverses(d.Kind, r.Kind))
                                    continue;
                                hidden.Add(d);
                                hidden.Add(r);
                                done.RemoveAt(i);
                                break;
                            }
                            break;
                        case OperationKind.Rollback:
                            break; // откат группы называется именем группы, а не отменённых команд — пару не найти
                        default:
                            done.Add(r); // правка или повтор (Ctrl+Y): повтор тоже можно отменить
                            break;
                    }
                }
            }
            return hidden;
        }

        /// <summary>Отмена создания пишется как удаление, отмена удаления — как создание, отмена правки — как правка.</summary>
        private static bool Reverses(ChangeKind done, ChangeKind undo) =>
            (done == ChangeKind.Created && undo == ChangeKind.Deleted) ||
            (done == ChangeKind.Deleted && undo == ChangeKind.Created) ||
            (done == ChangeKind.Modified && undo == ChangeKind.Modified);
    }
}
