using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: дерево «Задачи выгрузки» (ExportTask) — номера, перенос, краткая информация.</summary>
    internal static class ClashExportTaskTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        // Арман Кала → Блок 1 (6 моделей: Высокая, Средняя), Блок 2 (5); ЖК Нурлы (12)
        private static List<ExportTask> Sample(out ExportTask block1, out ExportTask high)
        {
            var project = new ExportTask { Name = "Арман Кала" };
            block1 = project.Add(new ExportTask { Name = "Блок 1", Models = 6 });
            high = block1.Add(new ExportTask { Name = "Высокая критичность" });
            block1.Add(new ExportTask { Name = "Средняя критичность" });
            project.Add(new ExportTask { Name = "Блок 2", Models = 5 });
            return new List<ExportTask> { project, new ExportTask { Name = "ЖК Нурлы", Models = 12 } };
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("номера строк — сами, по уровням; свёрнутый узел — без вложенных", () =>
            {
                var roots = Sample(out var block1, out _);
                var rows = ExportTask.Rows(roots, new HashSet<ExportTask>());
                var numbers = string.Join(" ", rows.Select(r => r.Number + ":" + r.Level));
                Check(numbers == "1:0 1.1:1 1.1.1:2 1.1.2:2 1.2:1 2:0", numbers);
                rows = ExportTask.Rows(roots, new HashSet<ExportTask> { block1 });
                numbers = string.Join(" ", rows.Select(r => r.Number));
                Check(numbers == "1 1.1 1.2 2", "свёрнут блок 1: " + numbers);
            });
            yield return Case("перенос — только внутри своего уровня, номера пересчитываются", () =>
            {
                var roots = Sample(out var block1, out var high);
                var block2 = roots[0].Children[1];
                Check(ExportTask.MoveTo(roots[0].Children, block2, block1), "блок 2 — на место блока 1");
                var rows = ExportTask.Rows(roots, new HashSet<ExportTask>());
                Check(rows[1].Task == block2 && rows[1].Number == "1.1" && rows[2].Task == block1 && rows[2].Number == "1.2",
                    string.Join(" ", rows.Select(r => r.Number + " " + r.Task.Name)));
                Check(!ExportTask.MoveTo(roots[0].Children, high, block2), "критичность — не на уровень блоков");
                Check(!ExportTask.MoveTo(roots, block1, roots[1]), "блок — не на уровень проектов");
            });
            yield return Case("моделей в выгрузке: свои, сумма вложенных, у критичности — модели блока", () =>
            {
                var roots = Sample(out var block1, out var high);
                Check(block1.AllModels == 6, "блок 1: " + block1.AllModels);
                Check(high.AllModels == 6, "критичность — модели блока: " + high.AllModels);
                Check(roots[0].AllModels == 11, "проект — сумма блоков: " + roots[0].AllModels);
                Check(new ExportTask().AllModels == 0, "пустая задача");
            });
            yield return Case("краткая информация: модели, выгрузка, не вышедшие, идёт у коллеги", () =>
            {
                var t = new ExportTask { Models = 6 };
                Check(t.Summary() == "6 моделей · не выгружалось", t.Summary());
                t.LastRun = new DateTime(2026, 10, 28, 14, 20, 0);
                Check(t.Summary() == "6 моделей · выгружено 28.10 14:20", t.Summary());
                t.Failed = 1;
                Check(t.Summary() == "6 моделей · 1 не вышла 28.10 14:20", t.Summary());
                t.Failed = 2;
                Check(t.Summary() == "6 моделей · 2 не вышли 28.10 14:20", t.Summary());
                t.RunningBy = "Иванов А.";
                t.RunningDone = 3;
                Check(t.Summary() == "идёт у Иванов А. — 3 из 6", t.Summary());
            });
            yield return Case("«модель / модели / моделей» по числу", () =>
            {
                var words = string.Join(" ", new[] { 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111 }.Select(n => n + " " + ExportTask.ModelsWord(n)));
                Check(words == "1 модель 2 модели 4 модели 5 моделей 11 моделей 12 моделей 14 моделей 21 модель 22 модели 25 моделей 111 моделей", words);
            });
            yield return Case("удаление: вложенных на всех уровнях", () =>
            {
                var roots = Sample(out var block1, out _);
                Check(roots[0].Descendants == 4, "у проекта: " + roots[0].Descendants);
                roots[0].Remove(block1);
                Check(roots[0].Descendants == 1 && block1.Parent == null, "после удаления блока 1: " + roots[0].Descendants);
            });
        }
    }
}
