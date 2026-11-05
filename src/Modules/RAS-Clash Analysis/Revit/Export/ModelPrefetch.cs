using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Копии моделей Revit Server наперёд (разработчик, 04.11.2026: «по умолчанию 4 копии»): пока Revit открывает модель и выгружает
    /// NWC, следующие копируются утилитой Revit Server (RevitServerTool createLocalRVT — отдельный процесс, без Revit). Замер
    /// 04.11.2026: поток Revit Server отдаёт ~5,5 МБ/с, потоки друг другу не мешают — три модели (3,4 ГБ) за 4:37 вместо ~10 минут.
    /// Копий на диске — не больше «сразу»: новая начинается, когда Revit закончил с прежней (Done). Модель, до которой Revit дошёл
    /// раньше, чем началась её копия, Revit копирует сам (Take → null) — и первую: CopyModel быстрее одного потока утилиты (7,6 МБ/с).
    /// Модели из сетевых папок — только Revit (CopyModel): простая копия файла хранилища, в который синхронизируются, — не целая.
    /// </summary>
    internal sealed class ModelPrefetch : IDisposable
    {
        /// <summary>Копия наперёд: готова, если Task завершена и Error пуст.</summary>
        public sealed class Copy
        {
            public NwcJob Job;
            public string Folder;
            public string File;
            public Task Task;
            public string Error;
            public double Seconds;
            public long Size;
            public Process Process;
            // скопирована по просьбе Revit (CopyNow), а не наперёд — места в очереди не занимала
            public bool Own;
        }

        private readonly List<NwcJob> _jobs;
        private readonly int _parallel;
        private readonly int _year;
        private readonly string _tempRoot;
        private readonly ExportJournal _j;
        private readonly Dictionary<NwcJob, Copy> _started = new Dictionary<NwcJob, Copy>();
        private readonly HashSet<NwcJob> _claimed = new HashSet<NwcJob>();
        private readonly HashSet<Copy> _done = new HashSet<Copy>();
        private readonly SemaphoreSlim _slots;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _lock = new object();
        private Task _loop;

        /// <summary>Утилита Revit Server той версии, что и служба модели (у каждой версии Revit — своя).</summary>
        public static string ToolPath(int year) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "Revit " + year, "RevitServerToolCommand", "RevitServerTool.exe");

        public ModelPrefetch(IEnumerable<NwcJob> jobs, int parallel, int year, string tempRoot, ExportJournal journal)
        {
            _jobs = jobs.Where(j => ExportNodes.IsServerPath(j.Model.Path)).ToList();
            _parallel = Math.Max(0, parallel);
            _year = year;
            _tempRoot = tempRoot;
            _j = journal;
            _slots = new SemaphoreSlim(Math.Max(1, _parallel));
        }

        /// <summary>Начать; first — её Revit копирует сам (быстрее), остальные — по порядку, не больше «сразу».</summary>
        public void Start(NwcJob first)
        {
            if (first != null) Take(first);
            if (_parallel == 0 || _jobs.Count(j => j != first) == 0)
            {
                _j.Line(_parallel == 0 ? "копии наперёд выключены (0 в настройках) — каждую модель копирует Revit" : "копировать наперёд нечего — модели копирует Revit");
                return;
            }
            _j.Line($"копии наперёд: до {_parallel} сразу, утилитой Revit Server; моделей Revit Server — {_jobs.Count}, первую копирует Revit");
            _loop = Task.Run(Loop);
        }

        /// <summary>Revit дошёл до модели: копия наперёд (начата — дождаться) или null — копировать самому (больше её наперёд не начнут).</summary>
        public Copy Take(NwcJob job)
        {
            lock (_lock)
            {
                if (_started.TryGetValue(job, out var c)) return c;
                _claimed.Add(job);
                return null;
            }
        }

        /// <summary>
        /// Скопировать сейчас (Revit дошёл до модели, копия наперёд не начата, а сам Revit её не скопирует — модель Revit Server
        /// другой версии): утилитой её версии, в фоне — Revit ждёт Task, окно обновляется.
        /// </summary>
        public Copy CopyNow(NwcJob job)
        {
            var c = new Copy
            {
                Job = job, Own = true,
                Folder = Path.Combine(_tempRoot, "own_" + DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)),
            };
            lock (_lock)
            {
                _started[job] = c;
                c.Task = Task.Run(() => Run(c));
            }
            return c;
        }

        /// <summary>Revit закончил с копией — удалить её и освободить место следующей (копии наперёд).</summary>
        public void Done(Copy c)
        {
            lock (_lock)
            {
                if (!_done.Add(c)) return;
            }
            Delete(c.Folder);
            if (!c.Own) _slots.Release();
        }

        private async Task Loop()
        {
            try
            {
                foreach (var job in _jobs)
                {
                    await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_claimed.Contains(job))
                        {
                            _slots.Release();
                            continue;
                        }
                        var c = new Copy
                        {
                            Job = job,
                            Folder = Path.Combine(_tempRoot, "pre_" + DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)),
                        };
                        _started[job] = c;
                        c.Task = Task.Run(() => Run(c));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // «Отмена» или конец выгрузки — новых копий нет
            }
            catch (Exception ex)
            {
                _j.Line("копии наперёд остановились: " + ex.Message);
            }
        }

        private void Run(Copy c)
        {
            var clock = Stopwatch.StartNew();
            var name = c.Job.ModelName;
            try
            {
                var parts = c.Job.Model.Path.Trim().Substring("RSN://".Length).Split('/');
                var host = parts[0];
                var relative = string.Join("\\", parts.Skip(1));
                var year = c.Job.Model.Year > 0 ? c.Job.Model.Year : _year;
                var tool = ToolPath(year);
                if (!System.IO.File.Exists(tool))
                {
                    c.Error = $"нет утилиты Revit Server {year}: {tool}";
                    return;
                }
                Directory.CreateDirectory(c.Folder);
                c.File = Path.Combine(c.Folder, ExportNodes.FileName(c.Job.Model.Path));
                _j.Line($"   [{(c.Own ? "утилитой" : "наперёд")}] {name}: копия начата (утилита Revit Server {year})");
                var info = new ProcessStartInfo(tool, $"createLocalRVT \"{relative}\" -s {host} -d \"{c.File}\" -o")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var process = Process.Start(info))
                {
                    c.Process = process;
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    while (!process.WaitForExit(500))
                    {
                        if (_stop.IsCancellationRequested || clock.Elapsed > TimeSpan.FromMinutes(40))
                        {
                            try { process.Kill(); } catch (Exception) { /* уже вышел */ }
                            c.Error = _stop.IsCancellationRequested ? "остановлена" : "дольше 40 минут — прервана";
                            return;
                        }
                    }
                    // прервана из Dispose («Отмена» или конец выгрузки) — процесс убит, код -1
                    if (_stop.IsCancellationRequested)
                    {
                        c.Error = "остановлена";
                        return;
                    }
                    var text = (output.Result + " " + errors.Result).Replace("\r", " ").Replace("\n", " ").Trim();
                    if (process.ExitCode != 0 || !System.IO.File.Exists(c.File))
                    {
                        c.Error = $"утилита Revit Server: код {process.ExitCode}; {Tail(text)}";
                        return;
                    }
                }
                c.Size = new FileInfo(c.File).Length;
            }
            catch (Exception ex)
            {
                c.Error = ex.Message;
            }
            finally
            {
                c.Seconds = clock.Elapsed.TotalSeconds;
                c.Process = null;
                if (c.Error == null)
                {
                    _j.Line($"   [{(c.Own ? "утилитой" : "наперёд")}] {name}: готова за {NwcHistory.Duration(c.Seconds)}, {NwcHistory.SizeText(c.Size)}, " +
                            $"{c.Size / 1048576.0 / Math.Max(1, c.Seconds):0.0} МБ/с");
                    if (!c.Own) _j.StepDone("Копия наперёд (параллельно)", c.Seconds, true);
                }
                else
                    _j.Line($"   [{(c.Own ? "утилитой" : "наперёд")}] {name}: не вышла — {c.Error}");
            }
        }

        /// <summary>Остановить: новых копий нет, идущие — прервать, временные папки — удалить.</summary>
        public void Dispose()
        {
            _stop.Cancel();
            List<Copy> all;
            lock (_lock) all = _started.Values.ToList();
            foreach (var c in all)
            {
                try { c.Process?.Kill(); } catch (Exception) { /* уже вышел */ }
            }
            try { _loop?.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { /* остановлен */ }
            foreach (var c in all)
            {
                try { c.Task?.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { /* остановлена */ }
                bool consumed;
                lock (_lock) consumed = _done.Contains(c);
                if (!consumed) Delete(c.Folder);
            }
        }

        private void Delete(string folder)
        {
            try
            {
                if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                _j.Line("   временная папка не удалена: " + folder + " — " + ex.Message);
            }
        }

        private static string Tail(string text) => text.Length > 300 ? "…" + text.Substring(text.Length - 300) : text;
    }
}
