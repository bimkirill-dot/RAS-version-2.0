using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.BlockMirrorRotation.Logic
{
    /// <summary>Ось модели для переименования: номер элемента, имя, средняя точка в плане, вертикальна ли (вдоль Y).</summary>
    public sealed class GridInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public bool Vertical { get; set; }
    }

    public enum GridNameKind { Number, Letter, Other }

    /// <summary>
    /// Имена осей после отражения (DECISIONS.md, 11.11.2026): вертикальные — цифрами слева направо, горизонтальные — буквами
    /// снизу вверх; набор имён прежний — те же цифры по возрастанию, те же буквы по алфавиту, только по новым местам.
    /// Дробные и прочие имена («1/1», «А'», «2а») — не трогаем ничего, причина — в Problems. Без Revit API — с тестами.
    /// </summary>
    public static class GridNaming
    {
        // порядок букв — по алфавиту (ГОСТ 21.101 выкидывает часть букв, но порядок оставшихся тот же); затем латиница
        private const string Alphabet = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public static GridNameKind KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return GridNameKind.Other;
            if (name.All(c => c >= '0' && c <= '9')) return GridNameKind.Number;
            if (name.All(c => Alphabet.IndexOf(char.ToUpperInvariant(c)) >= 0)) return GridNameKind.Letter;
            return GridNameKind.Other;
        }

        /// <summary>Порядок буквенных имён: короче — раньше («Я» перед «АА»), затем по алфавиту.</summary>
        public static int CompareLetters(string a, string b)
        {
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            for (var i = 0; i < a.Length; i++)
            {
                var d = Alphabet.IndexOf(char.ToUpperInvariant(a[i])).CompareTo(Alphabet.IndexOf(char.ToUpperInvariant(b[i])));
                if (d != 0) return d;
            }
            return 0;
        }

        public sealed class Plan
        {
            /// <summary>Номер оси → новое имя (только те, у кого имя меняется).</summary>
            public Dictionary<long, string> Renames { get; } = new Dictionary<long, string>();

            /// <summary>Почему оси не переименованы (пусто — всё хорошо).</summary>
            public List<string> Problems { get; } = new List<string>();
        }

        public static Plan Make(IEnumerable<GridInfo> grids)
        {
            var plan = new Plan();
            var list = grids.ToList();
            var odd = list.Where(g => KindOf(g.Name) == GridNameKind.Other).Select(g => "«" + g.Name + "»").ToList();
            if (odd.Count > 0)
            {
                plan.Problems.Add("оси не переименованы: имена не цифрами и не буквами — " + string.Join(", ", odd.Take(10)) +
                                  (odd.Count > 10 ? ", …" : ""));
                return plan;
            }
            var numbers = list.Where(g => KindOf(g.Name) == GridNameKind.Number).ToList();
            var letters = list.Where(g => KindOf(g.Name) == GridNameKind.Letter).ToList();
            var crossed = numbers.Where(g => !g.Vertical).Select(g => "«" + g.Name + "»")
                .Concat(letters.Where(g => g.Vertical).Select(g => "«" + g.Name + "»")).ToList();
            if (crossed.Count > 0)
            {
                plan.Problems.Add("оси не переименованы: цифровые должны быть вертикальными, буквенные — горизонтальными, а здесь — " +
                                  string.Join(", ", crossed.Take(10)) + (crossed.Count > 10 ? ", …" : ""));
                return plan;
            }
            var numberNames = numbers.Select(g => g.Name).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
            Assign(numbers.OrderBy(g => g.X).ThenBy(g => g.Y).ToList(), numberNames, plan);
            var letterNames = letters.Select(g => g.Name).ToList();
            letterNames.Sort(CompareLetters);
            Assign(letters.OrderBy(g => g.Y).ThenBy(g => g.X).ToList(), letterNames, plan);
            return plan;
        }

        private static void Assign(List<GridInfo> ordered, List<string> names, Plan plan)
        {
            for (var i = 0; i < ordered.Count; i++)
                if (!string.Equals(ordered[i].Name, names[i], StringComparison.Ordinal))
                    plan.Renames[ordered[i].Id] = names[i];
        }
    }
}
