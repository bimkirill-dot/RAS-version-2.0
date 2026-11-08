using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Модели другой версии Revit — фоновому Revit их версии (NwcWorker): задание — файлом в папку заданий; фонового Revit нет — запустить
    /// его свёрнутым (Revit.exe той версии, переменная RAS_NWC_WORKER); ход — из файла хода в строки окна; «Отмена» — файлом. Не взял
    /// задание за 10 минут (Revit ждёт ответа в каком-то окне) или закрылся, не закончив, — оставшиеся NWC с ошибкой и причиной.
    /// </summary>
    internal sealed class NwcRemote
    {
        private static readonly TimeSpan TakeWait = TimeSpan.FromMinutes(10);

        private readonly string _dir = NwcWorkers.Dir;
        private readonly string _id;
        private readonly List<NwcProgress> _items;
        private readonly ExportJournal _j;
        private readonly DateTime _sent = DateTime.Now;
        private Process _process;
        private bool _cancelled;

        public int Year { get; }
        public string Journal { get; }
        public bool Finished { get; private set; }

        private NwcRemote(int year, List<NwcProgress> items, string journal, ExportJournal main)
        {
            Year = year;
            _items = items;
            Journal = journal;
            _j = main;
            _id = year + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static string RevitExe(int year) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "Revit " + year, "Revit.exe");

        /// <summary>Отдать модели фоновому Revit их версии; null — не вышло (строки — с ошибкой, причина — в журнале).</summary>
        public static NwcRemote Send(int year, List<NwcProgress> items, string title, string journal, int parallel, ExportJournal main)
        {
            var remote = new NwcRemote(year, items, journal, main);
            foreach (var p in items) p.Step = $"ждёт фоновый Revit {year}";
            try
            {
                var handoff = new NwcHandoff
                {
                    Id = remote._id, Year = year, Title = title, Journal = journal, Parallel = parallel, Created = DateTime.Now,
                    Jobs = items.Select(p => NwcWorkers.ToHandoff(p.Job)).ToList(),
                };
                NwcWorkers.Write(NwcWorkers.JobPath(remote._dir, remote._id), handoff);
                main.Line($"Revit {year}: {items.Count} {ExportTask.ModelsWord(items.Count)} — фоновому Revit {year}; задание {remote._id}, его журнал — {journal}");
                var alive = NwcWorkers.Read<WorkerAlive>(NwcWorkers.AlivePath(remote._dir, year));
                if (NwcWorkers.IsAlive(alive, DateTime.Now, Running))
                {
                    main.Line($"Revit {year}: фоновый уже открыт (процесс {alive.ProcessId}) — возьмёт задание сразу");
                    remote._process = TryProcess(alive.ProcessId);
                }
                else
                    remote.Launch();
                return remote;
            }
            catch (Exception ex)
            {
                main.Error($"Revit {year}: задание не передано — {ex.Message}", ex);
                remote.FailAll($"фоновый Revit {year} не запустился: {ex.Message}");
                return null;
            }
        }

        private void Launch()
        {
            var exe = RevitExe(Year);
            if (!File.Exists(exe)) throw new FileNotFoundException("нет " + exe);
            var info = new ProcessStartInfo(exe, "/nosplash")
            {
                UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized, WorkingDirectory = Path.GetDirectoryName(exe),
            };
            info.EnvironmentVariables[NwcWorkers.EnvVar] = Year.ToString();
            _process = Process.Start(info);
            _j.Line($"Revit {Year}: запущен фоновый (процесс {_process?.Id}), свёрнут; откроется примерно за минуту");
        }

        /// <summary>Прочитать ход; изменившиеся строки — в changed. Пропал или не взял задание — закончить с ошибкой.</summary>
        public void Poll(Action<NwcProgress> changed)
        {
            if (Finished) return;
            var progress = NwcWorkers.Read<HandoffProgress>(NwcWorkers.ProgressPath(_dir, _id));
            if (progress != null)
            {
                foreach (var s in progress.Items)
                {
                    if (s.Index < 0 || s.Index >= _items.Count) continue;
                    var p = _items[s.Index];
                    var state = Enum.TryParse<NwcState>(s.State, out var st) ? st : p.State;
                    var note = s.Note;
                    var step = state == NwcState.Waiting ? $"ждёт фоновый Revit {Year}" : $"Revit {Year}: {s.Step}";
                    NwcState? Part(string s) => Enum.TryParse<NwcState>(s ?? "", out var x) ? x : (NwcState?)null;
                    var nwc = Part(s.Nwc);
                    var rvt = Part(s.Rvt);
                    if (p.State == state && p.Step == step && p.Note == note && p.Nwc == nwc && p.Rvt == rvt) continue;
                    p.State = state;
                    p.Step = step;
                    p.Note = note;
                    p.Nwc = nwc;
                    p.Rvt = rvt;
                    p.NwcNote = s.NwcNote;
                    p.RvtNote = s.RvtNote;
                    p.Time = TimeSpan.FromSeconds(s.Seconds);
                    changed(p);
                }
                if (progress.Finished)
                {
                    Finished = true;
                    if (progress.Error != null) _j.Error($"Revit {Year}: {progress.Error}");
                    // не дошедшие (Revit остановился) — отменены
                    foreach (var p in _items.Where(x => x.State == NwcState.Waiting || x.State == NwcState.Working))
                    {
                        p.State = NwcState.Skipped;
                        p.Note = progress.Error ?? "отменено";
                        changed(p);
                    }
                    _j.Line($"Revit {Year}: закончил — выгружено {_items.Count(x => x.State == NwcState.Done)}, с ошибкой {_items.Count(x => x.State == NwcState.Failed)}, " +
                            $"пропущено {_items.Count(x => x.State == NwcState.Skipped)}; журнал — {Journal}");
                    return;
                }
            }
            // фоновый пропал, не закончив
            if (_process != null && HasExited(_process))
            {
                FailAll($"фоновый Revit {Year} закрылся, не закончив — журнал: {Journal}", changed);
                return;
            }
            // задание не взято
            if (progress == null && File.Exists(NwcWorkers.JobPath(_dir, _id)) && DateTime.Now - _sent > TakeWait)
            {
                try { File.Delete(NwcWorkers.JobPath(_dir, _id)); } catch (Exception) { /* уже взял */ }
                FailAll($"фоновый Revit {Year} не взял задание за {TakeWait.TotalMinutes:0} минут — не ждёт ли он ответа в каком-то окне " +
                        "(значок Revit на панели задач)?", changed);
            }
        }

        /// <summary>«Отмена»: фоновый остановится после текущей модели; задание ещё не взято — убрать.</summary>
        public void Cancel()
        {
            if (_cancelled || Finished) return;
            _cancelled = true;
            try
            {
                File.WriteAllText(NwcWorkers.CancelPath(_dir, _id), DateTime.Now.ToString("O"));
                if (File.Exists(NwcWorkers.JobPath(_dir, _id)))
                {
                    File.Delete(NwcWorkers.JobPath(_dir, _id));
                    FailAll("отменено", null, NwcState.Skipped);
                }
            }
            catch (Exception ex)
            {
                _j.Line($"Revit {Year}: «Отмена» не передана — {ex.Message}");
            }
        }

        private void FailAll(string reason, Action<NwcProgress> changed = null, NwcState state = NwcState.Failed)
        {
            Finished = true;
            foreach (var p in _items.Where(x => x.State == NwcState.Waiting || x.State == NwcState.Working))
            {
                p.State = state;
                p.Note = reason;
                changed?.Invoke(p);
            }
            if (state == NwcState.Failed) _j.Error($"Revit {Year}: {reason}");
            else _j.Line($"Revit {Year}: {reason}");
        }

        private static bool Running(int id) => TryProcess(id) is Process p && !HasExited(p);

        private static Process TryProcess(int id)
        {
            try { return Process.GetProcessById(id); }
            catch (Exception) { return null; }
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch (Exception) { return false; }
        }
    }
}
