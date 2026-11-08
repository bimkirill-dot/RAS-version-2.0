using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>Где виден ход выгрузки — окно хода: строка изменилась, подождать не замирая, «Отмена».</summary>
    internal interface IRunView
    {
        void Changed(NwcProgress p);
        void Pump(int milliseconds);
        bool Cancelled { get; }
    }

    /// <summary>Чем кончился запуск: NWC и NWF с итогом, несостыковки плана, журнал (общий — в папке задачи — или свой).</summary>
    internal sealed class RunOutcome
    {
        public List<NwcProgress> Items { get; } = new List<NwcProgress>();
        public List<string> Notes { get; } = new List<string>();
        public string Journal { get; set; }
        public DateTime Started { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// «Запустить» у строки «Задач выгрузки»: план (что выгружать, что нет и почему, несостыковки) → сразу журнал → NWC (свои и
    /// фоновых Revit) → NWF → копия журнала в папку задачи. Ход — в окно хода (ExportRunWindow; убиралось в список задач и
    /// вернулось 07.11.2026). Журналы — %LocalAppData%\RAS_v_2.0\Logs\Выгрузка NWC (у каждого свои) и
    /// «Журналы выгрузки» в папке задачи (видны всем).
    /// </summary>
    internal static class NwcRunner
    {
        public static string JournalDir => Path.Combine(Path.GetDirectoryName(RASv2.Core.Log.FilePath), "Выгрузка NWC");

        /// <param name="only">«Повторить ошибки»: только эти NWC и NWF (и NWF, в которых эти NWC); null — всё.</param>
        /// <param name="planned">Что пойдёт в запуск — до начала: список раскладывает по строкам.</param>
        /// <returns>null — не начато (причина показана).</returns>
        public static RunOutcome Run(Window owner, IRunView view, UIApplication ui, ExportTask task, ExportSettings common, int year,
            ICollection<string> only, Action<List<NwcProgress>, string> planned)
        {
            const string title = "Выгрузка NWC";
            if (ui == null)
            {
                MessageBox.Show(owner, "Выгрузка работает только в Revit.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            var notes = new List<string>();
            var started = DateTime.Now;
            var jobs = NwcRunPlan.Build(task, common, year, notes, null, started);
            // NWF строк с галочкой «Формировать NWF» — после NWC (DECISIONS.md модуля, «Этап 2 — сборка NWF»)
            var nwfPlans = NwfRunPlan.Build(task, common);
            if (only != null)
            {
                // повтор: NWC — только если её путь в списке, RVT — так же (повтор RVT не выгружает NWC заново)
                var rvtNames = new HashSet<string>(only.Where(o => RunStates.KindOf(o) == "RVT").Select(ExportNodes.FileName), StringComparer.OrdinalIgnoreCase);
                foreach (var j in jobs)
                {
                    if (!only.Contains(j.NwcPath) && !j.Off) j.Off = true;
                    // RVT — и по имени файла: в папке RVT может быть {Дата} — у повтора она своя
                    if (j.RvtPath != null && !only.Contains(j.RvtPath) && !rvtNames.Contains(ExportNodes.FileName(j.RvtPath))) j.RvtPath = null;
                }
                jobs = jobs.Where(j => !j.Off || j.RvtPath != null).ToList();
                nwfPlans = nwfPlans.Where(n => only.Contains(n.NwfPath) || n.Nwcs.Any(only.Contains)).ToList();
            }
            AskTests(owner, task, nwfPlans);
            var todo = jobs.Count(j => j.Skip == null) + nwfPlans.Count(n => n.Skip == null);
            if (todo == 0)
            {
                var why = jobs.Count == 0 && nwfPlans.Count == 0 ? "у «" + task.Name + "» и её подзадач нет моделей." : "всё пропускается:\n" +
                          string.Join("\n", jobs.Select(j => "• " + j.ModelName + " — " + j.Skip)
                              .Concat(nwfPlans.Select(n => "• " + ExportNodes.FileName(n.NwfPath) + " — " + n.Skip)));
                MessageBox.Show(owner, "Выгружать нечего: " + why, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            // без вопроса «Начать?» (разработчик, 04.11.2026: «после нажатия сразу выгрузка начинается»): пропуски с причинами и
            // несостыковки — в подсказке к статусу строки и в журнале
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
                return null;
            }
            var outcome = new RunOutcome { Journal = journalPath, Started = DateTime.Now };
            outcome.Notes.AddRange(notes);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (journal)
            {
                var runTitle = NwcExporter.Title(task) + (only != null ? " — повтор ошибок" : "");
                var exporter = new NwcExporter(ui, journal, year, common.ParallelCopies);
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
                // своих моделей нет — экспортёр этого Revit не нужен (Revit 2026 со сломанным экспортёром отдаёт модели 2023 Revit 2023);
                // только RVT — экспортёр не нужен (08.11.2026)
                var needNwc = local.Where(p => !p.Job.Off).ToList();
                if (needNwc.Count > 0 && !OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                {
                    foreach (var p in needNwc) p.Job.Skip = $"в этом Revit {year} нет модуля экспорта Navisworks (NWC) — установите «Navisworks NWC Export Utility» для Revit {year}";
                    journal.Error($"в Revit {year} нет модуля экспорта Navisworks (NWC): {needNwc.Count} {ExportTask.ModelsWord(needNwc.Count)} не выгружаются");
                    local = local.Except(needNwc).ToList();
                }
                // шапка — после раскладки: пропуски «нет Revit …», «нет модуля экспорта» — уже в плане
                exporter.Header(runTitle, common, jobs, notes);
                NwfStage.Header(journal, nwfPlans);
                var nwfItems = NwfStage.Items(nwfPlans);
                outcome.Items.AddRange(items.Concat(nwfItems));
                planned?.Invoke(outcome.Items, journalPath);

                var remoteJournals = new List<string>();
                try
                {
                    var remotes = others.Select(g =>
                    {
                        var path = Path.ChangeExtension(journalPath, null) + $" — Revit {g.Key}.log";
                        remoteJournals.Add(path);
                        return NwcRemote.Send(g.Key, g.ToList(), runTitle, path, common.ParallelCopies, journal);
                    }).Where(r => r != null).ToList();
                    void Poll()
                    {
                        foreach (var r in remotes) r.Poll(view.Changed);
                    }
                    bool Cancelled()
                    {
                        if (view.Cancelled) remotes.ForEach(r => r.Cancel());
                        return view.Cancelled;
                    }
                    // свои — здесь; ход фоновых — между шагами своих
                    var mine = items.Where(p => p.Job.Skip != null || local.Contains(p)).ToList();
                    exporter.Run(mine, p =>
                    {
                        Poll();
                        view.Changed(p);
                    }, Cancelled);
                    // свои закончены — ждать фоновые
                    while (remotes.Any(r => !r.Finished))
                    {
                        Cancelled();
                        Poll();
                        view.Pump(1000);
                    }
                    // NWC готовы (свои и фоновых) — NWF; остановили — NWF не собираются
                    if (view.Cancelled) NwfStage.Stopped(nwfItems, view.Changed);
                    else NwfStage.Run(nwfPlans, nwfItems, journal, view.Changed, () => view.Cancelled, view.Pump);
                    RememberTests(task, nwfPlans, nwfItems, journal);
                }
                catch (Exception ex)
                {
                    // ошибка сквозь окно уронила бы Revit: то, что не кончилось, — ошибкой, причина — в журнал и ras.log
                    RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: запуск оборвался", ex);
                    journal.Error("запуск оборвался: " + ex.Message, ex);
                    foreach (var p in outcome.Items.Where(p => p.State == NwcState.Waiting || p.State == NwcState.Working))
                    {
                        p.State = NwcState.Failed;
                        p.Note = "запуск оборвался: " + ex.Message;
                        view.Changed(p);
                    }
                }
                outcome.Elapsed = clock.Elapsed;
                var shared = ShareJournal(journal, ExportNodes.FolderOf(root));
                if (shared != null) outcome.Journal = shared;
                foreach (var path in remoteJournals.Where(File.Exists)) ShareFile(journal, path, ExportNodes.FolderOf(root));
            }
            return outcome;
        }

        /// <summary>
        /// Файл проверок строки сменился с прошлой сборки её NWF — спросить (разработчик, 08.11.2026): «Да» — проверки в NWF заменятся
        /// новыми (результаты, статусы и комментарии прежних коллизий пропадут), «Нет» — прежние остаются, только прогон.
        /// </summary>
        private static void AskTests(Window owner, ExportTask task, List<NwfPlan> plans)
        {
            var path = RunStates.PathOf(task);
            if (path == null) return;
            var state = RunStates.Load(path);
            foreach (var p in plans.Where(p => p.Skip == null && p.Tests.Count > 0))
            {
                p.TestsSignature = RunStates.TestsSignature(p.Tests);
                var was = RunStates.TestsChanged(state, p.NwfPath, p.TestsSignature);
                if (was == null || !File.Exists(p.NwfPath)) continue;
                var text = $"Файл проверок у «{p.Row.Name}» изменился с прошлой сборки NWF ({was.At:dd.MM.yyyy HH:mm}).\n\n" +
                           "Сейчас: " + string.Join(", ", p.Tests.Select(Path.GetFileName)) + "\n" +
                           (was.Files.Count > 0 ? "Было: " + string.Join(", ", was.Files.Select(Path.GetFileName)) + "\n" : "") +
                           $"\nПроверки в NWF «{Path.GetFileName(p.NwfPath)}» заменятся новыми — результаты, статусы и комментарии прежних коллизий пропадут.\n\n" +
                           "Да — заменить. Нет — оставить прежние проверки (только прогон).";
                p.ReplaceTests = MessageBox.Show(owner, text, "Файл проверок изменился", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
            }
        }

        /// <summary>
        /// С какими файлами проверок собрана NWF — в состояние задачи (после сборки без ошибки). «Нет» в вопросе — прежняя подпись
        /// остаётся: в следующий раз спросит снова.
        /// </summary>
        private static void RememberTests(ExportTask task, List<NwfPlan> plans, List<NwcProgress> items, ExportJournal journal)
        {
            var path = RunStates.PathOf(task);
            if (path == null) return;
            var state = RunStates.Load(path);
            var marks = new List<NwfTests>();
            for (var i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                if (items[i].State != NwcState.Done || p.Tests.Count == 0) continue;
                var known = state.Tests.Any(x => string.Equals(x.Nwf, p.NwfPath, StringComparison.OrdinalIgnoreCase));
                if (known && !p.ReplaceTests && RunStates.TestsChanged(state, p.NwfPath, p.TestsSignature) != null) continue;
                marks.Add(new NwfTests { Nwf = p.NwfPath, Signature = p.TestsSignature ?? RunStates.TestsSignature(p.Tests), Files = p.Tests.ToList() });
            }
            if (marks.Count == 0) return;
            try
            {
                RunStates.Save(path, new RowRun[0], new ItemTime[0], marks);
            }
            catch (Exception ex)
            {
                journal.Line("какие файлы проверок у NWF — не записано: " + ex.Message);
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

        /// <summary>
        /// Копия журнала — в «Журналы выгрузки» папки задачи: коллеги видят, что и когда выгружалось. Путь копии; не вышло — null
        /// (причина — в журнал).
        /// </summary>
        private static string ShareJournal(ExportJournal journal, string taskFolder)
        {
            if (string.IsNullOrWhiteSpace(taskFolder)) return null;
            try
            {
                var dir = Path.Combine(taskFolder, "Журналы выгрузки");
                var target = Path.Combine(dir, Path.GetFileName(journal.FilePath));
                journal.Line("копия журнала: " + target);
                Directory.CreateDirectory(dir);
                File.WriteAllText(target, journal.ReadAll(), new UTF8Encoding(true));
                return target;
            }
            catch (Exception ex)
            {
                journal.Line("копия журнала в папку задачи не записана: " + ex.Message);
                return null;
            }
        }

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        }
    }
}
