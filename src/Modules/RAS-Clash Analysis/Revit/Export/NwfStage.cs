using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Сборка NWF после NWC (DECISIONS.md модуля, «Этап 2 — сборка NWF»): строки с галочкой «Формировать NWF». Navisworks без
    /// окна работает только в отдельном процессе .NET Framework 4.8 x64 — программа NwfTool (рядом со сборкой модуля); ей —
    /// задание файлом, от неё — строки хода (NwfTool.Parse). Окно хода тем временем перерисовывается и принимает «Отмену».
    /// </summary>
    internal static class NwfStage
    {
        // Navisworks молчит дольше — ждёт ответа в окне, которого не видно
        private static readonly TimeSpan Silence = TimeSpan.FromMinutes(10);

        public static string ToolPath => Path.Combine(Path.GetDirectoryName(typeof(NwfStage).Assembly.Location) ?? "", NwfTool.ExeName);

        /// <summary>Строки окна хода для NWF: имя — «S1_ВК.nwf», «куда» — путь NWF.</summary>
        public static List<NwcProgress> Items(List<NwfPlan> plans) =>
            plans.Select(p => new NwcProgress
            {
                Job = new NwcJob
                {
                    Model = new ExportModel { Path = p.NwfPath }, NwcPath = p.NwfPath, Skip = p.Skip, IsNwf = true,
                    Name = ExportNodes.FileName(p.NwfPath),
                },
                Step = "ждёт NWC",
            }).ToList();

        /// <summary>В шапку журнала: какие NWF и из каких NWC.</summary>
        public static void Header(ExportJournal j, List<NwfPlan> plans)
        {
            if (plans.Count == 0) return;
            j.Line($"NWF после NWC: {plans.Count(p => p.Skip == null)}, пропускаются: {plans.Count(p => p.Skip != null)}");
            foreach (var p in plans)
            {
                j.Line($"   NWF «{p.Row.Name}»: {p.NwfPath}" + (p.Skip != null ? " — пропускается: " + p.Skip : ""));
                foreach (var n in p.Nwcs) j.Line("      ← " + n);
                foreach (var h in p.HideSets) j.Line($"      скрыть: «{h.Name}» — {h.Text}");
                foreach (var t in p.Tests) j.Line("      проверки: " + t);
            }
            j.Line("");
        }

        /// <summary>Остановлено до NWF — строки NWF пропущены.</summary>
        public static void Stopped(List<NwcProgress> items, Action<NwcProgress> changed)
        {
            foreach (var p in items.Where(p => p.State == NwcState.Waiting))
            {
                p.State = NwcState.Skipped;
                p.Step = p.Note = p.Job.Skip ?? "выгрузка остановлена";
                changed(p);
            }
        }

        public static void Run(List<NwfPlan> plans, List<NwcProgress> items, ExportJournal j, Action<NwcProgress> changed, Func<bool> cancelled,
            Action<int> pump)
        {
            foreach (var p in items.Where(p => p.Job.Skip != null))
            {
                p.State = NwcState.Skipped;
                p.Step = p.Note = p.Job.Skip;
                changed(p);
            }
            var todo = Enumerable.Range(0, plans.Count).Where(i => plans[i].Skip == null).ToList();
            if (todo.Count == 0) return;
            j.Line("");
            j.Line($"══ Сборка NWF — {todo.Count}");
            var clock = Stopwatch.StartNew();

            void FailAll(string why)
            {
                foreach (var i in todo.Where(i => items[i].State == NwcState.Waiting || items[i].State == NwcState.Working))
                {
                    items[i].State = NwcState.Failed;
                    items[i].Note = why;
                    changed(items[i]);
                }
                j.Error("NWF: " + why);
            }

            var navisworks = NwfTool.FindNavisworks(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists);
            if (navisworks == null)
            {
                FailAll("на этом компьютере нет Navisworks Manage или Simulate (2023 и новее) — NWF собирает он");
                return;
            }
            if (!File.Exists(ToolPath))
            {
                FailAll("нет программы сборки NWF рядом с плагином: " + ToolPath);
                return;
            }

            var work = Path.Combine(NwcExporter.TempRoot, "nwf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var job = new NwfToolJob
            {
                NavisworksDir = navisworks, CancelFile = Path.Combine(work, "cancel"),
                BackupDir = Path.Combine(NwcRunner.JournalDir, "NWF до обновления"),
                Items = todo.Select(i => new NwfToolItem
                {
                    Nwf = plans[i].NwfPath, Nwcs = plans[i].Nwcs.ToList(), HideSets = plans[i].HideSets.Select(h => h.Xml).ToList(),
                    Tests = plans[i].Tests.ToList(),
                }).ToList(),
            };
            var jobFile = Path.Combine(work, "job.json");
            NwfTool.Write(jobFile, job);
            j.Line("Navisworks: " + navisworks + " — без окна; программа: " + ToolPath);

            var lines = new ConcurrentQueue<string>();
            var info = new ProcessStartInfo(ToolPath, "\"" + jobFile + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = work,
            };
            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (Exception ex)
            {
                FailAll("программа сборки NWF не запустилась: " + ex.Message);
                return;
            }
            using (process)
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lines.Enqueue("  " + e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var heard = Stopwatch.StartNew();
                var cancelSent = false;
                while (true)
                {
                    var exited = process.HasExited;
                    while (lines.TryDequeue(out var line))
                    {
                        heard.Restart();
                        Handle(line, todo, items, j, changed);
                    }
                    if (exited) break;
                    if (!cancelSent && cancelled())
                    {
                        cancelSent = true;
                        File.WriteAllText(job.CancelFile, "");
                        j.Line("NWF: «Отмена» — после текущей NWF");
                    }
                    if (heard.Elapsed > Silence)
                    {
                        try { process.Kill(); } catch (Exception) { }
                        FailAll($"Navisworks не отвечал {Silence.TotalMinutes:0} минут — остановлен (ждал ответа в окне, которого не видно?). " +
                                "Если в диспетчере задач остался Roamer.exe — закройте его");
                        break;
                    }
                    pump(300);
                }
                process.WaitForExit(5000);
                while (lines.TryDequeue(out var rest)) Handle(rest, todo, items, j, changed);
                var left = todo.Where(i => items[i].State == NwcState.Waiting || items[i].State == NwcState.Working).ToList();
                if (left.Count > 0)
                    FailAll("программа сборки NWF закрылась, не закончив" + (process.HasExited ? $" (код {process.ExitCode})" : "") + " — подробности выше в журнале");
            }
            try { Directory.Delete(work, true); } catch (Exception) { }

            j.Line($"NWF: собрано или обновлено {todo.Count(i => items[i].State == NwcState.Done)}, с ошибкой {todo.Count(i => items[i].State == NwcState.Failed)}, " +
                   $"пропущено {todo.Count(i => items[i].State == NwcState.Skipped)} из {todo.Count}; {NwcHistory.Duration(clock.Elapsed.TotalSeconds)}");
            foreach (var i in todo)
                j.Line($"  {NwcExporter.StateText(items[i].State),-12} {NwcHistory.Duration(items[i].Time.TotalSeconds),8}  {items[i].Job.ModelName}" +
                       (items[i].Note == null ? "" : " — " + items[i].Note));
        }

        private static void Handle(string line, List<int> todo, List<NwcProgress> items, ExportJournal j, Action<NwcProgress> changed)
        {
            var kind = NwfTool.Parse(line, out var index, out var result, out var seconds, out var text);
            if (kind == ' ' || index < 0 || index >= todo.Count)
            {
                j.Line("   [Navisworks] " + line.TrimStart());
                return;
            }
            var p = items[todo[index]];
            if (kind == '@')
            {
                p.State = NwcState.Working;
                p.Step = text;
                j.Line($"   [Navisworks] {p.Job.ModelName}: {text}");
            }
            else
            {
                p.State = result == NwfTool.Ok ? NwcState.Done : result == NwfTool.Skip ? NwcState.Skipped : NwcState.Failed;
                p.Note = text;
                p.Step = text;
                p.Time = TimeSpan.FromSeconds(seconds);
                if (p.State == NwcState.Failed) j.Error(p.Job.ModelName + " — " + text);
                else j.Line($"   [Navisworks] {p.Job.ModelName}: {text} — {NwcHistory.Duration(seconds)}");
            }
            changed(p);
        }
    }
}
