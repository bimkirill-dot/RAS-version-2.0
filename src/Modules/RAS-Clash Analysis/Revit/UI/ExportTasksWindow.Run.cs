using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RASv2.ClashAnalysis.Export;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Выгрузка и статусы — прямо в списке задач (DECISIONS.md модуля, «Статус, запуск, время — в списке задач», 07.11.2026;
    /// разработчик: «больше не открываем дополнительное окно с выгрузкой — сразу всё отображаем прямо в этом списке»): ▷ — запуск,
    /// у строк ветки — «Ждёт» / «Идёт выгрузка» / итог и время; ▷ у другой строки во время выгрузки — в очередь, ■ — остановить.
    /// Пока идёт выгрузка, задачи не правятся (окно отвечает только между шагами Revit). Итог запуска — файлом в папке задачи
    /// (RunStates): зелёная галочка, красный треугольник (ошибки; «Повторить» — только их), жёлтый — модель изменилась после своей
    /// NWC (плагин только предупреждает — что выгружать, решает человек), серый «?» — Revit Server дату не сообщил.
    /// </summary>
    public partial class ExportTasksWindow : IRunView
    {
        private sealed class RunRequest
        {
            public ExportTask Task;
            /// <summary>«Повторить ошибки» — только эти NWC и NWF; null — всё.</summary>
            public HashSet<string> Only;
            public RowRun Previous;
        }

        /// <summary>Строка ветки, которая выгружается: её NWC и NWF в запуске, когда пошла, когда кончилась.</summary>
        private sealed class RowLive
        {
            public HashSet<string> Paths;
            public DateTime? Start;
            public DateTime? End;
        }

        private readonly List<RunRequest> _queue = new List<RunRequest>();
        private RunRequest _active;
        private bool _running, _cancel, _closeAfter;
        private readonly Dictionary<ExportTask, RowLive> _live = new Dictionary<ExportTask, RowLive>();
        private List<NwcProgress> _runItems = new List<NwcProgress>();
        private DateTime _lastTick;

        // ── статусы: файлы состояния задач, даты NWC и моделей — в фоне, с запасом на повторные перестройки списка ──
        private readonly Dictionary<string, RunStateFile> _states = new Dictionary<string, RunStateFile>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime?> _nwcTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DateTime? Date, bool Answered, DateTime At)> _modelDates =
            new Dictionary<string, (DateTime?, bool, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private int _statusGeneration;

        private sealed class RowPlan
        {
            public RowItem Item;
            public string StatePath;
            public List<string> Paths;
            public List<(ExportModel Model, string Nwc)> Models;
        }

        /// <summary>Что у строки: её NWC и NWF (как у ▷), модели с их NWC, файл состояния задачи.</summary>
        private RowPlan PlanOf(RowItem item)
        {
            var jobs = NwcRunPlan.Build(item.Item, Settings, RevitYear, new List<string>());
            var nwf = NwfRunPlan.Build(item.Item, Settings);
            return new RowPlan
            {
                Item = item, StatePath = RunStates.PathOf(item.Item),
                Paths = jobs.Select(j => j.NwcPath).Concat(nwf.Select(n => n.NwfPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Models = jobs.Select(j => (j.Model, j.NwcPath)).ToList(),
            };
        }

        /// <summary>Статусы строк: сначала из того, что уже известно, затем — свежее из файлов и с серверов.</summary>
        private void LoadStatus(List<RowItem> items)
        {
            var generation = ++_statusGeneration;
            var plans = new List<RowPlan>();
            foreach (var item in items)
            {
                try
                {
                    plans.Add(PlanOf(item));
                }
                catch (Exception ex)
                {
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: статус строки — " + item.Name, ex);
                }
            }
            ApplyStatus(plans);
            var year = RevitYear;
            Task.Run(async () =>
            {
                var states = plans.Select(p => p.StatePath).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(p => p, RunStates.Load, StringComparer.OrdinalIgnoreCase);
                var nwcs = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in plans.SelectMany(p => p.Models.Select(m => m.Nwc)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (generation != _statusGeneration) return;
                    try { nwcs[path] = File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null; }
                    catch (Exception) { nwcs[path] = null; }
                }
                // файлы и даты NWC — сразу (статус, запуск, время), даты моделей — следом: сервер отвечает дольше
                Post(generation, () =>
                {
                    foreach (var s in states) _states[s.Key] = s.Value;
                    foreach (var n in nwcs) _nwcTimes[n.Key] = n.Value;
                    ApplyStatus(plans);
                });
                Dictionary<string, (DateTime?, bool, DateTime)> known;
                lock (_modelDates) known = _modelDates.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
                // дата модели — свежая в пределах 3 минут: список перестраивается после каждой галочки
                var models = plans.SelectMany(p => p.Models.Select(m => m.Model))
                    .Where(m => !string.IsNullOrWhiteSpace(m.Path) && !(known.TryGetValue(m.Path, out var k) && DateTime.Now - k.Item3 < TimeSpan.FromMinutes(3)))
                    .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                if (models.Count == 0) return;
                var gate = new SemaphoreSlim(6);
                await Task.WhenAll(models.Select(async m =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var d = await ModelDates.GetAsync(m, year).ConfigureAwait(false);
                        lock (_modelDates) _modelDates[m.Path] = (d.Date, d.Answered, DateTime.Now);
                    }
                    finally
                    {
                        gate.Release();
                    }
                })).ConfigureAwait(false);
                Post(generation, () => ApplyStatus(plans));
            });
        }

        private void Post(int generation, Action action) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation == _statusGeneration) action();
            }));

        private void ApplyStatus(List<RowPlan> plans)
        {
            foreach (var p in plans)
            {
                var file = p.StatePath != null && _states.TryGetValue(p.StatePath, out var f) ? f : null;
                var run = RunStates.Find(file, p.Item.Item);
                var estimate = RunStates.EstimateRow(p.Item.Item, file, t => t == p.Item.Item ? p.Paths : RowPaths(t), out var source);
                List<ModelCheck> checks;
                lock (_modelDates)
                    checks = p.Models.Select(m =>
                    {
                        var has = _modelDates.TryGetValue(m.Model.Path ?? "", out var d);
                        return new ModelCheck
                        {
                            Name = ExportNames.BareName(m.Model.Path), Model = has ? d.Date : null, Answered = !has || d.Answered,
                            Nwc = _nwcTimes.TryGetValue(m.Nwc, out var n) ? n : null,
                        };
                    }).ToList();
                // дату модели ещё не спросили — не «?»: подождать ответа
                var changed = RunStates.Changed(checks.Where(c => c.Model != null || !c.Answered), out var unknown);
                p.Item.SetStatus(run, changed, unknown, RunStates.EstimateText(estimate),
                    estimate == null ? (p.Paths.Count == 0 ? null : "Ещё не выгружалось — время появится после первой выгрузки")
                    : "Примерно: " + source);
            }
        }

        // ── запуск, очередь, остановка ──

        /// <summary>«Запустить» (▷): сразу; идёт выгрузка — строка в очередь (ещё раз — из очереди), у выгружаемой — «Остановить».</summary>
        private void Run_Click(object sender, RoutedEventArgs e)
        {
            var task = RowOf(sender).Item;
            if (_running)
            {
                Toggle(new RunRequest { Task = task });
                return;
            }
            if (!Leave()) return;
            // новая строка, от сохранения которой отказались, — исчезла
            if (!_roots.SelectMany(r => r.Self()).Contains(task)) return;
            _queue.Add(new RunRequest { Task = task });
            RunQueue();
        }

        /// <summary>«Повторить» у красного треугольника: заново — только NWC и NWF с ошибками (и NWF, где они).</summary>
        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row.LastRun == null || row.LastRun.Errors.Count == 0) return;
            var request = new RunRequest
            {
                Task = row.Item, Previous = row.LastRun,
                Only = new HashSet<string>(row.LastRun.Errors.Select(x => x.Path), StringComparer.OrdinalIgnoreCase),
            };
            if (_running)
            {
                Toggle(request);
                return;
            }
            if (!Leave()) return;
            _queue.Add(request);
            RunQueue();
        }

        /// <summary>Во время выгрузки: у выгружаемой строки — остановить, у строки в очереди — убрать, у остальных — в очередь.</summary>
        private void Toggle(RunRequest request)
        {
            if (_active != null && _active.Task == request.Task)
            {
                if (_cancel) return;
                if (MessageBox.Show(this, "Остановить выгрузку «" + request.Task.Name + "»? Остановится после текущей модели.", Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _cancel = true;
            }
            else
            {
                var queued = _queue.FirstOrDefault(q => q.Task == request.Task);
                if (queued != null) _queue.Remove(queued);
                else _queue.Add(request);
            }
            UpdateBusy();
        }

        private void RunQueue()
        {
            _running = true;
            UpdateBusy();
            try
            {
                while (_queue.Count > 0 && !_closeAfter)
                {
                    _active = _queue[0];
                    _queue.RemoveAt(0);
                    _cancel = false;
                    UpdateBusy();
                    RunOne(_active);
                }
            }
            finally
            {
                _active = null;
                _running = false;
                _queue.Clear();
                _live.Clear();
                UpdateBusy();
                Refresh();
            }
            if (_closeAfter) Close();
        }

        private void RunOne(RunRequest request)
        {
            RunOutcome outcome = null;
            try
            {
                outcome = NwcRunner.Run(this, this, Revit as Autodesk.Revit.UI.UIApplication, request.Task, Settings, RevitYear, request.Only,
                    items => StartLive(request, items));
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: не запустилась — " + request.Task.Name, ex);
                MessageBox.Show(this, "Выгрузка не запустилась: " + ex.Message + "\nПодробности — в журнале плагина.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            if (outcome != null) Finish(request, outcome);
            _live.Clear();
            _runItems = new List<NwcProgress>();
        }

        /// <summary>Что пойдёт в запуск — по строкам ветки: у каждой её NWC и NWF; строка запуска — сразу «Идёт выгрузка».</summary>
        private void StartLive(RunRequest request, List<NwcProgress> items)
        {
            _runItems = items;
            _live.Clear();
            var inRun = new HashSet<string>(items.Select(p => p.Job.NwcPath), StringComparer.OrdinalIgnoreCase);
            foreach (var row in request.Task.Self())
            {
                var paths = new HashSet<string>(RowPaths(row).Where(inRun.Contains), StringComparer.OrdinalIgnoreCase);
                if (row == request.Task) paths.UnionWith(inRun);
                if (paths.Count == 0) continue;
                _live[row] = new RowLive { Paths = paths, Start = row == request.Task ? DateTime.Now : (DateTime?)null };
            }
            UpdateLive(true);
        }

        private List<string> RowPaths(ExportTask row) =>
            NwcRunPlan.Build(row, Settings, RevitYear, new List<string>()).Select(j => j.NwcPath)
                .Concat(NwfRunPlan.Build(row, Settings).Select(n => n.NwfPath)).ToList();

        /// <summary>Итог — каждой строке ветки (и в файл задачи — его видят все); время NWC и NWF — для «Времени».</summary>
        private void Finish(RunRequest request, RunOutcome outcome)
        {
            var results = outcome.Items.Select(p => (p.Job.NwcPath, p.Job.ModelName,
                p.State == NwcState.Done ? ItemResult.Done : p.State == NwcState.Failed ? ItemResult.Failed : ItemResult.Skipped,
                p.Note ?? p.Job.Skip)).ToList();
            var now = DateTime.Now;
            var records = new List<RowRun>();
            foreach (var pair in _live)
            {
                var row = pair.Key;
                var live = pair.Value;
                var seconds = row == request.Task ? outcome.Elapsed.TotalSeconds
                    : live.Start != null ? ((live.End ?? now) - live.Start.Value).TotalSeconds : 0;
                var previous = request.Only != null ? (row == request.Task ? request.Previous : FindRun(row)) : null;
                records.Add(RunStates.Record(row, live.Paths, results, outcome.Started, seconds, outcome.Journal,
                    row == request.Task ? outcome.Notes : null, request.Only, previous));
            }
            var times = outcome.Items.Where(p => p.State == NwcState.Done && p.Time > TimeSpan.Zero)
                .Select(p => new ItemTime { Path = p.Job.NwcPath, Seconds = p.Time.TotalSeconds, At = now }).ToList();
            var path = RunStates.PathOf(request.Task);
            if (path == null) return;
            try
            {
                RunStates.Save(path, records, times);
                _states[path] = RunStates.Load(path);
            }
            catch (Exception ex)
            {
                // не записалось на сервер — хотя бы у себя до закрытия окна
                RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: состояние запуска не записано — " + path, ex);
                if (!_states.TryGetValue(path, out var file)) _states[path] = file = new RunStateFile();
                foreach (var r in records)
                {
                    file.Rows.RemoveAll(x => string.Equals(x.Key, r.Key, StringComparison.OrdinalIgnoreCase));
                    file.Rows.Add(r);
                }
                ShowBanner("Итог выгрузки не записан в папку задачи (" + ex.Message + ") — у коллег статус не обновится.");
            }
        }

        private RowRun FindRun(ExportTask row)
        {
            var path = RunStates.PathOf(row);
            return path != null && _states.TryGetValue(path, out var f) ? RunStates.Find(f, row) : null;
        }

        // ── несколько строк: Ctrl / Shift / Ctrl+A — пакетное копирование и удаление (разработчик, 07.11.2026) ──

        /// <summary>Ctrl+щелчок — добавить или убрать строку, Shift+щелчок — все строки от прежней до этой (как в проводнике).</summary>
        private void Pick(ExportTask item, bool range)
        {
            if (_picked.Count == 0)
            {
                // выбранная строка с правками в панели — сначала вопрос
                if (_selected != null && !Leave()) return;
                if (_selected != null && _roots.SelectMany(r => r.Self()).Contains(_selected))
                {
                    _picked.Add(_selected);
                    _anchor = _selected;
                }
            }
            var order = _items.Select(i => i.Item).ToList();
            if (range && _anchor != null && order.Contains(_anchor))
            {
                int a = order.IndexOf(_anchor), b = order.IndexOf(item);
                foreach (var t in order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    if (!_picked.Contains(t)) _picked.Add(t);
            }
            else
            {
                if (!_picked.Remove(item)) _picked.Add(item);
                _anchor = item;
            }
            ShowPicked();
        }

        /// <summary>Справа — «Выбрано: N строк», их список и «Копировать», «Удалить»; одна строка — её настройки, как обычно.</summary>
        private void ShowPicked()
        {
            if (_picked.Count <= 1)
            {
                var one = _picked.FirstOrDefault();
                _picked.Clear();
                Show(one);
                return;
            }
            _selected = null;
            _panel = null;
            var n = _picked.Count;
            var rows = ExportNodes.Rows(_roots, new HashSet<ExportTask>()).Where(r => _picked.Contains(r.Item)).ToList();
            var box = new System.Windows.Controls.StackPanel { Margin = new Thickness(24, 18, 24, 18) };
            box.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"Выбрано: {n} {ExportTask.RowsWord(n)}", FontSize = 14, FontWeight = FontWeights.SemiBold,
            });
            box.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.Join("\n", rows.Take(15).Select(r => r.Number + "   " + r.Item.Name)) + (rows.Count > 15 ? "\n…" : ""),
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16),
            });
            var buttons = new System.Windows.Controls.WrapPanel();
            var copy = new System.Windows.Controls.Button { Content = "Копировать", MinWidth = 120, Margin = new Thickness(0, 0, 8, 8) };
            copy.Click += (s, e) => CopyPicked();
            var delete = new System.Windows.Controls.Button { Content = "Удалить", MinWidth = 120, Margin = new Thickness(0, 0, 8, 8) };
            delete.Click += (s, e) => DeletePicked();
            var clear = new System.Windows.Controls.Button
            {
                Content = "Снять выбор", Style = (Style)FindResource("LinkButton"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 8),
            };
            clear.Click += (s, e) => ClearPicked();
            buttons.Children.Add(copy);
            buttons.Children.Add(delete);
            buttons.Children.Add(clear);
            box.Children.Add(buttons);
            Side.Content = box;
            foreach (var i in _items) i.IsSelected = _picked.Contains(i.Item);
        }

        private void ClearPicked()
        {
            _picked.Clear();
            Show(null);
        }

        /// <summary>Копии выбранных — с подзадачами, «(2)» в названии; подзадача — под своей строкой, задача — в конец; сразу на сервер.</summary>
        private void CopyPicked()
        {
            if (_running || _picked.Count == 0) return;
            var copies = ExportTask.CopyRows(_roots, _picked, NextOrder);
            foreach (var top in copies.Select(ExportNodes.Root).Distinct().ToList()) SaveTop(top);
            foreach (var c in copies.Where(c => c.Parent != null)) _collapsed.Remove(c.Parent);
            // выбраны — копии: видно, что появилось, их можно сразу удалить, если лишние
            var all = _roots.SelectMany(r => r.Self()).ToList();
            _picked.Clear();
            _picked.AddRange(copies.Where(all.Contains));
            Refresh();
            ShowPicked();
        }

        /// <summary>Удалить выбранные (с подзадачами) — после вопроса; задача — её файл на сервере, подзадача — из своей задачи.</summary>
        private void DeletePicked()
        {
            if (_running) return;
            var rows = ExportTask.Outermost(_roots, _picked);
            if (rows.Count == 0) return;
            var inner = rows.Sum(t => t.Descendants);
            var text = $"Удалить {rows.Count} {ExportTask.RowsWord(rows.Count)}" + (inner > 0 ? $" и {inner} {ExportTask.SubtasksWord(inner)} в них" : "") + "?\n\n" +
                       string.Join("\n", rows.Take(12).Select(t => "• " + t.Name)) + (rows.Count > 12 ? "\n…" : "");
            if (MessageBox.Show(this, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var touched = new List<ExportTask>();
            foreach (var t in rows)
            {
                if (t.Parent != null)
                {
                    var root = ExportNodes.Root(t);
                    t.Parent.Remove(t);
                    if (!touched.Contains(root)) touched.Add(root);
                    continue;
                }
                try
                {
                    _store.DeleteProject(t);
                    _roots.Remove(t);
                }
                catch (Exception ex)
                {
                    RASv2.Core.Log.Write("ClashAnalysis", "Задачи выгрузки: задача не удалена с сервера — " + t.Name, ex);
                    MessageBox.Show(this, "Задача «" + t.Name + "» не удалена с сервера: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            foreach (var root in touched.Where(_roots.Contains)) SaveTop(root);
            _picked.Clear();
            Refresh();
            Show(null);
        }

        /// <summary>Ctrl+A — выбрать все строки списка, Delete — удалить выбранные, Esc — снять выбор (не когда печатают в панели).</summary>
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_running) return;
            for (var f = System.Windows.Input.Keyboard.FocusedElement as DependencyObject; f != null; f = System.Windows.Media.VisualTreeHelper.GetParent(f))
                if (f == Side || f is System.Windows.Controls.TextBox) return;
            var ctrl = System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control;
            if (e.Key == System.Windows.Input.Key.A && ctrl && _items.Count > 1)
            {
                if (_picked.Count == 0 && _selected != null && !Leave()) return;
                _picked.Clear();
                _picked.AddRange(_items.Select(i => i.Item));
                ShowPicked();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Delete && _picked.Count > 1)
            {
                DeletePicked();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape && _picked.Count > 0)
            {
                ClearPicked();
                e.Handled = true;
            }
        }

        // ── ход выгрузки — в строки ──

        void IRunView.Changed(NwcProgress p)
        {
            var now = DateTime.Now;
            foreach (var live in _live.Values.Where(l => l.Paths.Contains(p.Job.NwcPath)))
            {
                if (p.State == NwcState.Working && live.Start == null) live.Start = now;
                if (live.End == null && _runItems.Where(x => live.Paths.Contains(x.Job.NwcPath)).All(Finished))
                {
                    live.Start = live.Start ?? now;
                    live.End = now;
                }
            }
            UpdateLive(false);
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        void IRunView.Pump(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Thread.Sleep(30);
            }
            UpdateLive(false);
        }

        bool IRunView.Cancelled => _cancel;

        private static bool Finished(NwcProgress p) => p.State == NwcState.Done || p.State == NwcState.Failed || p.State == NwcState.Skipped;

        /// <summary>«Ждёт» / «Идёт выгрузка» / «Готово» / «С ошибками» и время строк ветки; раз в секунду — не чаще (force — сразу).</summary>
        private void UpdateLive(bool force)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastTick).TotalMilliseconds < 900) return;
            _lastTick = now;
            foreach (var item in _items)
            {
                if (!_live.TryGetValue(item.Item, out var live))
                {
                    item.SetLive(_queue.Any(q => q.Task == item.Item) ? "В очереди" : null, null);
                    continue;
                }
                string text;
                if (live.Start == null) text = "Ждёт";
                else if (live.End == null) text = _cancel ? "Остановка…" : "Идёт выгрузка";
                else text = _runItems.Any(x => live.Paths.Contains(x.Job.NwcPath) && x.State == NwcState.Failed) ? "С ошибками" : "Готово";
                var time = live.Start == null ? "" : RunStates.Elapsed((live.End ?? now) - live.Start.Value);
                item.SetLive(text, time);
            }
        }

        /// <summary>Пока идёт выгрузка — правка закрыта: панель, кнопки сверху, галочки, «+», копия, корзина; ▷ / ■ — работают.</summary>
        private void UpdateBusy()
        {
            Toolbar.IsEnabled = !_running;
            Side.IsEnabled = !_running;
            foreach (var item in _items)
                item.SetBusy(_running, _active?.Task == item.Item);
            UpdateLive(true);
        }
    }
}
