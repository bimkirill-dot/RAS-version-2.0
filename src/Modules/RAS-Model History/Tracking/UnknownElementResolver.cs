using System;
using System.Collections.Generic;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// «Нет сведений: элемент удалён до того, как плагин его запомнил» — но об этом элементе в журнале бывают другие
    /// записи (его создавали или меняли — у любого пользователя). Тогда имя, категория, тип и прочее берутся из самой
    /// свежей такой записи; запись помечается InfoFromJournal. Записи приходят частями (журнал читается понемногу),
    /// поэтому неразрешённые ждут следующих частей. Файлы журнала не меняются — только показ. Без Revit: с тестами.
    /// </summary>
    public sealed class UnknownElementResolver
    {
        /// <summary>Номер элемента → самая свежая запись с настоящими сведениями о нём.</summary>
        private readonly Dictionary<long, ChangeRecord> _known = new Dictionary<long, ChangeRecord>();
        private readonly List<ChangeRecord> _waiting = new List<ChangeRecord>();

        public void Add(IEnumerable<ChangeRecord> records)
        {
            var fresh = new List<ChangeRecord>();
            foreach (var r in records)
            {
                if (r == null || r.ElementId <= 0 || r.Kind == ChangeKind.Event || r.IsSummary) continue;
                if (IsUnknown(r)) fresh.Add(r);
                else if (!_known.TryGetValue(r.ElementId, out var k) || k.Time <= r.Time) _known[r.ElementId] = r;
            }
            _waiting.AddRange(fresh);
            _waiting.RemoveAll(Resolve);
        }

        public static bool IsUnknown(ChangeRecord r) => string.Equals(r.Name, ChangeRecord.UnknownElementName, StringComparison.Ordinal);

        private bool Resolve(ChangeRecord r)
        {
            if (!_known.TryGetValue(r.ElementId, out var k)) return false;
            r.Name = k.Name;
            r.Category = k.Category;
            r.ClassName = k.ClassName;
            r.Group = k.Group;
            r.TypeName = k.TypeName;
            r.Level = k.Level;
            r.Workset = k.Workset;
            r.OwnerView = k.OwnerView;
            if (string.IsNullOrEmpty(r.UniqueId)) r.UniqueId = k.UniqueId;
            if (string.IsNullOrEmpty(r.Creator)) r.Creator = k.Creator;
            r.InfoFromJournal = true;
            return true;
        }
    }
}
