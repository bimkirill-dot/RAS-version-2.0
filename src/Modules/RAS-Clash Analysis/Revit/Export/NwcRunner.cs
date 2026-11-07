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
    /// «Запустить» у строки «Задач выгрузки»: план (что выгружается, что нет и почему, несостыковки) → сразу журнал →
    /// окно хода → копия журнала в папку задачи. Журналы — %LocalAppData%\RAS_v_2.0\Logs\Выгрузка NWC (у каждого свои) и
    /// «Журналы выгрузки» в папке задачи (видны всем).
    /// </summary>
    internal static class NwcRunner
    {
        public static string JournalDir => Path.Combine(Path.GetDirectoryName(RASv2.Core.Log.FilePath), "Выгрузка NWC");

        /// <param name="force">Shift при «Запустить»: выгрузить и NWC, модели которых не менялись.</param>
        public static void Start(Window owner, UIApplication ui, ExportTask task, ExportSettings common, int year, bool force)
        {
            const string title = "Выгрузка NWC";
            if (ui == null)
            {
                MessageBox.Show(owner, "Выгрузка работает только в Revit.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var notes = new List<string>();
            var jobs = NwcRunPlan.Build(task, common, year, notes);
            // NWF строк с галочкой «Формировать NWF» — после NWC (DECISIONS.md модуля, «Этап 2 — сборка NWF»)
            var nwfPlans = NwfRunPlan.Build(task, common);
            var todo = jobs.Count(j => j.Skip == null) + nwfPlans.Count(n => n.Skip == null);
            if (todo == 0)
            {
                var why = jobs.Count == 0 && nwfPlans.Count == 0 ? "у «" + task.Name + "» и её подзадач нет моделей." : "всё пропускается:\n" +
                          string.Join("\n", jobs.Select(j => "• " + j.ModelName + " — " + j.Skip)
                              .Concat(nwfPlans.Select(n => "• " + ExportNodes.FileName(n.NwfPath) + " — " + n.Skip)));
                MessageBox.Show(owner, "Выгружать нечего: " + why, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // без вопроса «Начать?» (разработчик, 04.11.2026: «после нажатия сразу выгрузка начинается»): пропуски с причинами — строками
            // окна хода, несостыковки — в журнале
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
                var runTitle = NwcExporter.Title(task);
                var exporter = new NwcExporter(ui, journal, year, force, common.ParallelCopies);
                var items = jobs.Select(j => new NwcProgress { Job = j }).ToList();

                // модели другой версии Revit — фоновому Revit их версии (разработчик, 05.11.2026: «пусть плагин запускает Revit 2023»):
                // Revit Server — по версии хранилища, файл — по версии самого файла; свои — здесь
                var byYear = items.Where(p => p.Job.Skip == null)
                    .GroupBy(p => NwcWorkers.RunYear(p.Job.Model, FileYear(p.Job.Model), year)).ToList();
                var local = byYear.Where(g => g.Key == year).SelectMany(g => g).ToList();
                var others = byYear.Where(g => g.Key != year).ToList();
                foreach (var g in others.Where(g => !File.Exists(NwcRemote.RevitExe(g.Key))))
                    foreach (var p in g)
                        p.Job.Skip = $"модель Revit {g.Key} — на этом компьютере нет Revit {g.Key}";
                others = others.Where(g => File.Exists(NwcRemote.RevitExe(g.Key))).ToList();
                // своих моделей нет — экспортёр этого Revit не нужен (Revit 2026 со сломанным экспортёром отдаёт модели 2023 Revit 2023)
                if (local.Count > 0 && !OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                {
                    foreach (var p in local) p.Job.Skip = $"в этом Revit {year} нет модуля экспорта Navisworks (NWC) — установите «Navisworks NWC Export Utility» для Revit {year}";
                    journal.Error($"в Revit {year} нет модуля экспорта Navisworks (NWC): {local.Count} {ExportTask.ModelsWord(local.Count)} не выгружаются");
                    local.Clear();
                }
                // шапка — после раскладки: пропуски «нет Revit …», «нет модуля экспорта» — уже в плане
                exporter.Header(runTitle, common, jobs, notes);
                NwfStage.Header(journal, nwfPlans);
                var nwfItems = NwfStage.Items(nwfPlans);

                var nwcFolder = items.Concat(nwfItems).Select(p => Path.GetDirectoryName(p.Job.NwcPath)).FirstOrDefault();
                var remoteJournals = new List<string>();
                var window = new ExportRunWindow(task.Name, items.Concat(nwfItems).ToList(), journalPath, nwcFolder, w =>
                {
                    var remotes = others.Select(g =>
                    {
                        var path = Path.ChangeExtension(journalPath, null) + $" — Revit {g.Key}.log";
                        remoteJournals.Add(path);
                        return NwcRemote.Send(g.Key, g.ToList(), runTitle, path, force, common.ParallelCopies, journal);
                    }).Where(r => r != null).ToList();
                    void Poll()
                    {
                        foreach (var r in remotes) r.Poll(w.Changed);
                    }
                    bool Cancelled()
                    {
                        if (w.Cancelled) remotes.ForEach(r => r.Cancel());
                        return w.Cancelled;
                    }
                    // свои — здесь; ход фоновых — между шагами своих
                    var mine = items.Where(p => p.Job.Skip != null || local.Contains(p)).ToList();
                    exporter.Run(mine, p =>
                    {
                        Poll();
                        w.Changed(p);
                    }, Cancelled);
                    // свои закончены — ждать фоновые
                    while (remotes.Any(r => !r.Finished))
                    {
                        Cancelled();
                        Poll();
                        w.Pump(1000);
                    }
                    // NWC готовы (свои и фоновых) — NWF; остановили — NWF не собираются
                    if (w.Cancelled) NwfStage.Stopped(nwfItems, w.Changed);
                    else NwfStage.Run(nwfPlans, nwfItems, journal, w.Changed, () => w.Cancelled, w.Pump);
                }) { Owner = owner };
                window.ShowDialog();
                ShareJournal(journal, ExportNodes.FolderOf(root));
                foreach (var path in remoteJournals.Where(File.Exists)) ShareFile(journal, path, ExportNodes.FolderOf(root));
            }
        }

        /// <summary>Версия Revit файла модели (не Revit Server): из самого файла; не прочитался — 0 (выгружает этот Revit).</summary>
        private static int FileYear(ExportModel model)
        {
            if (ExportNodes.IsServerPath(model.Path) || !File.Exists(model.Path)) return 0;
            try
            {
                return int.TryParse(BasicFileInfo.Extract(model.Path).Format, out var y) ? y : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Журнал фонового Revit — тоже в «Журналы выгрузки» папки задачи.</summary>
        private static void ShareFile(ExportJournal journal, string path, string taskFolder)
        {
            if (string.IsNullOrWhiteSpace(taskFolder)) return;
            try
            {
                var dir = Path.Combine(taskFolder, "Журналы выгрузки");
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, Path.GetFileName(path)), true);
            }
            catch (Exception ex)
            {
                journal.Line("копия журнала фонового Revit в папку задачи не записана: " + ex.Message);
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
