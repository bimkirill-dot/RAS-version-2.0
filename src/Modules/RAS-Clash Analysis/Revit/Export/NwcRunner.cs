using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.UI;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// «Запустить» у строки «Задач выгрузки»: план (что выгружается, что нет и почему, несостыковки) → вопрос «Начать?» → журнал →
    /// окно хода → копия журнала в папку задачи. Журналы — %LocalAppData%\RAS_v_2.0\Logs\Выгрузка NWC (у каждого свои) и
    /// «Журналы выгрузки» в папке задачи (видны всем).
    /// </summary>
    internal static class NwcRunner
    {
        public static string JournalDir => Path.Combine(Path.GetDirectoryName(RASv2.Core.Log.FilePath), "Выгрузка NWC");

        public static void Start(Window owner, UIApplication ui, ExportTask task, ExportSettings common, int year)
        {
            const string title = "Выгрузка NWC";
            if (ui == null)
            {
                MessageBox.Show(owner, "Выгрузка работает только в Revit.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            {
                MessageBox.Show(owner, $"В этом Revit {year} нет модуля экспорта Navisworks (NWC). Установите «Navisworks NWC Export Utility» для Revit {year} и повторите.",
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var notes = new List<string>();
            var jobs = NwcRunPlan.Build(task, common, year, notes);
            var todo = jobs.Count(j => j.Skip == null);
            if (todo == 0)
            {
                var why = jobs.Count == 0 ? "у «" + task.Name + "» и её подзадач нет моделей." : "все модели пропускаются:\n" + string.Join("\n", jobs.Select(j => "• " + j.ModelName + " — " + j.Skip));
                MessageBox.Show(owner, "Выгружать нечего: " + why, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ask = new StringBuilder();
            ask.AppendLine($"Выгрузить NWC «{task.Name}»: {todo} {ExportTask.ModelsWord(todo)}.");
            ask.AppendLine("Revit будет занят до конца выгрузки; модели открываются копиями, сами модели не меняются.");
            var skipped = jobs.Where(j => j.Skip != null).ToList();
            if (skipped.Count > 0)
            {
                ask.AppendLine().AppendLine($"Пропускаются ({skipped.Count}):");
                foreach (var j in skipped.Take(8)) ask.AppendLine($"• {j.ModelName} — {j.Skip}");
                if (skipped.Count > 8) ask.AppendLine($"• … ещё {skipped.Count - 8}");
            }
            if (notes.Count > 0)
            {
                ask.AppendLine().AppendLine("Обратите внимание:");
                foreach (var n in notes.Take(6)) ask.AppendLine("• " + n);
                if (notes.Count > 6) ask.AppendLine($"• … ещё {notes.Count - 6} — в журнале");
            }
            ask.AppendLine().Append("Начать?");
            if (MessageBox.Show(owner, ask.ToString(), title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var root = ExportNodes.Root(task);
            var journalPath = Path.Combine(JournalDir, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {Safe(root.Name)}{(root == task ? "" : " — " + Safe(task.Name))}.log");
            ExportJournal journal;
            try
            {
                journal = new ExportJournal(journalPath);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: журнал не создан — " + journalPath, ex);
                MessageBox.Show(owner, "Журнал выгрузки не создался: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            using (journal)
            {
                var exporter = new NwcExporter(ui, journal, year);
                exporter.Header(task, common, jobs, notes);
                var items = jobs.Select(j => new NwcProgress { Job = j }).ToList();
                var nwcFolder = items.Select(p => Path.GetDirectoryName(p.Job.NwcPath)).FirstOrDefault();
                var window = new ExportRunWindow(task.Name, items, journalPath, nwcFolder,
                    w => exporter.Run(items, w.Changed, () => w.Cancelled)) { Owner = owner };
                window.ShowDialog();
                ShareJournal(journal, ExportNodes.FolderOf(root));
            }
        }

        /// <summary>Копия журнала — в «Журналы выгрузки» папки задачи: коллеги видят, что и когда выгружалось. Не вышло — только в журнал.</summary>
        private static void ShareJournal(ExportJournal journal, string taskFolder)
        {
            if (string.IsNullOrWhiteSpace(taskFolder)) return;
            try
            {
                var dir = Path.Combine(taskFolder, "Журналы выгрузки");
                var target = Path.Combine(dir, Path.GetFileName(journal.FilePath));
                journal.Line("копия журнала: " + target);
                Directory.CreateDirectory(dir);
                File.WriteAllText(target, journal.ReadAll(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                journal.Line("копия журнала в папку задачи не записана: " + ex.Message);
            }
        }

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        }
    }
}
