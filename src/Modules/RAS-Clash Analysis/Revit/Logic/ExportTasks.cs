using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Строка «Задач выгрузки» (DECISIONS.md модуля, «Ручной порядок и модель разработчика (03.11.2026)»): верхняя — **проект**
    /// (папка, папка для NWC, модели — Project, ModelFiles), под ним — **задачи** (например, «Высокая критичность»: файлы
    /// проверок, имя NWF, папки, форматы — Options). Блоки задачи — не строки, плагин собирает их из моделей проекта по именам
    /// (ExportNodes.Blocks). Вторая критичность — копия задачи. Без Revit API.
    /// </summary>
    public sealed class ExportTask
    {
        public string Name { get; set; }
        public ExportTask Parent { get; private set; }
        public List<ExportTask> Children { get; } = new List<ExportTask>();

        /// <summary>Модели проекта — один раз на проект: блок и отдел — по имени, обе критичности берут одни модели.</summary>
        public List<ExportModel> ModelFiles { get; set; } = new List<ExportModel>();
        /// <summary>Моделей у самой строки.</summary>
        public int Models => ModelFiles.Count;
        /// <summary>Настройки проекта — у верхней строки.</summary>
        public ProjectSettings Project { get; } = new ProjectSettings();
        /// <summary>Настройки задачи — у строки под проектом.</summary>
        public TaskSettings Options { get; set; } = new TaskSettings();
        /// <summary>Проект в общем хранилище: ключ файла (null — ещё не записан) и время файла, когда прочитан или записан (ExportStore).</summary>
        public string StoreId { get; set; }
        public DateTime StoreStamp { get; set; }
        /// <summary>
        /// Порядковый номер проекта в списке — общий у всех (разработчик, 02.11.2026: «зачем по алфавиту — просто по порядковому
        /// номеру»): новый — в конец, перетаскивание меняет; хранится в файле проекта.
        /// </summary>
        public int StoreOrder { get; set; }

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

        /// <summary>Сосед по уровню: сдвинуть себя на место target (перетаскивание строки мышью — проекты между собой, задачи внутри проекта).</summary>
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

        /// <summary>«задача», «задачи», «задач» — по числу.</summary>
        public static string TasksWord(int n) => Word(n, "задача", "задачи", "задач");

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
