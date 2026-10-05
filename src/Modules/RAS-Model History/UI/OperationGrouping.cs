using System.Collections.Generic;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Строка списка до превращения в ChangeRow: обычная запись, заголовок операции или запись внутри операции.</summary>
    public sealed class RowEntry
    {
        public ChangeRecord Record;
        /// <summary>Все записи операции (у заголовка), иначе null.</summary>
        public List<ChangeRecord> Members;
        public bool IsHeader;
        public bool IsChild;
        public bool Expanded;
        public string Key;
    }

    /// <summary>
    /// Группировка «по операциям»: записи одной транзакции Revit (поле пакета «g») показываются одной строкой,
    /// которую можно раскрыть. Одиночные записи и события модели не группируются. Без WPF — проверяется отдельно.
    /// </summary>
    public static class OperationGrouping
    {
        /// <summary>
        /// Ключ операции: пакет транзакции + автор. Автор нужен для записей «Синхронизация»: один разбор синхронизации —
        /// один пакет, но в нём изменения разных коллег, и каждый должен остаться своей строкой.
        /// </summary>
        public static string KeyOf(ChangeRecord r) =>
            r.Kind == ChangeKind.Event || string.IsNullOrEmpty(r.Batch) ? null : r.Batch + "|" + (r.User ?? "");

        /// <param name="ordered">Записи в порядке показа (обычно новые сверху).</param>
        /// <param name="expanded">Ключи раскрытых операций.</param>
        public static List<RowEntry> Build(IList<ChangeRecord> ordered, ICollection<string> expanded)
        {
            var groups = new Dictionary<string, List<ChangeRecord>>();
            foreach (var r in ordered)
            {
                var key = KeyOf(r);
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ChangeRecord>();
                list.Add(r);
            }

            var result = new List<RowEntry>(ordered.Count);
            var emitted = new HashSet<string>();
            foreach (var r in ordered)
            {
                var key = KeyOf(r);
                if (key == null || groups[key].Count < 2)
                {
                    result.Add(new RowEntry { Record = r });
                    continue;
                }
                if (!emitted.Add(key)) continue; // запись уже показана в своей операции
                var members = groups[key];
                var open = expanded != null && expanded.Contains(key);
                result.Add(new RowEntry { Record = members[0], Members = members, IsHeader = true, Expanded = open, Key = key });
                if (!open) continue;
                foreach (var m in members) result.Add(new RowEntry { Record = m, IsChild = true, Key = key });
            }
            return result;
        }

        /// <summary>«1 элемент», «3 элемента», «12 элементов».</summary>
        public static string Elements(int n) => Plural(n, "элемент", "элемента", "элементов");

        /// <summary>«1 день», «3 дня», «14 дней».</summary>
        public static string Days(int n) => Plural(n, "день", "дня", "дней");

        private static string Plural(int n, string one, string few, string many)
        {
            var n10 = n % 10;
            var n100 = n % 100;
            var word = n10 == 1 && n100 != 11 ? one
                : n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? few
                : many;
            return n + " " + word;
        }
    }
}
