using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Набор записей одной транзакции (или одной синхронизации).
    /// Следит за лимитами: сверх MaxRecordsPerTransaction пишется одна сводная запись.
    /// </summary>
    internal sealed class RecordBatch
    {
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private readonly ChangeSource _source;
        private readonly OperationKind _op;
        private readonly string _tx;
        private readonly string _user;
        private readonly string _batchId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();
        private readonly Dictionary<ChangeKind, Dictionary<string, int>> _overflow = new Dictionary<ChangeKind, Dictionary<string, int>>();
        /// <summary>Элементы, параметры которых переснимем в фоне (не уложились в бюджет операции).</summary>
        private readonly List<long> _deferred = new List<long>();
        private int _paramBudget;
        private int _tooltipBudget;
        private int _added;
        /// <summary>Сколько записей об удалении получат полный набор параметров (чтобы массовое удаление не раздувало журнал).</summary>
        private int _stateBudget;

        /// <summary>Мелкая операция (обычная ручная правка) — для неё можно позволить больше работы.</summary>
        private readonly bool _small;

        public RecordBatch(ChangeTracker tr, DocumentTracker t, ChangeSource source, OperationKind op, string tx, string user,
            int totalIds = 0)
        {
            _tr = tr;
            _t = t;
            _source = source;
            _op = op;
            _tx = tx;
            _user = user;
            _small = totalIds <= 100;
            _paramBudget = tr.Settings.TrackParameters ? tr.Settings.MaxParamDiffPerTransaction : 0;
            _tooltipBudget = _small ? 100 : 0;
            _stateBudget = Math.Max(200, tr.Settings.MaxParamDiffPerTransaction * 2);
        }

        public int Count => _records.Count;

        public bool OverLimit => _added >= _tr.Settings.MaxRecordsPerTransaction;

        /// <summary>Изменение, сделанное текущим пользователем в этом сеансе.</summary>
        public void AddLive(ChangeKind kind, ElementId id)
        {
            var s = _tr.Settings;
            var doc = _t.Doc;
            var v = id.Value;

            if (kind == ChangeKind.Deleted)
            {
                _t.LiveDeletedSinceSync.Add(v);
                if (!_t.Info.TryGetValue(v, out var known))
                {
                    // после полной подготовки «неизвестный» элемент — служебный
                    if (_t.WarmupDone && !s.RecordNoise) return;
                    known = ElementDescriber.Unknown(v);
                }
                if (known.IsNoise && !s.RecordNoise) return;
                if (ChangesView3D.IsChangesViewName(known.Name)) return; // 3D-вид изменений удалили вручную — не правка модели
                var del = New(kind, _user);
                known.FillRecord(del);
                if (!OverLimit) AttachLastState(del, v); // сверх лимита запись всё равно уйдёт в сводку
                Add(del);
                return;
            }

            var el = doc.GetElement(id);
            if (el == null) return;
            if (ChangesView3D.IsChangesView(el))
            {
                // свой 3D-вид изменений: детализация, изоляция, камера — работа с видом, а не правка модели.
                // Запоминаем имя, чтобы и удаление вида вручную не записалось
                if (!_t.Info.ContainsKey(v)) _t.Info[v] = ElementDescriber.Describe(doc, el, _t);
                return;
            }
            _t.LiveChangedSinceSync.Add(v);

            if (OverLimit)
            {
                // сверх лимита — только дешёвый подсчёт по категориям, параметры переснимем в фоне
                var quick = ElementDescriber.QuickInfo(el);
                if (!_t.Info.ContainsKey(v)) _t.Info[v] = quick;
                if (quick.IsNoise && !s.RecordNoise) return;
                _t.Snapshots.Remove(v); // старое «было» уже неверно — переснимем в фоне в первую очередь
                Defer(v);
                CountOverflow(kind, string.IsNullOrEmpty(quick.Category) ? quick.ClassName : quick.Category);
                return;
            }

            var info = ElementDescriber.Describe(doc, el, _t);
            if (_t.Info.TryGetValue(v, out var prev) && !string.IsNullOrEmpty(prev.Creator)) info.Creator = prev.Creator;
            if (kind == ChangeKind.Created && _op != OperationKind.Undo && string.IsNullOrEmpty(info.Creator)) info.Creator = _user;
            _t.Info[v] = info;
            if (info.IsNoise && !s.RecordNoise) return;

            var rec = New(kind, _user);
            if (kind == ChangeKind.Modified && _t.Model.IsWorkshared && _tooltipBudget > 0)
            {
                _tooltipBudget--;
                var tip = ChangeTracker.Tooltip(doc, id);
                if (string.IsNullOrEmpty(info.Creator)) info.Creator = tip.Item1;
                rec.LastChangedBy = NullIfEmpty(tip.Item2);
            }
            info.FillRecord(rec);

            ParameterSnapshot before = null;
            var hasBefore = _t.Snapshots.CheckBeforeUse(v, () => ChangeTracker.VersionOf(el)) && _t.Snapshots.TryGet(v, out before);
            // «после» снимаем сразу, если есть «было» (нужна разница) или операция мелкая
            if (_paramBudget > 0 && (hasBefore || _small))
            {
                _paramBudget--;
                var after = ParameterSnapshot.Capture(doc, el, info.TypeName, _t);
                if (kind == ChangeKind.Modified && hasBefore)
                {
                    var diff = before.DiffTo(after);
                    if (diff.Count > 0) rec.Params = diff;
                    rec.BeforeKnown = true;
                }
                if (kind == ChangeKind.Created) rec.State = after.ToState();
                _tr.StoreSnapshot(_t, el, after, dirty: _source == ChangeSource.Live);
            }
            else
            {
                if (hasBefore) _t.Snapshots.Remove(v); // старое «было» больше не актуально
                Defer(v);
            }
            // явное «было неизвестно»: без этой отметки (старые записи) не отличить от «параметры не поменялись»
            if (kind == ChangeKind.Modified && rec.BeforeKnown != true) rec.BeforeKnown = false;
            Add(rec);
        }

        /// <summary>Изменение, пришедшее от другого пользователя.</summary>
        public void AddRemote(ChangeKind kind, ElementInfo info, string author, string creator, string lastChangedBy,
            string elementVersion, List<ParamChange> parameters, List<ParamValue> state = null)
        {
            var rec = New(kind, author);
            rec.RecordedBy = _tr.CurrentUser;
            rec.Creator = NullIfEmpty(creator);
            rec.LastChangedBy = NullIfEmpty(lastChangedBy);
            rec.ElementVersion = elementVersion;
            info.FillRecord(rec);
            if (parameters != null && parameters.Count > 0) rec.Params = parameters;
            if (kind == ChangeKind.Modified) rec.BeforeKnown = parameters != null; // разница посчитана (хоть и пустая) или «было» неизвестно
            if (state != null && state.Count > 0) rec.State = state;
            Add(rec);
        }

        /// <summary>Последние известные параметры удалённого элемента — в запись, чтобы их видела вся команда.</summary>
        public void AttachLastState(ChangeRecord rec, long id)
        {
            if (_stateBudget <= 0) return;
            if (!_t.Snapshots.TryGet(id, out var last)) return;
            rec.State = last.ToState();
            _stateBudget--;
        }

        /// <summary>Можно ли ещё приложить полный набор параметров к записи.</summary>
        public bool HasStateBudget => _stateBudget > 0 && !OverLimit;

        public void UseStateBudget() => _stateBudget--;

        public bool TakeParamBudget()
        {
            if (_paramBudget <= 0) return false;
            _paramBudget--;
            return true;
        }

        public void Defer(long id)
        {
            if (_tr.Settings.TrackParameters) _deferred.Add(id);
        }

        private ChangeRecord New(ChangeKind kind, string user)
        {
            var r = _tr.NewRecord(kind, _source, user, _op, _tx, _batchId);
            if (_source == ChangeSource.Live) r.Session = _t.SessionId; // по сеансу видно, дошло ли изменение до хранилища
            return r;
        }

        private void Add(ChangeRecord rec)
        {
            if (!OverLimit)
            {
                _records.Add(rec);
                _added++;
                return;
            }
            CountOverflow(rec.Kind, string.IsNullOrEmpty(rec.Category) ? rec.ClassName : rec.Category);
        }

        private void CountOverflow(ChangeKind kind, string category)
        {
            if (!_overflow.TryGetValue(kind, out var byCat))
            {
                byCat = new Dictionary<string, int>();
                _overflow[kind] = byCat;
            }
            var cat = string.IsNullOrEmpty(category) ? "?" : category;
            byCat.TryGetValue(cat, out var n);
            byCat[cat] = n + 1;
        }

        /// <summary>Отправить записи в журнал (частями — для длинных фоновых задач).</summary>
        public void Flush(bool final)
        {
            if (final)
            {
                foreach (var kv in _overflow)
                {
                    var total = kv.Value.Values.Sum();
                    var top = kv.Value.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key}: {x.Value}");
                    var r = New(kv.Key, _source == ChangeSource.Live ? _user : "");
                    r.ElementId = -1;
                    r.SummaryCount = total;
                    r.Name = $"… и ещё {total} элементов (превышен лимит записей на одну операцию)";
                    r.Category = string.Join(", ", top);
                    r.Group = ObjectGroup.Unknown;
                    if (_source != ChangeSource.Live) r.RecordedBy = _tr.CurrentUser;
                    _records.Add(r);
                }
                _overflow.Clear();
                if (_deferred.Count > 0)
                {
                    _tr.EnqueueRecapture(_t, new List<long>(_deferred), dirty: _source == ChangeSource.Live);
                    _deferred.Clear();
                }
            }
            if (_records.Count == 0) return;
            _tr.Emit(_t, new List<ChangeRecord>(_records));
            _records.Clear();
        }

        public void Commit() => Flush(true);

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Подготовка после открытия (в фоне, порциями):
    /// 1) сведения обо всех элементах — чтобы описывать удалённые;
    /// 2) снимки параметров: сначала виды, шаблоны, типы, уровни, оси, фильтры, материалы,
    ///    затем (если включён полный снимок) — все остальные элементы модели.
    /// </summary>
    internal sealed class WarmupJob : IdleWorkQueue.IJob
    {
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private int _phase;
        private List<ElementId> _ids;
        private readonly List<ElementId> _priority = new List<ElementId>();
        private readonly List<ElementId> _rest = new List<ElementId>();
        private List<ElementId> _snapIds;
        private int _i;
        private int _lastReported;

        public WarmupJob(ChangeTracker tr, DocumentTracker t)
        {
            _tr = tr;
            _t = t;
        }

        public bool IsObsolete => _t.IsClosed;

        public bool Step(UIApplication app, Stopwatch clock, long budgetMs)
        {
            var doc = _t.Doc;
            var s = _tr.Settings;
            switch (_phase)
            {
                case 0:
                    _ids = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds().ToList();
                    _ids.AddRange(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds());
                    _t.WarmupPhase = 1;
                    _t.WarmupTotal = _ids.Count;
                    _t.WarmupProgress = 0;
                    _phase = 1;
                    return false;

                case 1:
                    while (_i < _ids.Count && clock.ElapsedMilliseconds < budgetMs)
                    {
                        var id = _ids[_i++];
                        Element el;
                        try { el = doc.GetElement(id); } catch { continue; }
                        if (el == null) continue;
                        if (!_t.Info.TryGetValue(id.Value, out var info))
                        {
                            info = ElementDescriber.Describe(doc, el, _t, compact: true);
                            if (!info.IsNoise || s.RecordNoise) _t.Info[id.Value] = info;
                        }
                        if (!s.TrackParameters || (info.IsNoise && !s.RecordNoise)) continue;
                        if (IsPriority(el)) _priority.Add(id);
                        else if (s.SnapshotAllElements) _rest.Add(id);
                    }
                    Report(_i);
                    if (_i >= _ids.Count)
                    {
                        _ids = null;
                        _i = 0;
                        _snapIds = _priority.Concat(_rest).ToList();
                        _priority.Clear();
                        _rest.Clear();
                        _t.WarmupPhase = 2;
                        _t.WarmupTotal = _snapIds.Count;
                        _lastReported = 0;
                        Report(0);
                        _phase = 2;
                    }
                    return false;

                case 2:
                    // без файла на диске держим снимки только в памяти — и ограничиваемся её объёмом
                    var limit = _t.Snapshots.IsDiskBacked ? int.MaxValue : (int)(_t.Snapshots.Capacity * 0.8);
                    // массовый снимок: пока человек работает — короткими порциями, когда отошёл — крупными
                    var slice = UserActivity.IsIdle ? budgetMs : Math.Min(budgetMs, 15);
                    while (_i < _snapIds.Count && clock.ElapsedMilliseconds < slice && _t.Snapshots.Count < limit)
                    {
                        var id = _snapIds[_i++];
                        var el = doc.GetElement(id);
                        if (el == null) continue;
                        // уже снят (изменение, выделение, кэш прошлого сеанса) — пропускаем
                        if (_t.Snapshots.CheckBeforeUse(id.Value, () => ChangeTracker.VersionOf(el))) continue;
                        _tr.CaptureSnapshot(_t, el);
                    }
                    Report(_i);
                    if (_i < _snapIds.Count && _t.Snapshots.Count < limit) return false;
                    _snapIds = null;
                    _t.WarmupDone = true;
                    _t.WarmupProgress = _t.WarmupTotal;
                    _tr.ReportWarmup(_t);
                    return true;
            }
            return true;
        }

        private void Report(int progress)
        {
            _t.WarmupProgress = progress;
            var step = Math.Max(2000, _t.WarmupTotal / 50);
            if (progress == 0 || progress - _lastReported >= step || progress >= _t.WarmupTotal)
            {
                _lastReported = progress;
                _tr.ReportWarmup(_t);
            }
        }

        private static bool IsPriority(Element el)
        {
            return el is View || el is ElementType || el is FilterElement || el is Material || el is ProjectInfo
                   || el is Level || el is Grid || el is ParameterElement || el is Phase;
        }
    }

    /// <summary>Снимки параметров для списка элементов (большое выделение, отложенный переснимок).</summary>
    internal sealed class SnapshotJob : IdleWorkQueue.IJob
    {
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private readonly List<long> _ids;
        private readonly bool _force;
        private readonly bool _dirty;
        private int _i;

        /// <param name="force">true — переснять, даже если снимок уже есть (элемент изменился).</param>
        /// <param name="dirty">Элементы изменены правками, ещё не сохранёнными в файл модели.</param>
        public SnapshotJob(ChangeTracker tr, DocumentTracker t, List<long> ids, bool force = false, bool dirty = false)
        {
            _tr = tr;
            _t = t;
            _ids = ids;
            _force = force;
            _dirty = dirty;
        }

        public bool IsObsolete => _t.IsClosed;

        public bool Step(UIApplication app, Stopwatch clock, long budgetMs)
        {
            while (_i < _ids.Count && clock.ElapsedMilliseconds < budgetMs)
            {
                var v = _ids[_i++];
                if (!_force && _t.Snapshots.Contains(v)) continue;
                var el = _t.Doc.GetElement(new ElementId(v));
                if (el != null) _tr.CaptureSnapshot(_t, el, _dirty);
            }
            return _i >= _ids.Count;
        }
    }

    /// <summary>Снимки элементов открытого вида — раньше полного снимка модели.</summary>
    internal sealed class ViewPriorityJob : IdleWorkQueue.IJob
    {
        private const int MaxElements = 50000;
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private readonly long _viewId;
        private List<ElementId> _ids;
        private int _i;
        private bool _cancelled;

        public ViewPriorityJob(ChangeTracker tr, DocumentTracker t, long viewId)
        {
            _tr = tr;
            _t = t;
            _viewId = viewId;
        }

        public bool IsObsolete => _cancelled || _t.IsClosed;

        public void Cancel() => _cancelled = true;

        public bool Step(UIApplication app, Stopwatch clock, long budgetMs)
        {
            var doc = _t.Doc;
            if (_ids == null)
            {
                try
                {
                    _ids = new FilteredElementCollector(doc, new ElementId(_viewId))
                        .WhereElementIsNotElementType().ToElementIds().Take(MaxElements).ToList();
                }
                catch
                {
                    return true; // по этому виду элементы не собрать (спецификация, лист и т.п.)
                }
                return false;
            }
            while (_i < _ids.Count && clock.ElapsedMilliseconds < budgetMs)
            {
                var id = _ids[_i++];
                if (_t.Info.TryGetValue(id.Value, out var info) && info.IsNoise && !_tr.Settings.RecordNoise) continue;
                var el = doc.GetElement(id);
                if (el == null) continue;
                if (_t.Snapshots.CheckBeforeUse(id.Value, () => ChangeTracker.VersionOf(el))) continue;
                _tr.CaptureSnapshot(_t, el);
            }
            return _i >= _ids.Count;
        }
    }

    /// <summary>
    /// Разбор изменений других пользователей, полученных из файла-хранилища.
    /// Автор берётся из данных совместной работы (LastChangedBy / Creator).
    /// </summary>
    internal sealed class RemoteDiffJob : IdleWorkQueue.IJob
    {
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private readonly ChangeSource _source;
        private readonly List<KeyValuePair<ChangeKind, long>> _items = new List<KeyValuePair<ChangeKind, long>>();
        private readonly HashSet<long> _myChanged;
        private readonly HashSet<long> _myDeleted;
        private readonly RecordBatch _batch;
        private int _i;

        public RemoteDiffJob(ChangeTracker tr, DocumentTracker t, ChangeSource source,
            List<long> created, List<long> modified, List<long> deleted,
            HashSet<long> myChanged, HashSet<long> myDeleted)
        {
            _tr = tr;
            _t = t;
            _source = source;
            _myChanged = myChanged;
            _myDeleted = myDeleted;
            _items.AddRange(created.Select(x => new KeyValuePair<ChangeKind, long>(ChangeKind.Created, x)));
            _items.AddRange(modified.Select(x => new KeyValuePair<ChangeKind, long>(ChangeKind.Modified, x)));
            _items.AddRange(deleted.Select(x => new KeyValuePair<ChangeKind, long>(ChangeKind.Deleted, x)));

            string tx;
            switch (source)
            {
                case ChangeSource.Sync: tx = "Получено при синхронизации"; break;
                case ChangeSource.Reload: tx = "Получено при обновлении до последней версии"; break;
                default: tx = "Изменения с прошлого сеанса"; break;
            }
            _batch = new RecordBatch(tr, t, source, OperationKind.None, tx, "");
        }

        public bool IsObsolete => _t.IsClosed;

        public bool Step(UIApplication app, Stopwatch clock, long budgetMs)
        {
            var s = _tr.Settings;
            var doc = _t.Doc;
            var me = _tr.CurrentUser;

            while (_i < _items.Count && clock.ElapsedMilliseconds < budgetMs)
            {
                var item = _items[_i++];
                var v = item.Value;
                try
                {
                    if (item.Key == ChangeKind.Deleted)
                    {
                        if (_myDeleted.Contains(v)) continue;
                        if (!_t.Info.TryGetValue(v, out var known))
                        {
                            if (_t.WarmupDone && !s.RecordNoise) continue;
                            known = ElementDescriber.Unknown(v);
                        }
                        if (known.IsNoise && !s.RecordNoise) continue;
                        if (ChangesView3D.IsChangesViewName(known.Name)) continue; // 3D-вид изменений — не правка модели
                        List<ParamValue> lastState = null;
                        if (_batch.HasStateBudget && _t.Snapshots.TryGet(v, out var last))
                        {
                            lastState = last.ToState();
                            _batch.UseStateBudget();
                        }
                        _batch.AddRemote(ChangeKind.Deleted, known, "", known.Creator, null, null, null, lastState);
                        continue;
                    }

                    var id = new ElementId(v);
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    if (ChangesView3D.IsChangesView(el))
                    {
                        // 3D-вид изменений (свой или коллеги) попал в хранилище — это не правка модели; запомним его
                        // как служебный, чтобы и удаление не записалось
                        var view = ElementDescriber.Describe(doc, el, _t);
                        view.IsNoise = true;
                        _t.Info[v] = view;
                        _t.Snapshots.Remove(v);
                        continue;
                    }
                    var info = ElementDescriber.Describe(doc, el, _t);
                    var tip = ChangeTracker.Tooltip(doc, id);
                    var creator = tip.Item1;
                    var lastChangedBy = tip.Item2;
                    info.Creator = creator;
                    _t.Info[v] = info;
                    if (info.IsNoise && !s.RecordNoise)
                    {
                        _t.Snapshots.Remove(v);
                        continue;
                    }

                    // своё уже записано «вживую» — запись не нужна, но снимок устарел
                    bool mine = !string.IsNullOrEmpty(lastChangedBy) &&
                                string.Equals(lastChangedBy, me, StringComparison.OrdinalIgnoreCase) &&
                                (_myChanged.Contains(v) || _source == ChangeSource.Open);
                    if (mine)
                    {
                        _t.Snapshots.Remove(v);
                        _batch.Defer(v);
                        continue;
                    }

                    List<ParamChange> diff = null;
                    List<ParamValue> state = null;
                    if (s.TrackParameters && _batch.TakeParamBudget())
                    {
                        var after = ParameterSnapshot.Capture(doc, el, info.TypeName, _t);
                        if (item.Key == ChangeKind.Modified &&
                            _t.Snapshots.CheckBeforeUse(v, () => ChangeTracker.VersionOf(el)) &&
                            _t.Snapshots.TryGet(v, out var before)) diff = before.DiffTo(after);
                        if (item.Key == ChangeKind.Created) state = after.ToState();
                        _tr.StoreSnapshot(_t, el, after, dirty: false);
                    }
                    else
                    {
                        _t.Snapshots.Remove(v);
                        _batch.Defer(v);
                    }

                    var author = item.Key == ChangeKind.Created && string.IsNullOrEmpty(lastChangedBy) ? creator : lastChangedBy;
                    _batch.AddRemote(item.Key, info, author ?? "", creator, lastChangedBy, ElementVersion(el), diff, state);
                }
                catch (Exception ex)
                {
                    Diag.Log("Remote item failed: " + v, ex);
                    _t.Snapshots.Remove(v);
                    if (item.Key != ChangeKind.Deleted) _batch.Defer(v);
                }
                if (_batch.Count >= 1000) _batch.Flush(false);
            }

            if (_i < _items.Count) return false;
            _batch.Commit();
            return true;
        }

        private static string ElementVersion(Element el)
        {
            try { return el.VersionGuid.ToString("N"); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Актуализация журнала: проверить элементы по текущей модели (порциями в простое Revit).
    /// </summary>
    internal sealed class ActualizeJob : IdleWorkQueue.IJob
    {
        private readonly ChangeTracker _tr;
        private readonly DocumentTracker _t;
        private readonly List<ElementCheck> _checks;
        private readonly AuditContext _ctx;
        private readonly System.Threading.CancellationToken _cancel;
        private readonly Action<int, int> _progress;
        private readonly Action<List<AuditFinding>> _done;
        private readonly List<AuditFinding> _findings = new List<AuditFinding>();
        private bool? _hasClosedWorksets;
        private bool _notified;
        private int _i;
        private int _lastReport;

        public ActualizeJob(ChangeTracker tr, DocumentTracker t, List<ElementCheck> checks, AuditContext ctx,
            System.Threading.CancellationToken cancel, Action<int, int> progress, Action<List<AuditFinding>> done)
        {
            _tr = tr;
            _t = t;
            _checks = checks;
            _ctx = ctx;
            _cancel = cancel;
            _progress = progress;
            _done = done;
        }

        public bool IsObsolete
        {
            get
            {
                if (_cancel.IsCancellationRequested) return true; // отменено из панели (другая модель)
                if (!_t.IsClosed) return false;
                Finish(null); // модель закрыли — актуализация прервана
                return true;
            }
        }

        private void Finish(List<AuditFinding> result)
        {
            if (_notified) return;
            _notified = true;
            _done?.Invoke(result);
        }

        public bool Step(UIApplication app, Stopwatch clock, long budgetMs)
        {
            try
            {
                return StepCore(clock, budgetMs);
            }
            catch (Exception ex)
            {
                Diag.Log("Actualize failed", ex);
                Finish(null);
                return true;
            }
        }

        private bool StepCore(Stopwatch clock, long budgetMs)
        {
            var doc = _t.Doc;
            if (_hasClosedWorksets == null)
            {
                _hasClosedWorksets = false;
                if (doc.IsWorkshared)
                {
                    try
                    {
                        _hasClosedWorksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Any(ws => !ws.IsOpen);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log("Workset check failed", ex);
                        _hasClosedWorksets = true; // не знаем — считаем, что отсутствие элемента ничего не доказывает
                    }
                }
            }

            while (_i < _checks.Count && clock.ElapsedMilliseconds < budgetMs)
            {
                var check = _checks[_i++];
                // элемент изменён уже после чтения журнала — его записи могли ещё не дойти, не проверяем
                if (_t.AuditWatch != null && _t.AuditWatch.Contains(check.ElementId)) continue;
                try
                {
                    var states = new Dictionary<string, ElementState>(StringComparer.OrdinalIgnoreCase);
                    ElementState StateOf(ChangeRecord r)
                    {
                        var key = string.IsNullOrEmpty(r.UniqueId) ? "#" + check.ElementId : r.UniqueId;
                        if (!states.TryGetValue(key, out var st)) states[key] = st = Resolve(doc, check, r.UniqueId);
                        return st;
                    }
                    _findings.AddRange(JournalAudit.Evaluate(check, StateOf, _ctx));
                }
                catch (Exception ex)
                {
                    Diag.Log("Actualize check failed: " + check.ElementId, ex);
                }
            }

            if (_i - _lastReport >= 500 || _i >= _checks.Count)
            {
                _lastReport = _i;
                _progress?.Invoke(_i, _checks.Count);
            }
            if (_i < _checks.Count) return false;
            Finish(_findings);
            return true;
        }

        /// <summary>
        /// Найти элемент: сначала по UniqueId (ID может смениться при синхронизации), затем по ID.
        /// </summary>
        private ElementState Resolve(Document doc, ElementCheck check, string uniqueId)
        {
            Element el = null;
            if (!string.IsNullOrEmpty(uniqueId))
            {
                try { el = doc.GetElement(uniqueId); } catch { /* ignore */ }
            }
            if (el == null)
            {
                try { el = doc.GetElement(new ElementId(check.ElementId)); } catch { /* ignore */ }
            }
            var state = new ElementState { Exists = el != null };
            if (el == null)
            {
                // элемент может лежать в закрытом рабочем наборе
                state.Unverifiable = _hasClosedWorksets == true;
                return state;
            }
            state.UniqueId = el.UniqueId;
            if (check.NeedsValues)
            {
                var typeName = ElementDescriber.GetTypeName(doc, el);
                var current = ParameterSnapshot.Capture(doc, el, typeName, _t);
                state.CurrentValue = current.ValueByName;
            }
            return state;
        }
    }
}
