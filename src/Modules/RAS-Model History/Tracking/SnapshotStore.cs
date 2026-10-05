using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Хранилище снимков параметров всех элементов модели.
    ///
    /// Снимки лежат в файле на диске (%LocalAppData%\RAS_v_2.0\RAS-Model History\Cache), в памяти — только индекс
    /// «id → позиция в файле», версии элементов и небольшой кэш последних снимков.
    ///
    /// Постоянный режим (по умолчанию): при закрытии модели индекс сохраняется, и при следующем открытии
    /// той же модели (в том числе новой локальной копии того же хранилища) снимки берутся с диска.
    /// Каждый снимок помнит Element.VersionGuid — Revit гарантирует, что одинаковая версия означает
    /// одинаковый элемент, поэтому переснимаются только элементы, изменившиеся с прошлого сеанса.
    ///
    /// Используется только из главного потока Revit.
    /// </summary>
    public sealed class SnapshotStore : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private const int IndexMagic = 0x53505931; // "SPY1"
        /// <summary>2 — в снимках появились габариты элементов без точки вставки и оси: старый кэш готовится заново.</summary>
        private const int IndexFormat = 2;
        private const int DiskHotCapacity = 1000;
        private static long _compactThreshold = 256L * 1024 * 1024;
        private static long _closeCompactThreshold = 64L * 1024 * 1024;

        private readonly Dictionary<long, long> _index = new Dictionary<long, long>();
        private readonly Dictionary<long, int> _sizes = new Dictionary<long, int>();
        /// <summary>Element.VersionGuid на момент снимка (Guid.Empty — неизвестно).</summary>
        private readonly Dictionary<long, Guid> _versions = new Dictionary<long, Guid>();
        /// <summary>Сняты после изменений, которые ещё не сохранены в файл модели.</summary>
        private readonly HashSet<long> _dirty = new HashSet<long>();
        /// <summary>Из прошлого сеанса, элемента при проверке не было (удалён или в закрытом рабочем наборе).</summary>
        private readonly HashSet<long> _unverified = new HashSet<long>();
        /// <summary>Отпечаток единиц/языка, с которыми сняты значения (строки зависят от формата).</summary>
        private string _fingerprint = "";
        private string _loadedFingerprint;
        private bool _needsCompaction;
        private readonly LruCache<long, ParameterSnapshot> _hot;
        private readonly Dictionary<string, int> _keyIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _keys = new List<string>();
        private readonly List<string> _names = new List<string>();
        private readonly HashSet<string> _distinctNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly string _modelKey;
        private readonly string _cacheRoot;
        private FileStream _fs;
        private string _modelDir;
        private bool _persistent;
        private bool _disposed;
        private int _requestedCapacity;
        private long _liveBytes;

        /// <param name="persistent">Сохранять снимки между сеансами.</param>
        /// <param name="cacheDays">Удалять кэши моделей, не открывавшихся дольше этого срока.</param>
        public SnapshotStore(string modelKey, int hotCapacity, bool persistent = false, int cacheDays = 30)
        {
            _requestedCapacity = Math.Max(100, hotCapacity);
            _hot = new LruCache<long, ParameterSnapshot>(_requestedCapacity);
            _modelKey = ModelIdentity.Sanitize(modelKey);
            _cacheRoot = Path.Combine(DataFolders.LocalDataDir, "Cache");
            try
            {
                Directory.CreateDirectory(_cacheRoot);
                CleanupOld(_cacheRoot, cacheDays);
                if (persistent) TryOpenPersistent();
                if (_fs == null) _fs = CreateTempFile();
                _hot.Capacity = Math.Min(_requestedCapacity, DiskHotCapacity);
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot file unavailable, memory-only mode", ex);
                _fs = null;
                _persistent = false;
            }
        }

        /// <summary>Снимки на диске (иначе — только ограниченный кэш в памяти).</summary>
        public bool IsDiskBacked => _fs != null;

        /// <summary>Снимки сохраняются между сеансами.</summary>
        public bool IsPersistent => _persistent && _fs != null;

        /// <summary>Сколько снимков загружено из прошлого сеанса (до проверки).</summary>
        public int LoadedFromCache { get; private set; }

        /// <summary>Размер кэша в памяти (без файла на диске — это и есть предел снимков).</summary>
        public int Capacity
        {
            get => _hot.Capacity;
            set
            {
                _requestedCapacity = Math.Max(100, value);
                _hot.Capacity = IsDiskBacked ? Math.Min(_requestedCapacity, DiskHotCapacity) : _requestedCapacity;
            }
        }

        /// <summary>Сколько элементов «сфотографировано».</summary>
        public int Count => IsDiskBacked ? _index.Count : _hot.Count;

        /// <summary>Имена всех параметров, встреченных у элементов модели.</summary>
        public IReadOnlyCollection<string> ParamNames => _distinctNames;

        public long FileSize
        {
            get
            {
                try { return _fs?.Length ?? 0; } catch { return 0; }
            }
        }

        public bool Contains(long id) => IsDiskBacked ? _index.ContainsKey(id) : _hot.Contains(id);

        public bool TryGet(long id, out ParameterSnapshot snapshot)
        {
            if (_hot.TryGet(id, out snapshot)) return true;
            snapshot = null;
            if (!IsDiskBacked || !_index.TryGetValue(id, out var offset)) return false;
            try
            {
                snapshot = ReadAt(offset);
                if (snapshot == null) return false;
                _hot.Set(id, snapshot);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot read failed", ex);
                return false;
            }
        }

        /// <param name="version">Element.VersionGuid в момент снимка (null — неизвестно).</param>
        /// <param name="dirty">Снимок отражает изменения, ещё не сохранённые в файл модели.</param>
        public void Set(long id, ParameterSnapshot snapshot, Guid? version = null, bool dirty = false)
        {
            if (snapshot == null || _disposed) return;
            _hot.Set(id, snapshot);
            foreach (var kv in snapshot.Values) RegisterKey(kv.Key, kv.Value.Key);
            _versions[id] = version ?? Guid.Empty;
            if (dirty) _dirty.Add(id); else _dirty.Remove(id);
            if (!IsDiskBacked) return;
            try
            {
                var offset = Append(id, snapshot, out var size);
                if (_sizes.TryGetValue(id, out var oldSize)) _liveBytes -= oldSize;
                _index[id] = offset;
                _sizes[id] = size;
                _liveBytes += size;
                var dead = _fs.Length - _liveBytes;
                // сжатие не здесь (может идти внутри правки), а когда пользователь не работает — см. CompactIfNeeded
                if (dead > _compactThreshold && dead > _liveBytes) _needsCompaction = true;
                _unverified.Remove(id);
            }
            catch (Exception ex)
            {
                // например, закончилось место на диске — продолжаем только с кэшем в памяти
                Diag.Log("Snapshot write failed, switching to memory-only mode", ex);
                try { _fs.Dispose(); } catch { /* ignore */ }
                _fs = null;
                _persistent = false;
                _index.Clear();
                _sizes.Clear();
                _liveBytes = 0;
                _hot.Capacity = _requestedCapacity;
            }
        }

        public void Remove(long id)
        {
            _index.Remove(id);
            _versions.Remove(id);
            _dirty.Remove(id);
            _unverified.Remove(id);
            if (_sizes.TryGetValue(id, out var size))
            {
                _liveBytes -= size;
                _sizes.Remove(id);
            }
            _hot.Remove(id);
        }

        /// <summary>
        /// Проверить снимки из прошлого сеанса по текущим версиям элементов.
        /// Снимок изменившегося элемента удаляется (его нужно переснять); снимок отсутствующего
        /// элемента остаётся — это последние известные параметры удалённого элемента.
        /// </summary>
        /// <param name="currentVersion">id → версия элемента; null — элемента в модели нет; Guid.Empty — версия неизвестна.</param>
        /// <param name="keep">id, которые не трогать (их разберёт «изменения с прошлого сеанса»).</param>
        /// <returns>id существующих элементов, которые нужно переснять.</returns>
        public List<long> Verify(Func<long, Guid?> currentVersion, ICollection<long> keep = null)
        {
            var changed = new List<long>();
            foreach (var id in _index.Keys.ToList())
            {
                if (keep != null && keep.Contains(id)) continue;
                var now = currentVersion(id);
                if (now == null)
                {
                    // удалён (или в закрытом рабочем наборе) — оставляем последние известные параметры,
                    // но перед использованием как «было» проверим ещё раз
                    _unverified.Add(id);
                    continue;
                }
                if (now.Value != Guid.Empty && _versions.TryGetValue(id, out var stored) && stored == now.Value) continue;
                Remove(id);
                changed.Add(id);
            }
            return changed;
        }

        /// <summary>
        /// Снимок, не проверенный при открытии (элемента тогда не было), — проверить перед использованием как «было».
        /// </summary>
        /// <returns>true — снимок можно использовать.</returns>
        public bool CheckBeforeUse(long id, Func<Guid?> currentVersion)
        {
            if (!_unverified.Contains(id)) return Contains(id);
            var now = currentVersion();
            if (now == null) return Contains(id); // элемента по-прежнему нет — это последние известные параметры
            if (now.Value != Guid.Empty && _versions.TryGetValue(id, out var stored) && stored == now.Value)
            {
                _unverified.Remove(id);
                return true;
            }
            Remove(id);
            return false;
        }

        /// <summary>
        /// Значения хранятся строками в формате единиц проекта и языка Revit. Если формат другой —
        /// старые строки не сравнимы с новыми, и снимки из прошлого сеанса отбрасываются.
        /// </summary>
        /// <returns>true — снимки сброшены.</returns>
        public bool ApplyFingerprint(string fingerprint)
        {
            fingerprint ??= "";
            var reset = false;
            if (_loadedFingerprint != null && _index.Count > 0 && !string.Equals(_loadedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                Clear();
                reset = true;
            }
            _loadedFingerprint = null;
            _fingerprint = fingerprint;
            return reset;
        }

        /// <summary>Отпечаток формата сменился посреди сеанса (поменяли единицы проекта) — снимки устарели.</summary>
        public bool FingerprintChanged(string fingerprint) =>
            !string.IsNullOrEmpty(_fingerprint) && !string.Equals(_fingerprint, fingerprint ?? "", StringComparison.Ordinal);

        /// <summary>Забыть все снимки (файл обнуляется).</summary>
        public void Clear()
        {
            _hot.Clear();
            _index.Clear();
            _sizes.Clear();
            _versions.Clear();
            _dirty.Clear();
            _unverified.Clear();
            _liveBytes = 0;
            LoadedFromCache = 0;
            try
            {
                if (IsPersistent && File.Exists(IndexPath)) File.Delete(IndexPath);
                _fs?.SetLength(0);
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot clear failed", ex);
            }
        }

        /// <summary>Нужно сжать файл (много устаревших версий снимков).</summary>
        public bool NeedsCompaction => _needsCompaction && IsDiskBacked;

        /// <summary>Сжать файл. Вызывать, когда пользователь не работает с Revit.</summary>
        public void CompactIfNeeded()
        {
            if (!NeedsCompaction) return;
            _needsCompaction = false;
            try
            {
                Compact();
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot compaction failed", ex);
            }
        }

        /// <summary>
        /// Модель сохранена (или синхронизирована): снимки после правок теперь соответствуют файлу —
        /// обновляем их версии, и они пригодятся в следующем сеансе.
        /// </summary>
        public void MarkSaved(Func<long, Guid?> currentVersion)
        {
            foreach (var id in _dirty.ToList())
            {
                var v = currentVersion(id);
                if (v.HasValue) _versions[id] = v.Value;
            }
            _dirty.Clear();
        }

        // ───────────────────────────── файлы ─────────────────────────────
        // данные:  [int32 длина][int64 id][7bit кол-во]{ [7bit id ключа][строка значения] }…
        // индекс:  magic, формат, ключи (ключ, имя), записи (id, смещение, размер, версия)

        private string DataPath => Path.Combine(_modelDir, "snapshots.dat");
        private string IndexPath => Path.Combine(_modelDir, "snapshots.idx");

        private void TryOpenPersistent()
        {
            _modelDir = Path.Combine(_cacheRoot, _modelKey);
            Directory.CreateDirectory(_modelDir);
            try
            {
                _fs = new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                    FileOptions.RandomAccess);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // эта модель уже открыта в другом окне Revit (или нет доступа) — работаем с временным файлом
                _fs = null;
                return;
            }
            _persistent = true;
            try
            {
                bool loaded;
                try
                {
                    loaded = LoadIndex();
                }
                catch (Exception ex)
                {
                    Diag.Log("Snapshot cache index is damaged, starting over", ex);
                    loaded = false;
                }
                if (!loaded)
                {
                    ResetIndex();
                    _fs.SetLength(0); // индекса нет (первое открытие или сбой) — начинаем с чистого файла
                }
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot cache unusable, switching to temp file", ex);
                try { _fs.Dispose(); } catch { /* ignore */ }
                _fs = null;
                _persistent = false;
                ResetIndex();
                return;
            }
            LoadedFromCache = _index.Count;
        }

        private void ResetIndex()
        {
            _index.Clear();
            _sizes.Clear();
            _versions.Clear();
            _dirty.Clear();
            _unverified.Clear();
            _keyIds.Clear();
            _keys.Clear();
            _names.Clear();
            _distinctNames.Clear();
            _liveBytes = 0;
        }

        private bool LoadIndex()
        {
            if (!File.Exists(IndexPath)) return false;
            var dataLength = _fs.Length;
            using (var fs = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var r = new BinaryReader(fs, Utf8))
            {
                if (r.ReadInt32() != IndexMagic || r.ReadInt32() != IndexFormat) return false;
                _loadedFingerprint = r.ReadString();
                var keyCount = r.ReadInt32();
                for (int i = 0; i < keyCount; i++)
                {
                    var key = r.ReadString();
                    var name = r.ReadString();
                    RegisterKey(key, name);
                }
                var count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var id = r.ReadInt64();
                    var offset = r.ReadInt64();
                    var size = r.ReadInt32();
                    var version = new Guid(r.ReadBytes(16));
                    if (offset < 0 || size <= 4 || offset + size > dataLength) return false;
                    _index[id] = offset;
                    _sizes[id] = size;
                    _versions[id] = version;
                    _liveBytes += size;
                }
            }
            return true;
        }

        /// <summary>
        /// Сохранить индекс (без снимков несохранённых правок). Если в файле много устаревших версий,
        /// сжатие выполняется в фоновом потоке — закрытие модели не ждёт.
        /// </summary>
        private void SaveIndex()
        {
            foreach (var id in _dirty.ToList())
            {
                // модель закрыта без сохранения: эти снимки не соответствуют файлу модели
                _index.Remove(id);
                if (_sizes.TryGetValue(id, out var s)) { _liveBytes -= s; _sizes.Remove(id); }
                _versions.Remove(id);
            }
            _dirty.Clear();
            _fs.Flush(true);
            // модель закрыли, ни разу не активировав (открыта в фоне другим плагином): отпечаток не проверялся —
            // оставляем тот, с которым снимки загружены, иначе следующее открытие выбросит весь кэш
            var fingerprint = _loadedFingerprint ?? _fingerprint;

            var dead = _fs.Length - _liveBytes;
            if (dead > _closeCompactThreshold && dead > _liveBytes)
            {
                // снимок состояния для фонового сжатия; хранилище после Dispose больше не используется
                var fs = _fs;
                _fs = null;
                var index = new Dictionary<long, long>(_index);
                var sizes = new Dictionary<long, int>(_sizes);
                var versions = new Dictionary<long, Guid>(_versions);
                var keys = new List<string>(_keys);
                var names = new List<string>(_names);
                var dir = _modelDir;
                CompactOnCloseTask = System.Threading.Tasks.Task.Run(() =>
                    CompactAndWriteIndex(fs, dir, index, sizes, versions, keys, names, fingerprint));
                return;
            }
            WriteIndexFile(IndexPath, _index, _sizes, _versions, _keys, _names, fingerprint);
        }

        /// <summary>Фоновое сжатие при закрытии (для тестов — можно дождаться).</summary>
        public System.Threading.Tasks.Task CompactOnCloseTask { get; private set; }

        private static void CompactAndWriteIndex(FileStream fs, string dir, Dictionary<long, long> index, Dictionary<long, int> sizes,
            Dictionary<long, Guid> versions, List<string> keys, List<string> names, string fingerprint)
        {
            var dataPath = Path.Combine(dir, "snapshots.dat");
            var indexPath = Path.Combine(dir, "snapshots.idx");
            var dataTmp = Path.Combine(dir, "snapshots.new");
            var indexTmp = indexPath + ".new";
            try
            {
                var newIndex = new Dictionary<long, long>(index.Count);
                var buffer = new byte[64 * 1024];
                using (var fresh = new FileStream(dataTmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    foreach (var kv in index.OrderBy(x => x.Value))
                    {
                        var size = sizes[kv.Key];
                        if (buffer.Length < size) buffer = new byte[size];
                        fs.Seek(kv.Value, SeekOrigin.Begin);
                        int read = 0;
                        while (read < size)
                        {
                            var n = fs.Read(buffer, read, size - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        newIndex[kv.Key] = fresh.Position;
                        fresh.Write(buffer, 0, read);
                    }
                    fresh.Flush(true);
                }
                fs.Dispose();
                WriteIndexFile(indexTmp, newIndex, sizes, versions, keys, names, fingerprint, direct: true);
                // порядок важен: без индекса кэш просто не используется, неверный индекс недопустим
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(dataTmp, dataPath, true);
                File.Move(indexTmp, indexPath, true);
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot cache compaction on close failed", ex);
                try { fs.Dispose(); } catch { /* ignore */ }
                try { if (File.Exists(indexPath)) File.Delete(indexPath); } catch { /* ignore */ }
            }
        }

        private static void WriteIndexFile(string path, Dictionary<long, long> index, Dictionary<long, int> sizes,
            Dictionary<long, Guid> versions, List<string> keys, List<string> names, string fingerprint, bool direct = false)
        {
            var tmp = direct ? path : path + ".new";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs, Utf8))
            {
                w.Write(IndexMagic);
                w.Write(IndexFormat);
                w.Write(fingerprint ?? "");
                w.Write(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                {
                    w.Write(keys[i]);
                    w.Write(names[i] ?? "");
                }
                w.Write(index.Count);
                foreach (var kv in index)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value);
                    w.Write(sizes[kv.Key]);
                    versions.TryGetValue(kv.Key, out var v);
                    w.Write(v.ToByteArray());
                }
                w.Flush();
                fs.Flush(true);
            }
            if (!direct) File.Move(tmp, path, true);
        }

        private FileStream CreateTempFile()
        {
            var file = Path.Combine(_cacheRoot, $"{_modelKey}_{Environment.ProcessId}_{Guid.NewGuid():N}.snap");
            return new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        }

        /// <summary>Переписать только актуальные записи в новый файл (старые версии снимков выбрасываются).</summary>
        private void Compact()
        {
            if (IsPersistent)
            {
                var tmp = Path.Combine(_modelDir, "snapshots.new");
                var newIndex = new Dictionary<long, long>(_index.Count);
                try
                {
                    using (var fresh = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        CopyLive(fresh, newIndex);
                    }
                }
                catch
                {
                    try { File.Delete(tmp); } catch { /* ignore */ }
                    throw; // старый файл и индекс не тронуты — работаем дальше с ними
                }
                // смещения меняются — старый индекс на диске становится неверным (новый запишется при закрытии)
                try { if (File.Exists(IndexPath)) File.Delete(IndexPath); } catch { /* ignore */ }
                _fs.Dispose();
                try
                {
                    // замена одним шагом: старый файл не удаляется, пока новый не встал на его место
                    File.Move(tmp, DataPath, overwrite: true);
                }
                catch
                {
                    // файл кратко занят (антивирус, резервное копирование) — остаёмся со старым файлом и старым индексом
                    try { File.Delete(tmp); } catch { /* ignore */ }
                    ReopenAfterFailedSwap();
                    throw;
                }
                // на месте уже новый файл — индекс сразу на его смещения, даже если открыть его сейчас не выйдет
                _index.Clear();
                foreach (var kv in newIndex) _index[kv.Key] = kv.Value;
                try
                {
                    _fs = new FileStream(DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);
                }
                catch
                {
                    ReopenAfterFailedSwap();
                    throw;
                }
            }
            else
            {
                var fresh = CreateTempFile();
                var newIndex = new Dictionary<long, long>(_index.Count);
                try
                {
                    CopyLive(fresh, newIndex);
                }
                catch
                {
                    fresh.Dispose();
                    throw;
                }
                _fs.Dispose();
                _fs = fresh;
                _index.Clear();
                foreach (var kv in newIndex) _index[kv.Key] = kv.Value;
            }
            Diag.Log("Snapshot file compacted: " + (_fs.Length / (1024 * 1024)) + " MB");
        }

        /// <summary>
        /// Сжатие не смогло подменить файл: открыть прежний снова. Если не открывается и он — дальше без кэша
        /// между сеансами (временный файл): «было → стало» для новых правок сохранится, старые снимки переснимутся.
        /// </summary>
        private void ReopenAfterFailedSwap()
        {
            try
            {
                _fs = new FileStream(DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);
                return;
            }
            catch (Exception ex)
            {
                Diag.Log("Snapshot file reopen failed, switching to temp file", ex);
            }
            try { _fs = CreateTempFile(); }
            catch (Exception ex)
            {
                Diag.Log("Snapshot file unavailable, memory-only mode", ex);
                _fs = null;
            }
            _persistent = false;
            Clear();
        }

        private void CopyLive(FileStream fresh, Dictionary<long, long> newIndex)
        {
            var buffer = new byte[64 * 1024];
            foreach (var kv in _index.OrderBy(x => x.Value)) // по порядку в файле — последовательное чтение
            {
                var size = _sizes[kv.Key];
                if (buffer.Length < size) buffer = new byte[size];
                _fs.Seek(kv.Value, SeekOrigin.Begin);
                int read = 0;
                while (read < size)
                {
                    var n = _fs.Read(buffer, read, size - read);
                    if (n <= 0) break;
                    read += n;
                }
                newIndex[kv.Key] = fresh.Position;
                fresh.Write(buffer, 0, read);
            }
        }

        private int RegisterKey(string key, string name)
        {
            if (_keyIds.TryGetValue(key, out var id)) return id;
            id = _keys.Count;
            _keyIds[key] = id;
            _keys.Add(key);
            _names.Add(name);
            if (!string.IsNullOrEmpty(name) && !key.StartsWith("~", StringComparison.Ordinal)) _distinctNames.Add(name);
            return id;
        }

        private long Append(long id, ParameterSnapshot snapshot, out int size)
        {
            byte[] payload;
            using (var ms = new MemoryStream(1024))
            using (var w = new BinaryWriter(ms, Utf8, true))
            {
                w.Write(id);
                w.Write7BitEncodedInt(snapshot.Values.Count);
                foreach (var kv in snapshot.Values)
                {
                    w.Write7BitEncodedInt(RegisterKey(kv.Key, kv.Value.Key));
                    w.Write(kv.Value.Value ?? "");
                }
                w.Flush();
                payload = ms.ToArray();
            }
            var offset = _fs.Seek(0, SeekOrigin.End);
            _fs.Write(BitConverter.GetBytes(payload.Length), 0, 4);
            _fs.Write(payload, 0, payload.Length);
            size = payload.Length + 4;
            return offset;
        }

        private ParameterSnapshot ReadAt(long offset)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            var lenBytes = new byte[4];
            if (_fs.Read(lenBytes, 0, 4) != 4) return null;
            var len = BitConverter.ToInt32(lenBytes, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            var payload = new byte[len];
            int read = 0;
            while (read < len)
            {
                var n = _fs.Read(payload, read, len - read);
                if (n <= 0) return null;
                read += n;
            }
            var snap = new ParameterSnapshot();
            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Utf8))
            {
                r.ReadInt64(); // id
                var count = r.Read7BitEncodedInt();
                for (int i = 0; i < count; i++)
                {
                    var keyId = r.Read7BitEncodedInt();
                    var value = r.ReadString();
                    if (keyId < 0 || keyId >= _keys.Count) continue;
                    snap.Values[_keys[keyId]] = new KeyValuePair<string, string>(_names[keyId], value);
                }
            }
            return snap;
        }

        /// <summary>Удалить временные файлы прошлых сбоев и кэши давно не открывавшихся моделей.</summary>
        private static void CleanupOld(string root, int cacheDays)
        {
            try
            {
                foreach (var f in Directory.GetFiles(root, "*.snap"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); }
                    catch { /* файл занят другим Revit — оставляем */ }
                }
                var limit = DateTime.UtcNow.AddDays(-Math.Max(1, cacheDays));
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        // индекс переписывается при каждом закрытии модели — по нему видно, когда модель открывали
                        var lastUse = Directory.GetLastWriteTimeUtc(dir);
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            var t = File.GetLastWriteTimeUtc(f);
                            if (t > lastUse) lastUse = t;
                        }
                        if (lastUse < limit) Directory.Delete(dir, true);
                    }
                    catch { /* занят — оставляем */ }
                }
            }
            catch { /* ignore */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (IsPersistent)
            {
                try { SaveIndex(); }
                catch (Exception ex)
                {
                    Diag.Log("Snapshot cache save failed", ex);
                    try { if (File.Exists(IndexPath)) File.Delete(IndexPath); } catch { /* ignore */ }
                }
            }
            try { _fs?.Dispose(); } catch { /* ignore */ }
            _fs = null;
            _index.Clear();
            _sizes.Clear();
            _versions.Clear();
            _dirty.Clear();
            _unverified.Clear();
            _hot.Clear();
        }
    }
}
