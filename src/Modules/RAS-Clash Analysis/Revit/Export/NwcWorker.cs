using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ClashAnalysis.Logic;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Фоновый Revit — выгрузчик NWC своей версии (разработчик, 05.11.2026: «если модель в Revit 2023 — пусть плагин запускает Revit
    /// 2023 для обработки; фоново, чтобы максимально быстро»). Запущен плагином другой версии (NwcRemote) с переменной окружения
    /// RAS_NWC_WORKER = год: свёрнут, задания берёт из папки заданий («{год}-….job»), выгружает тем же порядком (NwcExporter), ход —
    /// файлом; без заданий 15 минут — закрывается (второй запуск подряд — без минуты на открытие Revit). Revit пользователя без
    /// переменной задания не берёт.
    /// </summary>
    internal sealed class NwcWorker : IExternalEventHandler
    {
        private readonly int _year;
        private readonly ModuleLog _log;
        private readonly string _dir = NwcWorkers.Dir;
        private ExternalEvent _event;
        private Timer _timer;
        private IntPtr _window;
        private bool _minimized;
        private DateTime _idleSince = DateTime.Now;
        private volatile bool _busy;
        private volatile bool _exiting;

        public NwcWorker(int year, ModuleLog log)
        {
            _year = year;
            _log = log;
        }

        /// <summary>Этот Revit запущен фоновым выгрузчиком своей версии.</summary>
        public static bool Requested(int year) => Environment.GetEnvironmentVariable(NwcWorkers.EnvVar) == year.ToString();

        public void Start()
        {
            Directory.CreateDirectory(_dir);
            _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            _log.Info($"Фоновая выгрузка NWC: Revit {_year} ждёт задания в {_dir}");
        }

        /// <summary>Из простоя Revit (контекст Revit API): внешнее событие — создать, окно — свернуть (один раз).</summary>
        public void OnIdling(UIApplication app)
        {
            if (_event == null) _event = ExternalEvent.Create(this);
            if (_minimized) return;
            _minimized = true;
            try
            {
                _window = app.MainWindowHandle;
                ShowWindow(_window, ShowMinNoActive);
            }
            catch (Exception ex)
            {
                _log.Error("Фоновая выгрузка NWC: окно Revit не свернулось", ex);
            }
        }

        // таймер: «жив» — каждые 2 с; есть задание или пора закрываться — разбудить Revit
        private void Tick(object state)
        {
            if (_exiting) return;
            try
            {
                NwcWorkers.Write(NwcWorkers.AlivePath(_dir, _year), new WorkerAlive { ProcessId = Process.GetCurrentProcess().Id, At = DateTime.Now });
            }
            catch (Exception)
            {
                // папка недоступна на миг — отметимся через 2 с
            }
            if (_busy || _event == null) return;
            if (Pending() == null && DateTime.Now - _idleSince < NwcWorkers.IdleExit) return;
            _event.Raise();
            // свёрнутый Revit без мыши над окном может не взяться за внешнее событие — пустое сообщение будит его, не выводя вперёд
            if (_window != IntPtr.Zero) PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Старейшее задание своей версии; старше суток — не берётся (от прежних запусков).</summary>
        private string Pending()
        {
            try
            {
                return Directory.GetFiles(_dir, _year + "-*.job")
                    .Where(f => DateTime.Now - File.GetLastWriteTime(f) < TimeSpan.FromDays(1))
                    .OrderBy(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Execute(UIApplication app)
        {
            if (_busy || _exiting) return;
            _busy = true;
            try
            {
                var job = Pending();
                if (job == null)
                {
                    // запущен, а задания так и нет (основной Revit закрыли, пока этот запускался) — не висеть
                    if (DateTime.Now - _idleSince >= NwcWorkers.IdleExit) Exit(app, $"заданий нет {NwcWorkers.IdleExit.TotalMinutes:0} минут");
                    return;
                }
                var id = Path.GetFileNameWithoutExtension(job);
                var taken = NwcWorkers.TakenPath(_dir, id);
                File.Move(job, taken);
                var handoff = NwcWorkers.Read<NwcHandoff>(taken);
                if (handoff == null)
                {
                    NwcWorkers.Write(NwcWorkers.ProgressPath(_dir, id), new HandoffProgress { Id = id, Finished = true, Error = "задание не прочиталось", Updated = DateTime.Now });
                    return;
                }
                Run(app, id, handoff);
                // задание выполнено, новых нет — закрыться сразу (разработчик, 07.11.2026: «если плагин открывает Revit для выгрузки, то
                // он этот же Revit закрывает, когда закончит»; было — ждал новые задания 15 минут)
                if (Pending() == null) Exit(app, "выгрузка закончена");
            }
            catch (Exception ex)
            {
                _log.Error("Фоновая выгрузка NWC: задание не выполнено", ex);
            }
            finally
            {
                _busy = false;
                _idleSince = DateTime.Now;
            }
        }

        private void Run(UIApplication app, string id, NwcHandoff h)
        {
            var progressPath = NwcWorkers.ProgressPath(_dir, id);
            var cancelPath = NwcWorkers.CancelPath(_dir, id);
            var items = h.Jobs.Select(j => new NwcProgress { Job = NwcWorkers.FromHandoff(j) }).ToList();
            void Save(bool finished, string error = null)
            {
                try
                {
                    NwcWorkers.Write(progressPath, new HandoffProgress
                    {
                        Id = id, Finished = finished, Error = error, Updated = DateTime.Now,
                        Items = items.Select((p, i) => new HandoffState
                        {
                            Index = i, State = p.State.ToString(), Step = p.Step, Note = p.Note, Seconds = p.Time.TotalSeconds,
                        }).ToList(),
                    });
                }
                catch (Exception ex)
                {
                    _log.Error("Фоновая выгрузка NWC: ход не записан", ex);
                }
            }

            Save(false);
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            {
                foreach (var p in items)
                {
                    p.State = NwcState.Failed;
                    p.Note = $"в Revit {_year} нет модуля экспорта Navisworks (NWC)";
                }
                Save(true, $"в Revit {_year} нет модуля экспорта Navisworks (NWC)");
                return;
            }
            try
            {
                using (var journal = new ExportJournal(h.Journal))
                {
                    var exporter = new NwcExporter(app, journal, _year, h.Parallel);
                    exporter.Header(h.Title + $" — фоновый Revit {_year}", items[0].Job.Export, items.Select(p => p.Job).ToList(), new List<string>());
                    exporter.Run(items, p => Save(false), () => File.Exists(cancelPath));
                }
                Save(true);
            }
            catch (Exception ex)
            {
                _log.Error("Фоновая выгрузка NWC: " + h.Title, ex);
                Save(true, ex.Message);
            }
        }

        private void Exit(UIApplication app, string why)
        {
            _exiting = true;
            _timer?.Dispose();
            try { File.Delete(NwcWorkers.AlivePath(_dir, _year)); } catch (Exception) { /* не важно */ }
            _log.Info($"Фоновая выгрузка NWC: {why} — Revit {_year} закрывается");
            app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }

        public string GetName() => "RAS-Clash Analysis: фоновая выгрузка NWC";

        private const int ShowMinNoActive = 7;
        private const uint WmNull = 0;
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
