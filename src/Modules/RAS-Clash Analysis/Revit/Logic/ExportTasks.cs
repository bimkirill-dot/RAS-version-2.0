using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Строка «Задач выгрузки» (DECISIONS.md модуля, «Дерево задач с наследованием (03.11.2026)»): задача верхнего уровня и
    /// подзадачи любой глубины. Любая настройка (модели, файлы проверок, папки, форматы, вид) задаётся на любом уровне; не
    /// задана — берётся от уровня выше, заданная ниже перекрывает верхнюю (Settings: null — от уровня выше). NWF собирает строка
    /// без подзадач с галочкой «Формировать NWF» — из своих моделей, под своим именем (ExportNodes.MakesNwf). Без Revit API.
    /// </summary>
    public sealed class ExportTask
    {
        public string Name { get; set; }
        public ExportTask Parent { get; private set; }
        public List<ExportTask> Children { get; } = new List<ExportTask>();

        /// <summary>Свои настройки строки; null в поле — от уровня выше.</summary>
        public NodeSettings Settings { get; set; } = new NodeSettings();

        /// <summary>Задача в общем хранилище (у верхней строки): ключ файла (null — ещё не записана) и время файла, когда прочитана или записана (ExportStore).</summary>
        public string StoreId { get; set; }
        public DateTime StoreStamp { get; set; }
        /// <summary>
        /// Порядковый номер задачи в списке — общий у всех (разработчик, 02.11.2026: «зачем по алфавиту — просто по порядковому
        /// номеру»): новая — в конец, перетаскивание меняет; хранится в файле задачи.
        /// </summary>
        public int StoreOrder { get; set; }

        /// <summary>Новая задача верхнего уровня: все настройки свои, по умолчанию.</summary>
        public static ExportTask NewTop(string name) => new ExportTask { Name = name, Settings = NodeSettings.Top() };

        public ExportTask Add(ExportTask child)
        {
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public void Remove(ExportTask child)
        {
            if (Children.Remove(child)) child.Parent = null;
        }

        /// <summary>Сосед по уровню: сдвинуть себя на место target (перетаскивание строки мышью внутри своего уровня).</summary>
        public static bool MoveTo(List<ExportTask> siblings, ExportTask item, ExportTask target)
        {
            if (item == target || !siblings.Contains(item) || !siblings.Contains(target)) return false;
            var to = siblings.IndexOf(target);
            siblings.Remove(item);
            siblings.Insert(to, item);
            return true;
        }

        /// <summary>Вложенных строк на всех уровнях.</summary>
        public int Descendants => Children.Sum(c => 1 + c.Descendants);

        /// <summary>Строки ветки сверху вниз: сама и все вложенные.</summary>
        public IEnumerable<ExportTask> Self() => new[] { this }.Concat(Children.SelectMany(c => c.Self()));

        /// <summary>Копия с вложенными (⧉): настройки — копией, без места в хранилище.</summary>
        public ExportTask Copy()
        {
            var copy = new ExportTask { Name = Name, Settings = Settings.Clone() };
            foreach (var c in Children) copy.Add(c.Copy());
            return copy;
        }

        /// <summary>
        /// Выбранные строки для пакетного копирования и удаления — без тех, у которых выбрана строка выше (копия и удаление строки и так
        /// с её подзадачами); по порядку дерева (как в списке).
        /// </summary>
        public static List<ExportTask> Outermost(IList<ExportTask> roots, IEnumerable<ExportTask> picked)
        {
            var set = new HashSet<ExportTask>(picked);
            return roots.SelectMany(r => r.Self())
                .Where(t => set.Contains(t) && !Ancestors(t).Any(set.Contains))
                .ToList();
        }

        private static IEnumerable<ExportTask> Ancestors(ExportTask t)
        {
            for (var p = t.Parent; p != null; p = p.Parent) yield return p;
        }

        /// <summary>
        /// Пакетная копия (разработчик, 07.11.2026: «пакетное удаление, копирование задач / подзадач»): каждая выбранная строка — с
        /// подзадачами, название — свободное рядом («(2)»); подзадача — сразу под своей строкой, задача — в конец списка (номер — next).
        /// Возвращает копии.
        /// </summary>
        public static List<ExportTask> CopyRows(IList<ExportTask> roots, IEnumerable<ExportTask> picked, Func<int> next)
        {
            var copies = new List<ExportTask>();
            foreach (var t in Outermost(roots, picked))
            {
                var copy = t.Copy();
                if (t.Parent == null)
                {
                    copy.Name = ExportFiles.UniqueName(t.Name, roots.Select(r => r.Name));
                    copy.StoreOrder = next();
                    roots.Add(copy);
                }
                else
                {
                    var parent = t.Parent;
                    copy.Name = ExportFiles.UniqueName(t.Name, parent.Children.Select(c => c.Name));
                    parent.Add(copy);
                    parent.Children.Remove(copy);
                    parent.Children.Insert(parent.Children.IndexOf(t) + 1, copy);
                }
                copies.Add(copy);
            }
            return copies;
        }

        /// <summary>«строка», «строки», «строк» — по числу.</summary>
        public static string RowsWord(int n) => Word(n, "строка", "строки", "строк");

        /// <summary>«подзадача», «подзадачи», «подзадач» — по числу.</summary>
        public static string SubtasksWord(int n) => Word(n, "подзадача", "подзадачи", "подзадач");

        /// <summary>«блок», «блока», «блоков» — по числу.</summary>
        public static string BlocksWord(int n) => Word(n, "блок", "блока", "блоков");

        /// <summary>«модель», «модели», «моделей» — по числу.</summary>
        public static string ModelsWord(int n) => Word(n, "модель", "модели", "моделей");

        private static string Word(int n, string one, string few, string many)
        {
            var d = n % 10;
            var dd = n % 100;
            if (d == 1 && dd != 11) return one;
            if (d >= 2 && d <= 4 && (dd < 12 || dd > 14)) return few;
            return many;
        }
    }
}
