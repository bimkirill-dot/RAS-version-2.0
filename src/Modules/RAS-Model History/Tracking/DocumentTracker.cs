using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Состояние отслеживания одного открытого документа.</summary>
    public sealed class DocumentTracker : IDisposable
    {
        public DocumentTracker(Document doc, ModelIdentity model, RASv2.ModelHistory.Storage.SpySettings settings)
        {
            Doc = doc;
            Model = model;
            var persistent = settings.PersistSnapshots && settings.TrackParameters;
            Snapshots = new SnapshotStore(model.Key, settings.MaxSnapshotElements, persistent, settings.SnapshotCacheDays);
            _persistentInfo = settings.PersistSnapshots;
            if (_persistentInfo) LoadCachedInfo();
        }

        public Document Doc { get; }

        /// <summary>Сеанс: все записи одного открытия модели на этом компьютере.</summary>
        public string SessionId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        /// <summary>Путь к открытому файлу (для совместной работы — к локальной копии).</summary>
        public string LocalPath { get; set; }
        /// <summary>Сеансы прошлых дней, несинхронизированные изменения которых лежат в этой локальной копии.</summary>
        public List<string> CarriedSessions { get; } = new List<string>();
        /// <summary>Сколько записей журнала ещё не попало в хранилище (сбрасывается синхронизацией / сохранением одиночного файла).</summary>
        public int UncommittedRecords { get; set; }
        public ModelIdentity Model { get; set; }
        public bool IsClosed { get; set; }

        /// <summary>Сведения обо всех известных элементах — чтобы описать удалённые.</summary>
        public Dictionary<long, ElementInfo> Info { get; } = new Dictionary<long, ElementInfo>();

        /// <summary>
        /// Сведения из прошлых сеансов (ElementInfoCache) — запасной источник: элемент удалили раньше, чем подготовка
        /// описала его заново, или он в закрытом рабочем наборе.
        /// </summary>
        public Dictionary<long, ElementInfo> CachedInfo { get; private set; } = new Dictionary<long, ElementInfo>();

        /// <summary>Зависимый → выделенный элемент, от которого он зависит (Element.GetDependentElements при выделении).</summary>
        public Dictionary<long, long> DependsOn { get; } = new Dictionary<long, long>();

        /// <summary>Удалённые в этом сеансе (своими правками и пришедшие от коллег) — в кэш сведений не пишутся.</summary>
        public HashSet<long> DeletedIds { get; } = new HashSet<long>();

        private readonly bool _persistentInfo;

        /// <summary>Что известно об элементе: сведения этого сеанса, затем — из прошлых.</summary>
        public bool TryKnow(long id, out ElementInfo info) => Info.TryGetValue(id, out info) || CachedInfo.TryGetValue(id, out info);

        /// <summary>От чего зависит элемент: из его сведений и из выделения.</summary>
        public IEnumerable<long> ParentsOf(long id)
        {
            if (TryKnow(id, out var info) && info.Parents != null)
                foreach (var p in info.Parents) yield return p;
            if (DependsOn.TryGetValue(id, out var selected)) yield return selected;
        }

        private string InfoCachePath => Path.Combine(DataFolders.LocalDataDir, "Cache", ModelIdentity.Sanitize(Model.Key), "elements.dat");

        private void LoadCachedInfo()
        {
            try { CachedInfo = ElementInfoCache.Load(InfoCachePath); }
            catch (Exception ex)
            {
                Diag.Log("Element info cache is damaged, starting over (" + Model.Title + ")", ex);
                CachedInfo = new Dictionary<long, ElementInfo>();
            }
        }

        /// <summary>
        /// Сохранить сведения для следующего сеанса: этого сеанса — главные, из прошлых — для элементов, которых в этом
        /// сеансе не видели (закрытые рабочие наборы); удалённые — не пишутся.
        /// </summary>
        public void SaveInfoCache()
        {
            if (!_persistentInfo || Info.Count == 0) return;
            try
            {
                var items = Info.Values.Where(i => !DeletedIds.Contains(i.Id)).ToList();
                items.AddRange(CachedInfo.Values.Where(i => !Info.ContainsKey(i.Id) && !DeletedIds.Contains(i.Id)));
                ElementInfoCache.Save(InfoCachePath, items);
            }
            catch (Exception ex)
            {
                Diag.Log("Element info cache save failed (" + Model.Title + ")", ex);
            }
        }

        /// <summary>Снимки параметров элементов (последнее известное состояние) — на диске.</summary>
        public SnapshotStore Snapshots { get; }

        /// <summary>Что изменил текущий пользователь с последней синхронизации.</summary>
        public HashSet<long> LiveChangedSinceSync { get; } = new HashSet<long>();
        public HashSet<long> LiveDeletedSinceSync { get; } = new HashSet<long>();
        /// <summary>Элементы, изменённые в этом сеансе и ещё не сохранённые в файл модели.</summary>
        public HashSet<long> UnsavedIds { get; } = new HashSet<long>();
        /// <summary>Виды, элементы которых уже сняты в первую очередь.</summary>
        public HashSet<long> PrioritizedViews { get; } = new HashSet<long>();

        /// <summary>Когда открытая модель последний раз получила всё из хранилища (синхронизация или обновление в этом сеансе).</summary>
        public DateTimeOffset? CentralUpToDateAt { get; set; }
        /// <summary>Идёт актуализация: элементы, изменённые после начала проверки (их записи в журнале могли устареть).</summary>
        public HashSet<long> AuditWatch { get; set; }
        /// <summary>Во время актуализации была массовая операция без списка элементов.</summary>
        public bool AuditWatchMass { get; set; }

        /// <summary>Версия документа перед синхронизацией / обновлением.</summary>
        public Guid? VersionBeforeSync { get; set; }
        /// <summary>&gt;0 — идёт синхронизация или обновление: изменения приходят от других пользователей.</summary>
        public int SyncDepth { get; set; }

        /// <summary>Документ открыт, но ещё ни разу не активирован (подготовка отложена).</summary>
        public bool PendingActivation { get; set; }
        /// <summary>Версия с прошлого сеанса — разобрать при первой активации.</summary>
        public Guid? PendingOpenVersion { get; set; }
        /// <summary>Идёт закрытие (DocumentClosing получен, DocumentClosed ещё нет).</summary>
        public long? ClosingDocumentId { get; set; }

        /// <summary>3D-вид изменений этого пользователя (null — не построен).</summary>
        public long? ChangesViewId { get; set; }
        /// <summary>Раскрашенные на нём элементы — снять раскраску при повторном показе.</summary>
        public List<long> ChangesViewColored { get; set; } = new List<long>();
        /// <summary>Скрытые на нём неизменённые перекрытия, крыши, потолки — показать при повторном показе.</summary>
        public List<long> ChangesViewHidden { get; set; } = new List<long>();
        /// <summary>Вкладка вида уже была открыта: если её больше нет — закрыли, вид пора удалить.</summary>
        public bool ChangesViewSeenOpen { get; set; }
        /// <summary>Удалить свой 3D-вид, оставшийся с прошлого сеанса, — когда модель будет на экране (в простое).</summary>
        public bool ChangesViewCleanupPending { get; set; }

        /// <summary>Пул строк: одинаковые имена (типы, параметры) хранятся один раз.</summary>
        private readonly Dictionary<string, string> _pool = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Pool(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (_pool.TryGetValue(s, out var existing)) return existing;
            if (_pool.Count < 500000) _pool[s] = s;
            return s;
        }

        private List<Category> _categories;

        /// <summary>Категории верхнего уровня (кэш для снимков «Видимость/Графика»).</summary>
        public List<Category> Categories
        {
            get
            {
                if (_categories != null) return _categories;
                var list = new List<Category>();
                try
                {
                    foreach (Category c in Doc.Settings.Categories)
                    {
                        if (c != null) list.Add(c);
                    }
                }
                catch { /* ignore */ }
                _categories = list;
                return list;
            }
        }

        public bool WarmupDone { get; set; }
        /// <summary>1 — сведения об элементах, 2 — параметры элементов.</summary>
        public int WarmupPhase { get; set; }
        public int WarmupProgress { get; set; }
        public int WarmupTotal { get; set; }

        public void Dispose() => Snapshots.Dispose();

        public bool Matches(Document doc)
        {
            if (doc == null || IsClosed) return false;
            if (ReferenceEquals(Doc, doc)) return true;
            try
            {
                return Doc.IsValidObject && doc.IsValidObject && Doc.Equals(doc);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Карточка выделенного в Revit элемента (для панели).</summary>
    public sealed class SelectionCard
    {
        public string ModelKey { get; set; }
        public int Count { get; set; }
        public List<long> Ids { get; set; } = new List<long>();
        public long Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string GroupName { get; set; }
        public string Creator { get; set; }
        public string LastChangedBy { get; set; }
        public string Owner { get; set; }
        public bool IsWorkshared { get; set; }
    }
}
