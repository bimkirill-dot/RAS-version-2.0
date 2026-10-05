using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Чтение журналов всех пользователей одной модели. Читает инкрементально:
    /// при повторном вызове обрабатываются только дописанные строки.
    /// Папок журналов может быть несколько (DataFolders.LogFoldersToRead: новая и прежняя), первая — основная:
    /// только её недоступность показывается в панели. Одна и та же запись в разных папках читается один раз.
    /// Не потокобезопасен — вызывайте из одного фонового потока за раз.
    /// </summary>
    public sealed class LogReader
    {
        private readonly string _modelKey;
        private readonly List<string> _keys;
        private readonly string _ownPrefix;
        private readonly IReadOnlyList<string> _roots;
        private bool _extraRootErrorLogged;
        private readonly DateTimeOffset? _since;
        private readonly Dictionary<string, long> _positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pool = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        /// <summary>Живые записи раньше начала периода (файлы месяца читаются целиком) — для «Без дублей».</summary>
        private readonly HashSet<string> _liveBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _liveDeletedBefore = new HashSet<long>();

        /// <param name="roots">Папки журналов; первая — основная (туда пишет LogStore).</param>
        /// <param name="legacyKeys">Прежние ключи модели (ModelIdentity.LegacyKeys): их подпапки читаются вместе с текущей.</param>
        /// <param name="user">Текущий пользователь: свои файлы проверяются при каждом чтении (см. ReadNew).</param>
        public LogReader(IReadOnlyList<string> roots, string modelKey, DateTimeOffset? since, IEnumerable<string> legacyKeys = null, string user = null)
        {
            _roots = roots;
            _modelKey = modelKey;
            _since = since;
            _keys = new List<string> { modelKey };
            if (legacyKeys != null) _keys.AddRange(legacyKeys);
            _ownPrefix = string.IsNullOrEmpty(user) ? null : LogStore.OwnFilePrefix(user);
        }

        public string ModelKey => _modelKey;
        public DateTimeOffset? Since => _since;
        public string LastError { get; private set; }
        /// <summary>Было ли уже хотя бы одно чтение.</summary>
        public bool HasRead { get; private set; }

        /// <summary>
        /// Живые записи («элемент | автор») и удалённые «вживую» элементы раньше начала периода — копии.
        /// Вчерашняя правка автора скрывает её дубль, полученный коллегой при синхронизации сегодня.
        /// </summary>
        public void LiveBefore(out List<string> keys, out List<long> deleted)
        {
            keys = new List<string>(_liveBefore);
            deleted = new List<long>(_liveDeletedBefore);
        }

        /// <summary>
        /// Прочитать новые записи (появившиеся с прошлого вызова). Файл, который не вырос с прошлого чтения,
        /// не открывается: длина берётся из списка файлов папки — один запрос к серверу на папку, а не на каждый файл.
        /// Список файлов сетевой папки Windows кэширует до ~10 секунд, поэтому чужие записи могут прийти чуть позже;
        /// свои файлы и локальный буфер открываются всегда — свои правки видны сразу.
        /// </summary>
        public List<ChangeRecord> ReadNew()
        {
            var result = new List<ChangeRecord>();
            LastError = null;
            HasRead = true;
            foreach (var key in _keys)
            {
                for (int i = 0; i < _roots.Count; i++)
                    ReadDir(Path.Combine(_roots[i], key), local: false, primary: i == 0, result);
                ReadDir(Path.Combine(LogStore.SpoolRoot, key), local: true, primary: true, result);
            }
            return result;
        }

        private void ReadDir(string dir, bool local, bool primary, List<ChangeRecord> result)
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(dir).GetFiles("*.jsonl");
            }
            catch (DirectoryNotFoundException)
            {
                return; // папки нет (прежний ключ модели, пустой буфер) — это нормально
            }
            catch (Exception ex) when (primary)
            {
                LastError = "Не удалось прочитать папку журналов: " + ex.Message;
                return;
            }
            catch (Exception ex)
            {
                // прежняя папка (архив) недоступна — новые записи это не задерживает; в журнал ошибок — один раз
                if (!_extraRootErrorLogged) Diag.Log("Extra log folder unavailable: " + dir, ex);
                _extraRootErrorLogged = true;
                return;
            }
            foreach (var file in files)
            {
                if (!MonthMatches(file.Name)) continue;
                var always = local || (_ownPrefix != null && file.Name.StartsWith(_ownPrefix, StringComparison.OrdinalIgnoreCase));
                if (!always && _positions.TryGetValue(file.FullName, out var pos) && file.Length == pos) continue;
                try { ReadFile(file.FullName, result); }
                catch (Exception ex)
                {
                    Diag.Log("Read failed: " + file.FullName, ex);
                }
            }
        }

        /// <summary>Файлы разбиты по месяцам: ..._2026-09.jsonl. Старые месяцы пропускаем.</summary>
        private bool MonthMatches(string file)
        {
            if (!_since.HasValue) return true;
            var name = Path.GetFileNameWithoutExtension(file);
            var i = name.LastIndexOf('_');
            if (i < 0 || i + 8 > name.Length) return true;
            if (!DateTime.TryParseExact(name.Substring(i + 1), "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var month)) return true;
            // +1 день запаса на разницу часовых поясов
            return month.AddMonths(1).AddDays(1) > _since.Value.LocalDateTime;
        }

        private void ReadFile(string file, List<ChangeRecord> result)
        {
            _positions.TryGetValue(file, out var pos);
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var length = fs.Length;
                if (length < pos) pos = 0; // файл пересоздан
                if (length == pos) return;
                fs.Seek(pos, SeekOrigin.Begin);

                var buffer = new byte[(int)Math.Min(4L * 1024 * 1024, Math.Max(4096L, length - pos))];
                var carry = new MemoryStream();
                long totalRead = pos;
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalRead += read;
                    int start = 0;
                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        string line;
                        if (carry.Length > 0)
                        {
                            carry.Write(buffer, start, i - start);
                            line = Utf8.GetString(carry.GetBuffer(), 0, (int)carry.Length);
                            carry.SetLength(0);
                        }
                        else
                        {
                            line = Utf8.GetString(buffer, start, i - start);
                        }
                        start = i + 1;
                        Parse(line, result);
                    }
                    if (start < read) carry.Write(buffer, start, read - start);
                }
                // незавершённый хвост (строка ещё дописывается) дочитаем в следующий раз
                _positions[file] = totalRead - carry.Length;
            }
        }

        private void Parse(string line, List<ChangeRecord> result)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            ChangeRecord r;
            try
            {
                r = JsonSerializer.Deserialize<ChangeRecord>(line, SpyJson.Line);
            }
            catch
            {
                return; // повреждённая строка — пропускаем
            }
            if (r == null) return;
            if (_since.HasValue && r.Time < _since.Value)
            {
                if (JournalDedup.IsLiveElementRecord(r))
                {
                    _liveBefore.Add(JournalDedup.LiveKey(r));
                    if (r.Kind == ChangeKind.Deleted) _liveDeletedBefore.Add(r.ElementId);
                }
                return;
            }
            var id = r.RecordId ?? (r.Time.UtcTicks + "|" + r.User + "|" + r.ElementId + "|" + r.Kind);
            if (!_seen.Add(id)) return;

            r.User = Pool(r.User);
            r.Machine = Pool(r.Machine);
            r.RecordedBy = Pool(r.RecordedBy);
            r.Category = Pool(r.Category);
            r.ClassName = Pool(r.ClassName);
            r.TypeName = Pool(r.TypeName);
            r.Level = Pool(r.Level);
            r.Workset = Pool(r.Workset);
            r.OwnerView = Pool(r.OwnerView);
            r.Transaction = Pool(r.Transaction);
            r.Creator = Pool(r.Creator);
            r.LastChangedBy = Pool(r.LastChangedBy);
            r.DocTitle = Pool(r.DocTitle);
            if (r.State != null)
            {
                foreach (var p in r.State)
                {
                    p.Name = Pool(p.Name);
                    if (p.Value != null && p.Value.Length <= 64) p.Value = Pool(p.Value);
                }
            }
            if (r.Params != null)
            {
                foreach (var p in r.Params) p.Name = Pool(p.Name);
            }
            result.Add(r);
        }

        private string Pool(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (_pool.TryGetValue(s, out var existing)) return existing;
            _pool[s] = s;
            return s;
        }
    }
}
