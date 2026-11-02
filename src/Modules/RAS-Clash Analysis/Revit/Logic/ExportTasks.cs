using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Строка «Задач выгрузки» (DECISIONS.md модуля): верхняя — **проект**, каталог с общими настройками (Project,
    /// ExportNode.cs), своих моделей нет, «Запустить» — все его задачи; под ним — **задачи** (обычно блок): модели, файл проверок,
    /// свой NWF (разработчик, 02.11.2026). Без Revit API.
    /// </summary>
    public sealed class ExportTask
    {
        public string Name { get; set; }
        public ExportTask Parent { get; private set; }
        public List<ExportTask> Children { get; } = new List<ExportTask>();

        /// <summary>Модели задачи; у проекта своих нет.</summary>
        public List<ExportModel> ModelFiles { get; set; } = new List<ExportModel>();
        /// <summary>Моделей у самой строки.</summary>
        public int Models => ModelFiles.Count;
        /// <summary>Настройки проекта — у верхней строки.</summary>
        public ProjectSettings Project { get; } = new ProjectSettings();
        /// <summary>Файл проверок задачи; null — как у проекта (два NWF на блок — две задачи со своими файлами).</summary>
        public string TestsFile { get; set; }
        /// <summary>Последняя выгрузка узла; null — не выгружался.</summary>
        public DateTime? LastRun { get; set; }
        /// <summary>Моделей, не вышедших в последней выгрузке.</summary>
        public int Failed { get; set; }
        /// <summary>Узел сейчас выгружается у этого человека; null — не идёт.</summary>
        public string RunningBy { get; set; }
        /// <summary>Сколько моделей уже выгружено в идущей выгрузке.</summary>
        public int RunningDone { get; set; }

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

        /// <summary>Сосед по уровню: сдвинуть себя на место target (перетаскивание строки мышью — только внутри своего уровня).</summary>
        public static bool MoveTo(List<ExportTask> siblings, ExportTask item, ExportTask target)
        {
            if (item == target || !siblings.Contains(item) || !siblings.Contains(target)) return false;
            var to = siblings.IndexOf(target);
            siblings.Remove(item);
            siblings.Insert(to, item);
            return true;
        }

        /// <summary>Вложенных узлов на всех уровнях.</summary>
        public int Descendants => Children.Sum(c => 1 + c.Descendants);

        /// <summary>
        /// Моделей в выгрузке узла: свои — если заданы; нет своих — сумма вложенных (проект — сумма блоков); нет и вложенных —
        /// модели ближайшего узла выше, где они заданы (критичность выгружает модели своего блока).
        /// </summary>
        public int AllModels
        {
            get
            {
                if (Models > 0) return Models;
                if (Children.Count > 0) return Children.Sum(c => c.AllModels);
                for (var p = Parent; p != null; p = p.Parent)
                    if (p.Models > 0) return p.Models;
                return 0;
            }
        }

        /// <summary>
        /// Краткая информация строки: идёт у коллеги — «идёт у …, N из M»; иначе «N моделей · выгружено дата» и сколько не
        /// вышло.
        /// </summary>
        public string Summary()
        {
            var models = AllModels;
            if (RunningBy != null) return "идёт у " + RunningBy + " — " + RunningDone + " из " + models;
            var parts = new List<string>();
            if (Parent == null) parts.Add(Children.Count + " " + TasksWord(Children.Count));
            parts.Add(models + " " + ModelsWord(models));
            if (LastRun == null) parts.Add("не выгружалось");
            else if (Failed > 0) parts.Add(Failed + " не " + (Failed == 1 ? "вышла" : "вышли") + " " + LastRun.Value.ToString("dd.MM HH:mm"));
            else parts.Add("выгружено " + LastRun.Value.ToString("dd.MM HH:mm"));
            return string.Join(" · ", parts);
        }

        /// <summary>«задача», «задачи», «задач» — по числу.</summary>
        public static string TasksWord(int n)
        {
            var w = ModelsWord(n);
            return w == "модель" ? "задача" : w == "модели" ? "задачи" : "задач";
        }

        /// <summary>«модель», «модели», «моделей» — по числу.</summary>
        public static string ModelsWord(int n)
        {
            var d = n % 10;
            var dd = n % 100;
            if (d == 1 && dd != 11) return "модель";
            if (d >= 2 && d <= 4 && (dd < 12 || dd > 14)) return "модели";
            return "моделей";
        }

        /// <summary>
        /// Строки окна по порядку: номер («1», «1.1», «1.1.2»), уровень и узел; свёрнутые узлы (collapsed) — без вложенных.
        /// Номера ставятся сами и пересчитываются после переноса.
        /// </summary>
        public static List<(string Number, int Level, ExportTask Task)> Rows(IList<ExportTask> roots, ISet<ExportTask> collapsed)
        {
            var rows = new List<(string, int, ExportTask)>();
            Walk(roots, "", 0, collapsed, rows);
            return rows;
        }

        private static void Walk(IList<ExportTask> items, string prefix, int level, ISet<ExportTask> collapsed,
            List<(string, int, ExportTask)> rows)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var number = prefix + (i + 1);
                rows.Add((number, level, items[i]));
                if (!collapsed.Contains(items[i])) Walk(items[i].Children, number + ".", level + 1, collapsed, rows);
            }
        }
    }
}
