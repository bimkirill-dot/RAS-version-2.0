using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Сердце плагина: слушает события Revit и превращает их в записи журнала.
    /// Все обработчики вызываются в главном потоке Revit.
    /// </summary>
    public sealed class ChangeTracker
    {
        private readonly SpySettings _s;
        private readonly LogStore _store;
        private readonly IdleWorkQueue _idle;
        private readonly List<DocumentTracker> _docs = new List<DocumentTracker>();
        private static readonly string Machine = Environment.MachineName;

        public ChangeTracker(SpySettings settings, LogStore store, IdleWorkQueue idle)
        {
            _s = settings;
            _store = store;
            _idle = idle;
        }

        public SpySettings Settings => _s;
        public LogStore Store => _store;
        public DocumentTracker Active { get; private set; }
        public string CurrentUser { get; private set; } = Environment.UserName;

        /// <summary>Сменилась активная модель (null — нет открытых моделей).</summary>
        public event Action<DocumentTracker> ActiveModelChanged;
        /// <summary>Сменилось выделение в Revit (null — ничего не выделено).</summary>
        public event Action<SelectionCard> SelectionCardChanged;
        /// <summary>Изменилось состояние фоновой подготовки (для строки состояния).</summary>
        public event Action<DocumentTracker> WarmupProgressChanged;

        /// <summary>Почему открытую модель журнал не ведёт (отсоединена, не сохранена…); null — ведёт или моделей нет.</summary>
        public string UntrackedNote { get; private set; }

        /// <summary>Открытые отсоединённые модели: сохранят хранилищем или новым файлом — журнал начнётся с границы.</summary>
        private readonly List<Document> _detached = new List<Document>();

        // ───────────────────────────── документы ─────────────────────────────

        public DocumentTracker Find(Document doc)
        {
            if (doc == null) return null;
            for (int i = 0; i < _docs.Count; i++)
            {
                if (_docs[i].Matches(doc)) return _docs[i];
            }
            return null;
        }

        public DocumentTracker GetOrCreate(Document doc)
        {
            if (!ShouldTrack(doc)) return null;
            var t = Find(doc);
            if (t != null) return t;
            // подготовка — при первой активации вида, как после открытия; иначе модель, впервые попавшая
            // под отслеживание не через открытие (новый проект после «Сохранить как»), осталась бы без неё
            t = new DocumentTracker(doc, ModelIdentity.From(doc), _s) { PendingActivation = true };
            _docs.Add(t);
            UpdateUser(doc);
            return t;
        }

        private bool ShouldTrack(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject || doc.IsLinked) return false;
                if (doc.IsFamilyDocument && !_s.TrackFamilyDocuments) return false;
                // отсоединённая копия помнит своё хранилище: без этой проверки её правки ушли бы в журнал основной модели
                if (doc.IsDetached) return false;
                return !string.IsNullOrEmpty(doc.PathName); // несохранённые документы не отслеживаем
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Почему журнал не ведётся для этой модели — для надписи в журнале.</summary>
        private string NoteFor(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject) return null;
                if (doc.IsDetached) return "Модель отсоединена от хранилища — журнал не ведётся. Сохраните её хранилищем или новым файлом — журнал начнётся с этого момента.";
                if (doc.IsFamilyDocument && !_s.TrackFamilyDocuments) return "Это семейство — журнал ведётся только для моделей.";
                if (string.IsNullOrEmpty(doc.PathName)) return "Модель ещё не сохранена — журнал начнётся после сохранения.";
            }
            catch { /* ignore */ }
            return null;
        }

        private bool IsKnownDetached(Document doc) => _detached.Any(d => SameDocument(d, doc));

        private static bool SameDocument(Document a, Document b)
        {
            try { return ReferenceEquals(a, b) || (a.IsValidObject && b.IsValidObject && a.Equals(b)); }
            catch { return false; }
        }

        private void UpdateUser(Document doc)
        {
            try
            {
                var name = doc.Application.Username;
                if (!string.IsNullOrEmpty(name)) CurrentUser = name;
            }
            catch { /* ignore */ }
        }

        private void SetActive(DocumentTracker t)
        {
            if (ReferenceEquals(Active, t)) return;
            Active = t;
            ActiveModelChanged?.Invoke(t);
        }

        // ───────────────────────────── события Revit ─────────────────────────────

        public void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            Guard("DocumentOpened", () =>
            {
                if (e.Status != RevitAPIEventStatus.Succeeded) return;
                try
                {
                    if (e.Document.IsDetached)
                    {
                        _detached.Add(e.Document);
                        Diag.Log("Detached model opened — journal is not kept: " + e.Document.Title);
                        return;
                    }
                }
                catch { /* ignore */ }
                var t = GetOrCreate(e.Document);
                if (t == null) return;
                try { t.LocalPath = e.Document.PathName; } catch { /* ignore */ }
                WriteEvent(t, "Открытие модели", ModelEventType.Open);
                CheckPendingLocal(t);
                AbandonOtherLocalCopies(t);
                // Подготовку и разбор «что изменилось с прошлого сеанса» делаем при первой активации вида:
                // документы, открытые другими плагинами в фоне (без окна), так не нагружаются.
                t.PendingActivation = true;
                if (_s.DetectRemoteChanges && t.Model.IsWorkshared &&
                    Guid.TryParse(LastVersion(t.Model), out var g))
                {
                    t.PendingOpenVersion = g;
                }
                SaveVersionState(t);
            });
        }

        /// <summary>
        /// Страховка на случай, если ViewActivated не пришёл (например, при открытии):
        /// в простое проверяем активный документ Revit.
        /// </summary>
        public void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_docs.Count == 0) return;
            foreach (var d in _docs)
            {
                // сжатие файла снимков — только пока человек не трогает мышь и клавиатуру
                if (d.Snapshots.NeedsCompaction && UserActivity.IsIdle) d.Snapshots.CompactIfNeeded();
            }
            if (_docs.Any(d => d.ChangesViewId.HasValue || d.ChangesViewCleanupPending))
                Guard("ChangesView", () => CheckChangesView(sender as UIApplication));
            if (Active != null && !_docs.Any(d => d.PendingActivation)) return;
            Guard("Idling", () =>
            {
                var uidoc = (sender as UIApplication)?.ActiveUIDocument;
                if (uidoc == null) return;
                var t = Find(uidoc.Document);
                if (t == null) return;
                if (t.PendingActivation)
                {
                    OnFirstActivation(t);
                    var view = uidoc.ActiveView;
                    if (_s.TrackParameters && view != null) PrioritizeView(t, view);
                }
                SetActive(t);
            });
        }

        /// <summary>Первая активация вида документа: фоновая подготовка и изменения с прошлого сеанса.</summary>
        private void OnFirstActivation(DocumentTracker t)
        {
            t.PendingActivation = false;
            HashSet<long> remote = null;
            if (t.PendingOpenVersion.HasValue)
            {
                t.VersionBeforeSync = t.PendingOpenVersion;
                t.PendingOpenVersion = null;
                remote = ProcessRemote(t, ChangeSource.Open);
                SaveVersionState(t);
            }
            VerifyCachedSnapshots(t, remote);
            t.ChangesViewCleanupPending = true; // свой 3D-вид изменений, оставшийся с прошлого сеанса, — в ближайший простой
            if (_s.SnapshotOnOpen) _idle.Enqueue(new WarmupJob(this, t));
        }

        // ───────────────────────────── 3D-вид изменений ─────────────────────────────

        /// <summary>Вкладку 3D-вида изменений закрыли — вид удалён (панель убирает легенду).</summary>
        public event Action<DocumentTracker> ChangesViewClosed;

        private DateTime _lastViewCheck;

        /// <summary>
        /// В простое: удалить свой 3D-вид, оставшийся с прошлого сеанса (когда модель на экране), и удалить вид,
        /// вкладку которого закрыли (не чаще раза в секунду).
        /// </summary>
        private void CheckChangesView(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc == null) return;
            var t = Find(uidoc.Document);
            if (t == null) return;
            if (t.ChangesViewCleanupPending)
            {
                t.ChangesViewCleanupPending = false;
                try
                {
                    var n = ChangesView3D.DeleteLeftovers(uidoc, t, CurrentUser);
                    if (n > 0) Diag.Log($"Changes view leftovers deleted: {n} ({t.Model.Title})");
                }
                catch (Exception ex)
                {
                    Diag.Log("Changes view cleanup failed", ex);
                }
            }
            if (t.ChangesViewId == null) return;
            if ((DateTime.UtcNow - _lastViewCheck).TotalMilliseconds < 1000) return;
            _lastViewCheck = DateTime.UtcNow;
            var id = t.ChangesViewId.Value;
            if (uidoc.GetOpenUIViews().Any(v => v.ViewId.Value == id))
            {
                t.ChangesViewSeenOpen = true;
                return;
            }
            // вид ещё не успел открыться — ждём; если его уже нет (отменили создание) — просто забываем
            if (!t.ChangesViewSeenOpen && uidoc.Document.GetElement(new ElementId(id)) != null) return;
            var error = ChangesView3D.Delete(uidoc, t);
            if (error != null) Diag.Log(error);
            ChangesViewClosed?.Invoke(t);
        }

        /// <summary>Удалить свой 3D-вид изменений по кнопке в панели.</summary>
        internal string CloseChangesView(UIDocument uidoc)
        {
            var t = Find(uidoc.Document);
            if (t == null) return "Модель журнала не открыта.";
            var error = ChangesView3D.Delete(uidoc, t);
            if (error == null) ChangesViewClosed?.Invoke(t);
            return error;
        }

        /// <summary>
        /// Снимки из прошлого сеанса: оставляем те, у которых версия элемента не изменилась,
        /// изменившиеся переснимаем в первую очередь. Обычно это секунда даже на крупной модели.
        /// </summary>
        private void VerifyCachedSnapshots(DocumentTracker t, HashSet<long> keep)
        {
            if (t.Snapshots.ApplyFingerprint(UnitsFingerprint(t.Doc)))
                Diag.Log("Snapshot cache dropped: project units or Revit language differ from last session (" + t.Model.Title + ")");
            if (t.Snapshots.LoadedFromCache == 0) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var doc = t.Doc;
                var changed = t.Snapshots.Verify(id => VersionOf(doc, id), keep);
                Diag.Log($"Snapshot cache: {t.Snapshots.LoadedFromCache} loaded, {changed.Count} changed since last session, verified in {clock.ElapsedMilliseconds} ms ({t.Model.Title})");
                if (changed.Count > 0) _idle.EnqueueFirst(new SnapshotJob(this, t, changed, force: true));
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot cache verification failed", ex);
            }
        }

        /// <summary>
        /// Значения параметров хранятся строками в формате единиц проекта. Поменяли единицы — старые
        /// строки несравнимы с новыми («3000» и «3,000 м»), поэтому снимки переснимаются.
        /// </summary>
        private void CheckUnitsChanged(DocumentTracker t)
        {
            if (!_s.TrackParameters) return;
            var fp = UnitsFingerprint(t.Doc);
            if (!t.Snapshots.FingerprintChanged(fp)) return;
            Diag.Log("Project units changed — parameter snapshots will be retaken (" + t.Model.Title + ")");
            t.Snapshots.Clear();
            t.Snapshots.ApplyFingerprint(fp);
            t.PrioritizedViews.Clear();
            t.WarmupDone = false;
            if (_s.SnapshotOnOpen) _idle.Enqueue(new WarmupJob(this, t));
        }

        internal static string UnitsFingerprint(Document doc)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var units = doc.GetUnits();
                foreach (var spec in new[] { SpecTypeId.Length, SpecTypeId.Area, SpecTypeId.Volume, SpecTypeId.Angle })
                {
                    try
                    {
                        var fo = units.GetFormatOptions(spec);
                        sb.Append(fo.GetUnitTypeId()?.TypeId).Append('|')
                          .Append(fo.Accuracy.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                        try { sb.Append(fo.GetSymbolTypeId()?.TypeId); } catch { /* без символа */ }
                        sb.Append(';');
                    }
                    catch { sb.Append("?;"); }
                }
                sb.Append(units.DecimalSymbol).Append(';');
            }
            catch { /* ignore */ }
            try { sb.Append(doc.Application.Language); } catch { /* ignore */ }
            return sb.ToString();
        }

        /// <summary>Версия элемента: null — элемента нет, Guid.Empty — версия недоступна.</summary>
        internal static Guid? VersionOf(Document doc, long id)
        {
            Element el;
            try { el = doc.GetElement(new ElementId(id)); } catch { return null; }
            if (el == null) return null;
            return VersionOf(el);
        }

        internal static Guid VersionOf(Element el)
        {
            try { return el.VersionGuid; }
            catch { return Guid.Empty; }
        }

        public void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            Guard("DocumentClosing", () =>
            {
                _detached.RemoveAll(d => SameDocument(d, e.Document));
                var t = Find(e.Document);
                if (t == null) return;
                SaveVersionState(t); // документ ещё доступен
                t.ClosingDocumentId = Convert.ToInt64(e.DocumentId);
            });
        }

        /// <summary>Закрытие может быть отменено (другим плагином или пользователем) — завершаем только после успешного закрытия.</summary>
        public void OnDocumentClosed(object sender, DocumentClosedEventArgs e)
        {
            Guard("DocumentClosed", () =>
            {
                var id = Convert.ToInt64(e.DocumentId);
                var t = _docs.FirstOrDefault(x => x.ClosingDocumentId == id);
                if (t == null) return;
                if (e.Status != RevitAPIEventStatus.Succeeded)
                {
                    t.ClosingDocumentId = null; // закрытие отменено — продолжаем отслеживать
                    return;
                }
                WriteEvent(t, CloseEventName(t), ModelEventType.Close);
                t.IsClosed = true;
                _docs.Remove(t);
                t.SaveInfoCache(); // что за элементы в модели — для следующего открытия
                t.Dispose(); // удалить временный файл снимков
                if (ReferenceEquals(Active, t)) SetActive(null); // следующий активный документ придёт через ViewActivated
            });
        }

        public void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
        {
            Guard("DocumentSaved", () =>
            {
                if (e.Status != RevitAPIEventStatus.Succeeded) return;
                var t = GetOrCreate(e.Document);
                if (t == null) return;
                OnSavedCommon(t);
                SaveVersionState(t);
                MarkSnapshotsSaved(t);
            });
        }

        public void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs e)
        {
            Guard("DocumentSavedAs", () =>
            {
                if (e.Status != RevitAPIEventStatus.Succeeded) return;
                var fromDetached = IsKnownDetached(e.Document);
                var t = GetOrCreate(e.Document);
                if (t == null) return;
                t.Model = ModelIdentity.From(e.Document);
                try { t.LocalPath = e.Document.PathName; } catch { /* ignore */ }
                if (fromDetached)
                {
                    // граница журнала: отсоединённую копию журнал не вёл — что было с моделью до этого, он не знает
                    _detached.RemoveAll(d => SameDocument(d, e.Document));
                    WriteEvent(t, t.Model.IsWorkshared
                        ? "Хранилище создано из отсоединённой копии: что менялось в копии до этого, журнал не знает"
                        : "Модель сохранена из отсоединённой копии: что менялось в копии до этого, журнал не знает",
                        ModelEventType.FromDetached);
                }
                OnSavedCommon(t, "Сохранение как");
                MarkSnapshotsSaved(t);
                if (ReferenceEquals(Active, t)) ActiveModelChanged?.Invoke(t);
                else SetActive(t);
            });
        }

        public void OnSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            Guard("Synchronizing", () => BeginRemote(Find(e.Document)));
        }

        public void OnSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            Guard("Synchronized", () =>
                EndRemote(Find(e.Document), e.Status == RevitAPIEventStatus.Succeeded, ChangeSource.Sync,
                    "Синхронизация с файлом-хранилищем"));
        }

        public void OnReloadingLatest(object sender, DocumentReloadingLatestEventArgs e)
        {
            Guard("ReloadingLatest", () => BeginRemote(Find(e.Document)));
        }

        public void OnReloadedLatest(object sender, DocumentReloadedLatestEventArgs e)
        {
            Guard("ReloadedLatest", () =>
                EndRemote(Find(e.Document), e.Status == RevitAPIEventStatus.Succeeded, ChangeSource.Reload,
                    "Обновление до последней версии"));
        }

        private void BeginRemote(DocumentTracker t)
        {
            if (t == null) return;
            if (t.SyncDepth == 0 || t.VersionBeforeSync == null) t.VersionBeforeSync = CurrentVersion(t.Doc);
            t.SyncDepth++;
        }

        private void EndRemote(DocumentTracker t, bool succeeded, ChangeSource source, string eventName)
        {
            if (t == null) return;
            t.SyncDepth = Math.Max(0, t.SyncDepth - 1);
            if (!succeeded)
            {
                if (t.SyncDepth == 0) t.VersionBeforeSync = null;
                return;
            }
            t.CentralUpToDateAt = DateTimeOffset.Now;
            if (source == ChangeSource.Sync)
            {
                // всё несинхронизированное (и этого сеанса, и перенесённое из прошлых) теперь в хранилище
                WriteEvent(t, eventName, ModelEventType.Commit, t.CarriedSessions.Count > 0 ? new List<string>(t.CarriedSessions) : null,
                    storageVersion: StorageVersionOf(t));
                t.CarriedSessions.Clear();
                t.UncommittedRecords = 0;
                LocalState.SetPending(t.LocalPath, null);
            }
            else
            {
                WriteEvent(t, eventName, ModelEventType.Reload, storageVersion: StorageVersionOf(t));
            }
            ProcessRemote(t, source);
            if (source == ChangeSource.Sync)
            {
                t.LiveChangedSinceSync.Clear();
                t.LiveDeletedSinceSync.Clear();
                MarkSnapshotsSaved(t); // синхронизация сохраняет и локальную копию
            }
            SaveVersionState(t);
        }

        /// <summary>Правки сохранены в файл — снимки после них пригодятся в следующем сеансе.</summary>
        private static void MarkSnapshotsSaved(DocumentTracker t)
        {
            try
            {
                var doc = t.Doc;
                t.Snapshots.MarkSaved(id => VersionOf(doc, id));
                t.UnsavedIds.Clear();
            }
            catch (Exception ex)
            {
                Diag.Log("MarkSaved failed", ex);
            }
        }

        private static Guid? CurrentVersion(Document doc)
        {
            try
            {
                using (var v = Document.GetDocumentVersion(doc))
                {
                    return v?.VersionGUID;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Версия модели в прошлом сеансе (записанная и под прежним ключом модели).</summary>
        private static string LastVersion(ModelIdentity model)
        {
            var v = LocalState.GetLastVersion(model.Key);
            for (int i = 0; v == null && i < model.LegacyKeys.Count; i++) v = LocalState.GetLastVersion(model.LegacyKeys[i]);
            return v;
        }

        private static void SaveVersionState(DocumentTracker t)
        {
            try
            {
                if (!t.Model.IsWorkshared) return;
                // «изменения с прошлого сеанса» ещё не разобраны — не затираем прошлую версию
                if (t.PendingOpenVersion.HasValue) return;
                var v = CurrentVersion(t.Doc);
                if (v.HasValue) LocalState.SetLastVersion(t.Model.Key, v.Value.ToString());
            }
            catch { /* ignore */ }
        }

        /// <summary>Главное событие: транзакция зафиксирована / отменена / повторена.</summary>
        public void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            Guard("DocumentChanged", () =>
            {
                var doc = e.GetDocument();
                var t = GetOrCreate(doc);
                if (t == null) return;
                if (t.SyncDepth > 0) return; // изменения от других пользователей разберём после синхронизации

                var op = MapOperation(e.Operation);
                if (op == OperationKind.None) return;

                IList<string> names = null;
                try { names = e.GetTransactionNames(); } catch { /* ignore */ }
                if (ChangesView3D.IsOwnTransaction(names)) return; // 3D-вид изменений — действия самого плагина, не журнал
                var tx = names == null ? "" : string.Join("; ", names);

                var added = e.GetAddedElementIds();
                var modified = e.GetModifiedElementIds();
                var deleted = e.GetDeletedElementIds();
                // всё, что изменено и ещё не сохранено, — снимки этих элементов не пойдут в кэш следующего сеанса
                foreach (var id in added) t.UnsavedIds.Add(id.Value);
                foreach (var id in modified) t.UnsavedIds.Add(id.Value);
                foreach (var id in deleted) t.UnsavedIds.Add(id.Value);
                var batch = new RecordBatch(this, t, ChangeSource.Live, op, tx, CurrentUser,
                    added.Count + modified.Count + deleted.Count);
                batch.PrepareCascade(new HashSet<long>(deleted.Select(x => x.Value)));
                foreach (var id in added) SafeAdd(batch, ChangeKind.Created, id);
                foreach (var id in modified) SafeAdd(batch, ChangeKind.Modified, id);
                foreach (var id in deleted) SafeAdd(batch, ChangeKind.Deleted, id);
                batch.Commit();
                CheckUnitsChanged(t);
            });
        }

        private static void SafeAdd(RecordBatch batch, ChangeKind kind, ElementId id)
        {
            try
            {
                batch.AddLive(kind, id);
            }
            catch (Exception ex)
            {
                Diag.Log("AddLive failed: " + kind + " " + id?.Value, ex); // один «плохой» элемент не должен терять всю транзакцию
            }
        }

        private static OperationKind MapOperation(UndoOperation op)
        {
            switch (op)
            {
                case UndoOperation.TransactionCommitted: return OperationKind.Commit;
                case UndoOperation.TransactionUndone: return OperationKind.Undo;
                case UndoOperation.TransactionRedone: return OperationKind.Redo;
                case UndoOperation.TransactionGroupRolledBack: return OperationKind.Rollback;
                default: return OperationKind.None; // откат одиночной транзакции ничего не меняет
            }
        }

        public void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            Guard("ViewActivated", () =>
            {
                var t = GetOrCreate(e.Document);
                var note = t == null ? NoteFor(e.Document) : null;
                if (!string.Equals(note, UntrackedNote, StringComparison.Ordinal))
                {
                    UntrackedNote = note;
                    if (ReferenceEquals(Active, t)) ActiveModelChanged?.Invoke(t); // надпись в журнале сменилась
                }
                SetActive(t);
                if (t == null) return;
                if (t.PendingActivation) OnFirstActivation(t);
                if (!_s.TrackParameters) return;
                var view = e.CurrentActiveView;
                if (view == null) return;
                EnsureSnapshot(t, view);
                PrioritizeView(t, view);
            });
        }

        public void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Guard("SelectionChanged", () =>
            {
                var doc = e.GetDocument();
                var t = GetOrCreate(doc);
                if (t == null) { SelectionCardChanged?.Invoke(null); return; }

                var ids = e.GetSelectedElements().ToList();
                if (ids.Count == 0) { SelectionCardChanged?.Invoke(null); return; }

                if (_s.TrackParameters && _s.MaxSelectionSnapshot > 0)
                {
                    var toSnap = new List<long>();
                    foreach (var id in ids.Take(_s.MaxSelectionSnapshot))
                    {
                        toSnap.Add(id.Value);
                        try
                        {
                            var el = doc.GetElement(id);
                            var typeId = el?.GetTypeId();
                            if (ElementDescriber.IsValid(typeId)) toSnap.Add(typeId.Value);
                        }
                        catch { /* ignore */ }
                    }
                    var immediate = toSnap.Where(x => !t.Snapshots.Contains(x)).Distinct().ToList();
                    foreach (var id in immediate.Take(20))
                    {
                        var el = doc.GetElement(new ElementId(id));
                        if (el != null) EnsureSnapshot(t, el);
                    }
                    if (immediate.Count > 20) _idle.EnqueueFirst(new SnapshotJob(this, t, immediate.Skip(20).ToList()));
                }

                RememberSelection(t, doc, ids);
                SelectionCardChanged?.Invoke(BuildCard(t, ids));
            });
        }

        /// <summary>Сколько элементов описывать за одно выделение: выделение не должно тормозить Revit.</summary>
        private const int SelectionDescribeBudget = 3000;

        /// <summary>
        /// Выделенное, скорее всего, сейчас удалят: сведения о нём и обо всём, что от него зависит (марки, размеры, проёмы,
        /// вложенные), запоминаются сразу, не дожидаясь подготовки модели, — иначе удалённое сразу после открытия было бы
        /// «нет сведений». Зависимость «зависимый → выделенный» — для «удалён вместе с …». У типов, уровней и видов
        /// зависимых слишком много (все экземпляры, всё на уровне) — их связь и так известна по сведениям элементов.
        /// </summary>
        private void RememberSelection(DocumentTracker t, Document doc, List<ElementId> ids)
        {
            var budget = SelectionDescribeBudget;
            foreach (var id in ids.Take(Math.Max(1, _s.MaxSelectionSnapshot)))
            {
                Element el;
                try { el = doc.GetElement(id); } catch { continue; }
                if (el == null) continue;
                if (budget-- > 0)
                {
                    var info = ElementDescriber.Describe(doc, el, t);
                    if (t.Info.TryGetValue(info.Id, out var prev)) info.Creator = prev.Creator;
                    t.Info[info.Id] = info;
                }
                if (el is ElementType || el is Level || el is View || el is Family || el is Material) continue;
                ICollection<ElementId> dependents;
                try { dependents = el.GetDependentElements(null); } catch { continue; }
                foreach (var d in dependents)
                {
                    if (d.Value == id.Value) continue;
                    t.DependsOn[d.Value] = id.Value;
                    if (t.Info.ContainsKey(d.Value) || budget <= 0) continue;
                    try
                    {
                        var de = doc.GetElement(d);
                        if (de == null) continue;
                        budget--;
                        var info = ElementDescriber.Describe(doc, de, t, compact: true);
                        if (!info.IsNoise || _s.RecordNoise) t.Info[d.Value] = info;
                    }
                    catch { /* зависимый не описать — у него просто не будет сведений */ }
                }
            }
        }

        private SelectionCard BuildCard(DocumentTracker t, List<ElementId> ids)
        {
            var card = new SelectionCard
            {
                ModelKey = t.Model.Key,
                Count = ids.Count,
                Ids = ids.Take(5000).Select(x => x.Value).ToList(),
                Id = ids[0].Value,
                IsWorkshared = t.Model.IsWorkshared
            };
            var el = t.Doc.GetElement(ids[0]);
            if (el != null)
            {
                var info = ElementDescriber.Describe(t.Doc, el, t);
                // Describe создателя не знает — сохраняем известного (например, кто создал элемент в этом сеансе)
                if (t.Info.TryGetValue(info.Id, out var prev)) info.Creator = prev.Creator;
                card.Name = info.Name;
                card.Category = info.Category;
                card.TypeName = info.TypeName;
                card.GroupName = ElementDescriber.GroupDisplayName(info.Group);
                if (t.Model.IsWorkshared)
                {
                    var tip = Tooltip(t.Doc, el.Id);
                    card.Creator = tip.Item1;
                    card.LastChangedBy = tip.Item2;
                    card.Owner = tip.Item3;
                    if (string.IsNullOrEmpty(info.Creator)) info.Creator = tip.Item1;
                }
                t.Info[info.Id] = info;
            }
            return card;
        }

        /// <summary>Создатель, последним синхронизировал, кто занял сейчас.</summary>
        public static Tuple<string, string, string> Tooltip(Document doc, ElementId id)
        {
            try
            {
                using (var tip = WorksharingUtils.GetWorksharingTooltipInfo(doc, id))
                {
                    return Tuple.Create(tip.Creator ?? "", tip.LastChangedBy ?? "", tip.Owner ?? "");
                }
            }
            catch
            {
                return Tuple.Create("", "", "");
            }
        }

        // ───────────────────────────── вспомогательное ─────────────────────────────

        /// <summary>Снять параметры, если снимка ещё нет.</summary>
        internal void EnsureSnapshot(DocumentTracker t, Element el)
        {
            if (!t.Snapshots.CheckBeforeUse(el.Id.Value, () => VersionOf(el))) CaptureSnapshot(t, el);
        }

        /// <summary>Снять (или переснять) параметры элемента.</summary>
        /// <param name="dirty">Снимок после правки, ещё не сохранённой в файл модели.</param>
        internal void CaptureSnapshot(DocumentTracker t, Element el, bool dirty = false)
        {
            try
            {
                var id = el.Id.Value;
                var typeName = t.Info.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.TypeName)
                    ? info.TypeName
                    : ElementDescriber.GetTypeName(t.Doc, el);
                StoreSnapshot(t, el, ParameterSnapshot.Capture(t.Doc, el, typeName, t), dirty);
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot failed", ex);
            }
        }

        /// <summary>Положить снимок в хранилище вместе с версией элемента.</summary>
        internal void StoreSnapshot(DocumentTracker t, Element el, ParameterSnapshot snapshot, bool dirty)
        {
            ChangeStamp++;
            var id = el.Id.Value;
            t.Snapshots.Set(id, snapshot, VersionOf(el), dirty || t.UnsavedIds.Contains(id));
        }

        /// <summary>Переснять параметры в фоне (после массовой операции).</summary>
        internal void EnqueueRecapture(DocumentTracker t, List<long> ids, bool dirty)
        {
            // в начало очереди: «было» для следующей правки должно появиться раньше полного снимка модели
            if (ids.Count > 0) _idle.EnqueueFirst(new SnapshotJob(this, t, ids, force: true, dirty: dirty));
        }

        private ViewPriorityJob _viewJob;

        /// <summary>Элементы открытого вида снимаются раньше остальных — их скорее всего и будут править.</summary>
        private void PrioritizeView(DocumentTracker t, View view)
        {
            if (!_s.SnapshotAllElements || view.IsTemplate || t.WarmupDone) return;
            // сбор видимых элементов может заставить Revit перестроить графику вида — делаем один раз на вид
            if (!t.PrioritizedViews.Add(view.Id.Value)) return;
            _viewJob?.Cancel();
            _viewJob = new ViewPriorityJob(this, t, view.Id.Value);
            _idle.EnqueueFirst(_viewJob);
        }

        // ───────────────────────────── параметры для панели ─────────────────────────────

        private DocumentTracker ByKey(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return null;
            if (Active != null && string.Equals(Active.Model.Key, modelKey, StringComparison.OrdinalIgnoreCase)) return Active;
            return _docs.FirstOrDefault(d => string.Equals(d.Model.Key, modelKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Последнее известное значение параметра элемента (в том числе удалённого в этом сеансе).</summary>
        public string LookupParam(string modelKey, long id, string paramName)
        {
            var t = ByKey(modelKey);
            if (t == null || !t.Snapshots.TryGet(id, out var snap)) return null;
            return snap.ValueByName(paramName);
        }

        /// <summary>Все последние известные параметры элемента.</summary>
        public List<ParamValue> LookupState(string modelKey, long id)
        {
            var t = ByKey(modelKey);
            if (t == null || !t.Snapshots.TryGet(id, out var snap)) return null;
            return snap.ToState();
        }

        // ───────────────────────────── актуализация журнала ─────────────────────────────

        /// <summary>Запустить проверку записей журнала по текущей модели (только из главного потока).</summary>
        /// <returns>false — модель не открыта.</returns>
        public bool StartActualize(string modelKey, List<ElementCheck> checks, AuditContext ctx, System.Threading.CancellationToken cancel,
            Action<int, int> progress, Action<List<AuditFinding>> done)
        {
            var t = ByKey(modelKey);
            if (t == null) return false;
            _idle.EnqueueFirst(new ActualizeJob(this, t, checks, ctx, cancel, progress, done));
            return true;
        }

        /// <summary>
        /// Начать отслеживать изменения на время актуализации: элементы, изменённые после чтения журнала,
        /// не проверяются и не убираются (их записи могли ещё не дойти до журнала).
        /// Возвращает момент, до которого модель содержит всё из хранилища (null — неизвестно).
        /// </summary>
        public bool BeginAuditWatch(string modelKey, out DateTimeOffset? centralUpToDate)
        {
            centralUpToDate = null;
            var t = ByKey(modelKey);
            if (t == null) return false;
            t.AuditWatch = new HashSet<long>();
            t.AuditWatchMass = false;
            // одиночный файл открыт только у одного человека — в нём всё, что сохранили до этого
            centralUpToDate = t.Model.IsWorkshared ? t.CentralUpToDateAt : DateTimeOffset.Now;
            return true;
        }

        /// <summary>Закончить отслеживание: какие элементы менялись, пока шла актуализация.</summary>
        public HashSet<long> EndAuditWatch(string modelKey, out bool mass)
        {
            mass = false;
            var t = ByKey(modelKey);
            if (t?.AuditWatch == null) return null;
            var ids = t.AuditWatch;
            mass = t.AuditWatchMass;
            t.AuditWatch = null;
            t.AuditWatchMass = false;
            return ids;
        }

        /// <summary>Убрать записи из журнала: в журнал пишется отметка, которую учитывают панели всех пользователей.</summary>
        public bool WriteExclusion(string modelKey, IReadOnlyCollection<string> recordIds) =>
            WriteExcludeEvent(modelKey, recordIds, ModelEventType.Exclude,
                n => $"Актуализация журнала: убрано записей, не соответствующих модели — {n}");

        /// <summary>Вернуть убранные при актуализации записи — у всех пользователей (отметка в журнале).</summary>
        public bool WriteRestore(string modelKey, IReadOnlyCollection<string> recordIds) =>
            WriteExcludeEvent(modelKey, recordIds, ModelEventType.Restore,
                n => $"Актуализация журнала: возвращено в журнал записей — {n}");

        private bool WriteExcludeEvent(string modelKey, IReadOnlyCollection<string> recordIds, ModelEventType type, Func<int, string> name)
        {
            var t = ByKey(modelKey);
            if (t == null || recordIds.Count == 0) return false;
            var ids = recordIds.ToList();
            for (int i = 0; i < ids.Count; i += 2000)
            {
                var chunk = ids.Skip(i).Take(2000).ToList();
                var r = NewRecord(ChangeKind.Event, ChangeSource.Live, CurrentUser, OperationKind.None, "", null);
                r.ElementId = -1;
                r.Name = name(ids.Count);
                r.Group = ObjectGroup.Unknown;
                r.Session = t.SessionId;
                r.EventType = type;
                r.Excludes = chunk;
                _store.Append(t.Model, r);
            }
            Diag.Log($"Journal actualized ({type}): {ids.Count} records ({t.Model.Title}) by {CurrentUser}");
            return true;
        }

        /// <summary>Имена всех параметров, встреченных у элементов модели.</summary>
        public List<string> ParamNames(string modelKey)
        {
            var t = ByKey(modelKey);
            return t == null ? new List<string>() : t.Snapshots.ParamNames.ToList();
        }

        /// <summary>Версия хранилища после синхронизации — в событие (ChangeRecord.StorageVersion); не совместная работа — нет.</summary>
        private static string StorageVersionOf(DocumentTracker t) => t.Model.IsWorkshared ? DocVersionString(t.Doc) : null;

        private void WriteEvent(DocumentTracker t, string name, ModelEventType type, List<string> sessions = null, string storageVersion = null)
        {
            var r = NewRecord(ChangeKind.Event, ChangeSource.Live, CurrentUser, OperationKind.None, "", null);
            r.ElementId = -1;
            r.Name = name;
            r.Group = ObjectGroup.Unknown;
            r.Session = t.SessionId;
            r.EventType = type;
            r.Sessions = sessions;
            r.StorageVersion = storageVersion;
            _store.Append(t.Model, r);
            // период «С моей последней синхронизации»: время запоминается здесь, чтобы не искать его по журналу
            if (type == ModelEventType.Commit) LocalState.SetLastCommit(t.Model.Key, r.Time);
        }

        // ───────────────────── что попало в хранилище, а что нет ─────────────────────

        /// <summary>Сохранение: для одиночного файла это и есть «в хранилище», для локальной копии — ещё нет.</summary>
        private void OnSavedCommon(DocumentTracker t, string name = null)
        {
            if (t.Model.IsWorkshared)
            {
                WriteEvent(t, name ?? "Сохранение локальной копии", ModelEventType.SaveLocal);
                UpdatePendingLocal(t);
            }
            else
            {
                WriteEvent(t, name ?? "Сохранение", ModelEventType.Commit);
                t.UncommittedRecords = 0;
            }
        }

        /// <summary>
        /// Локальная копия сохранена с несинхронизированными изменениями — запомним это, чтобы при следующем
        /// открытии той же копии понять, дошли ли они до хранилища.
        /// </summary>
        private static void UpdatePendingLocal(DocumentTracker t)
        {
            var sessions = new List<string>(t.CarriedSessions);
            if (t.UncommittedRecords > 0 && !sessions.Contains(t.SessionId)) sessions.Add(t.SessionId);
            if (sessions.Count == 0)
            {
                LocalState.SetPending(t.LocalPath, null);
                return;
            }
            LocalState.SetPending(t.LocalPath, new LocalState.PendingLocal
            {
                Version = DocVersionString(t.Doc),
                Sessions = sessions,
                ModelKey = t.Model.Key,
                SavedUtc = DateTime.UtcNow
            });
        }

        /// <summary>При открытии: та ли это локальная копия, в которой остались несинхронизированные изменения?</summary>
        private void CheckPendingLocal(DocumentTracker t)
        {
            if (!t.Model.IsWorkshared) return;
            var entry = LocalState.GetPending(t.LocalPath);
            if (entry == null || entry.Sessions == null || entry.Sessions.Count == 0) return;
            if (!t.Model.HasKey(entry.ModelKey))
            {
                LocalState.SetPending(t.LocalPath, null);
                return;
            }
            var now = DocVersionString(t.Doc);
            if (now != null && now == entry.Version)
            {
                t.CarriedSessions.AddRange(entry.Sessions);
                WriteEvent(t, "Продолжение работы в той же локальной копии: несинхронизированные изменения прошлых сеансов на месте",
                    ModelEventType.Resume, new List<string>(entry.Sessions));
            }
            else
            {
                WriteEvent(t, "Локальная копия заменена: несинхронизированные изменения прошлых сеансов не попали в хранилище",
                    ModelEventType.Lost, new List<string>(entry.Sessions));
                LocalState.SetPending(t.LocalPath, null);
            }
        }

        /// <summary>
        /// Открыта другая локальная копия той же модели (например, новая — со штампом даты и времени при каждом открытии):
        /// несинхронизированное, оставшееся в прежних копиях на этом компьютере, считается брошенным — «✗ не попало
        /// в хранилище». Иначе оно навсегда осталось бы «только в локальной копии».
        /// </summary>
        private void AbandonOtherLocalCopies(DocumentTracker t)
        {
            if (!t.Model.IsWorkshared || string.IsNullOrEmpty(t.LocalPath)) return;
            foreach (var other in LocalState.PendingOf(t.Model.HasKey, t.LocalPath))
            {
                if (other.Value.Sessions == null || other.Value.Sessions.Count == 0) continue;
                WriteEvent(t, "Открыта другая локальная копия: несинхронизированные изменения в прежней копии (" +
                              System.IO.Path.GetFileName(other.Key) + ") не попали в хранилище",
                    ModelEventType.Lost, new List<string>(other.Value.Sessions));
                LocalState.SetPending(other.Key, null);
            }
        }

        private static string CloseEventName(DocumentTracker t)
        {
            if (t.Model.IsWorkshared)
            {
                if (t.UncommittedRecords > 0)
                    return $"Закрытие без синхронизации: изменений не попало в хранилище — {t.UncommittedRecords}";
                if (t.CarriedSessions.Count > 0)
                    return "Закрытие без синхронизации: изменения прошлых сеансов по-прежнему только в локальной копии";
            }
            else if (t.UncommittedRecords > 0)
            {
                return $"Закрытие без сохранения: изменений не сохранено — {t.UncommittedRecords}";
            }
            return "Закрытие модели";
        }

        private static string DocVersionString(Document doc)
        {
            try
            {
                using (var v = Document.GetDocumentVersion(doc))
                {
                    return v == null ? null : v.VersionGUID.ToString("N") + ":" + v.NumberOfSaves;
                }
            }
            catch
            {
                return null;
            }
        }

        internal ChangeRecord NewRecord(ChangeKind kind, ChangeSource source, string user, OperationKind op, string tx, string batch)
        {
            return new ChangeRecord
            {
                RecordId = LogStore.NewRecordId(),
                Time = DateTimeOffset.Now,
                User = user ?? "",
                Machine = Machine,
                WindowsAccount = WindowsUser.Account,
                FullName = WindowsUser.FullName,
                Kind = kind,
                Source = source,
                Operation = op,
                Transaction = tx,
                Batch = batch
            };
        }

        /// <summary>Разобрать изменения, пришедшие из хранилища (при синхронизации / обновлении / открытии).</summary>
        /// <returns>id созданных и изменённых элементов (null — разбора нет).</returns>
        private HashSet<long> ProcessRemote(DocumentTracker t, ChangeSource source)
        {
            var baseGuid = t.VersionBeforeSync;
            t.VersionBeforeSync = null;
            if (baseGuid == null || !_s.DetectRemoteChanges || !t.Model.IsWorkshared) return null;

            var doc = t.Doc;
            List<long> created, modified, deleted;
            try
            {
                if (!Document.IsValidVersionGUID(doc, baseGuid.Value)) return null;
                using (var diff = doc.GetChangedElements(baseGuid.Value))
                {
                    created = diff.GetCreatedElementIds().Select(x => x.Value).ToList();
                    modified = diff.GetModifiedElementIds().Select(x => x.Value).ToList();
                    deleted = diff.AreDeletedElementIdsAvailable
                        ? diff.GetDeletedElementIds().Select(x => x.Value).ToList()
                        : new List<long>();
                }
            }
            catch (Exception ex)
            {
                Diag.Log("GetChangedElements failed (" + source + ")", ex);
                return null;
            }
            if (created.Count + modified.Count + deleted.Count == 0) return null;

            _idle.EnqueueFirst(new RemoteDiffJob(this, t, source, created, modified, deleted,
                new HashSet<long>(t.LiveChangedSinceSync), new HashSet<long>(t.LiveDeletedSinceSync)));
            var ids = new HashSet<long>(created);
            ids.UnionWith(modified);
            return ids;
        }

        /// <summary>Растёт при каждом изменении снимков — панель по нему сбрасывает кэш значений параметров.</summary>
        public int ChangeStamp { get; private set; }

        internal void Emit(DocumentTracker t, List<ChangeRecord> records)
        {
            ChangeStamp++;
            foreach (var r in records)
            {
                if (r.Source == ChangeSource.Live && r.Kind != ChangeKind.Event) t.UncommittedRecords++;
                if (t.AuditWatch != null && r.Kind != ChangeKind.Event)
                {
                    if (r.IsSummary) t.AuditWatchMass = true;
                    else if (r.ElementId > 0) t.AuditWatch.Add(r.ElementId);
                }
            }
            if (records.Count > 0) _store.Append(t.Model, records);
        }

        internal void ReportWarmup(DocumentTracker t) => WarmupProgressChanged?.Invoke(t);

        /// <summary>Применить изменённые настройки к открытым документам.</summary>
        public void OnSettingsChanged()
        {
            foreach (var t in _docs) t.Snapshots.Capacity = _s.MaxSnapshotElements;
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Diag.Log("Handler failed: " + what, ex); // никогда не пропускаем исключение в Revit
            }
        }
    }
}
