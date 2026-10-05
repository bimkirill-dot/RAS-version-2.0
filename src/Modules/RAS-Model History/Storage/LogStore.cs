using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RASv2.ModelHistory.Tracking;

namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Запись журнала. Каждый пользователь пишет только в свои файлы:
    ///   &lt;Папка журналов&gt;\&lt;модель&gt;\&lt;пользователь&gt;@&lt;компьютер&gt;_&lt;гггг-ММ&gt;.jsonl
    /// поэтому одновременная работа многих людей с общей сетевой папкой безопасна.
    /// Если сетевая папка недоступна — записи копятся локально (%LocalAppData%\RAS_v_2.0\RAS-Model History\Spool)
    /// и досылаются, когда папка снова доступна. Запись идёт в фоновом потоке.
    /// </summary>
    public sealed class LogStore : IDisposable
    {
        private sealed class Pending
        {
            public ModelIdentity Model;
            public ChangeRecord Record;
            public string Line;
        }

        private readonly ConcurrentQueue<Pending> _queue = new ConcurrentQueue<Pending>();
        private readonly ConcurrentDictionary<string, bool> _descriptorsWritten =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly object _flushLock = new object();
        private readonly Timer _timer;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private volatile bool _disposed;
        private int _busy;

        /// <summary>После сбоя сети не пытаемся писать в сетевую папку каждую секунду.</summary>
        private DateTime _networkRetryAfter = DateTime.MinValue;
        private static readonly TimeSpan NetworkBackoff = TimeSpan.FromSeconds(30);

        /// <summary>Вызывается (в фоновом потоке) после записи новых строк; аргумент — ключ модели.</summary>
        public event Action<string> Flushed;

        /// <summary>Последняя ошибка доступа к папке журналов (для строки состояния).</summary>
        public string LastError { get; private set; }

        public LogStore()
        {
            _timer = new Timer(_ => SafeFlush(), null, 1000, 1000);
        }

        public static string SpoolRoot => Path.Combine(DataFolders.LocalDataDir, "Spool");

        public static string NewRecordId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>Поставить записи в очередь.</summary>
        public void Append(ModelIdentity model, IEnumerable<ChangeRecord> records)
        {
            if (model == null || records == null || _disposed) return;
            foreach (var r in records)
            {
                if (r == null) continue;
                if (string.IsNullOrEmpty(r.RecordId)) r.RecordId = NewRecordId();
                _queue.Enqueue(new Pending { Model = model, Record = r });
            }
        }

        public void Append(ModelIdentity model, ChangeRecord record) => Append(model, new[] { record });

        private void SafeFlush()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return; // предыдущая запись ещё идёт
            try { Flush(); }
            catch (Exception ex) { Diag.Log("Flush failed", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private bool InBackoff => DateTime.UtcNow < _networkRetryAfter;

        /// <summary>Записать накопленное. Безопасно вызывать из любого потока.</summary>
        public void Flush()
        {
            if (!Monitor.TryEnter(_flushLock, 5000)) return;
            try
            {
                var batch = new List<Pending>();
                while (_queue.TryDequeue(out var p)) batch.Add(p);

                var touchedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!InBackoff)
                {
                    try { DeliverSpool(touchedModels); }
                    catch (Exception ex)
                    {
                        Diag.Log("Spool delivery failed", ex);
                        _networkRetryAfter = DateTime.UtcNow + NetworkBackoff;
                    }
                }
                if (batch.Count == 0 && touchedModels.Count == 0) return;

                var root = DataFolders.LogFolder;
                foreach (var group in batch.GroupBy(p => (p.Model.Key, File: FileNameFor(p.Record))))
                {
                    var items = group.ToList();
                    try
                    {
                        foreach (var p in items) p.Line ??= JsonSerializer.Serialize(p.Record, SpyJson.Line);
                        WriteLines(root, items[0].Model, group.Key.File, items.Select(p => p.Line).ToList());
                        touchedModels.Add(group.Key.Key);
                    }
                    catch (Exception ex)
                    {
                        // ничего не теряем: вернём в очередь и попробуем позже
                        Diag.Log("Write failed, re-queued " + items.Count + " records", ex);
                        foreach (var p in items) _queue.Enqueue(p);
                    }
                }

                foreach (var key in touchedModels)
                {
                    try { Flushed?.Invoke(key); } catch (Exception ex) { Diag.Log("Flushed handler failed", ex); }
                }
            }
            finally
            {
                Monitor.Exit(_flushLock);
            }
        }

        private static string FileNameFor(ChangeRecord r)
        {
            var writer = string.IsNullOrEmpty(r.RecordedBy) ? r.User : r.RecordedBy;
            var month = r.Time.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return FilePrefix(writer, r.Machine) + month + ".jsonl";
        }

        private static string FilePrefix(string user, string machine) =>
            $"{ModelIdentity.Sanitize(user)}@{ModelIdentity.Sanitize(machine)}_";

        /// <summary>Начало имени файлов журнала, которые пишет этот пользователь на этом компьютере.</summary>
        public static string OwnFilePrefix(string user) => FilePrefix(user, Environment.MachineName);

        private void WriteLines(string root, ModelIdentity model, string fileName, List<string> lines)
        {
            if (!InBackoff)
            {
                var dir = Path.Combine(root, model.Key);
                try
                {
                    Directory.CreateDirectory(dir);
                    EnsureDescriptor(dir, model);
                    AppendWithRetry(Path.Combine(dir, fileName), lines);
                    LastError = null;
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    LastError = "Папка журналов недоступна, записи временно сохраняются локально: " + ex.Message;
                    Diag.Log("Log folder unavailable, spooling: " + dir, ex);
                    _networkRetryAfter = DateTime.UtcNow + NetworkBackoff; // только после реального сбоя
                }
            }
            SpoolLines(model, fileName, lines);
        }

        private void SpoolLines(ModelIdentity model, string fileName, List<string> lines)
        {
            var spoolDir = Path.Combine(SpoolRoot, model.Key);
            Directory.CreateDirectory(spoolDir);
            AppendWithRetry(Path.Combine(spoolDir, fileName), lines);
            var descriptor = Path.Combine(spoolDir, "model.json");
            if (!File.Exists(descriptor)) File.WriteAllText(descriptor, DescriptorJson(model), Utf8);
        }

        /// <summary>Досылка накопленного локально. Файл сначала переименовывается, чтобы не потерять дописанное параллельно.</summary>
        private void DeliverSpool(HashSet<string> touched)
        {
            if (!Directory.Exists(SpoolRoot)) return;
            var root = DataFolders.LogFolder;
            foreach (var spoolDir in Directory.GetDirectories(SpoolRoot))
            {
                var pending = Directory.GetFiles(spoolDir, "*.jsonl").Concat(Directory.GetFiles(spoolDir, "*.sending")).ToList();
                if (pending.Count == 0) continue;
                var key = Path.GetFileName(spoolDir);
                var targetDir = Path.Combine(root, key);
                try
                {
                    Directory.CreateDirectory(targetDir);
                    var descriptor = Path.Combine(spoolDir, "model.json");
                    var targetDescriptor = Path.Combine(targetDir, "model.json");
                    if (File.Exists(descriptor) && !File.Exists(targetDescriptor)) File.Copy(descriptor, targetDescriptor);

                    foreach (var file in pending)
                    {
                        var sending = file;
                        var targetName = Path.GetFileName(file);
                        if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        {
                            sending = file + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sending";
                            File.Move(file, sending);
                        }
                        else
                        {
                            // имя вида "<файл>.jsonl.<id>.sending"
                            var i = targetName.IndexOf(".jsonl", StringComparison.OrdinalIgnoreCase);
                            targetName = i > 0 ? targetName.Substring(0, i + 6) : targetName;
                        }
                        var lines = File.ReadAllLines(sending, Utf8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                        AppendWithRetry(Path.Combine(targetDir, targetName), lines);
                        File.Delete(sending);
                    }
                    touched.Add(key);
                    LastError = null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _networkRetryAfter = DateTime.UtcNow + NetworkBackoff;
                    return;
                }
            }
        }

        private static void AppendWithRetry(string path, List<string> lines)
        {
            if (lines.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append('\n');
            var bytes = Utf8.GetBytes(sb.ToString());

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }
                    return;
                }
                catch (IOException) when (attempt < 4 && Directory.Exists(Path.GetDirectoryName(path)))
                {
                    Thread.Sleep(150 * (attempt + 1)); // файл кратко занят другим процессом
                }
            }
        }

        private void EnsureDescriptor(string dir, ModelIdentity model)
        {
            if (_descriptorsWritten.ContainsKey(dir)) return;
            var path = Path.Combine(dir, "model.json");
            if (!File.Exists(path)) File.WriteAllText(path, DescriptorJson(model), Utf8);
            _descriptorsWritten[dir] = true;
        }

        private static string DescriptorJson(ModelIdentity model)
        {
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["title"] = model.Title,
                ["path"] = model.Path,
                ["key"] = model.Key
            }, SpyJson.Pretty);
        }

        /// <summary>При закрытии Revit: дописать остаток, но не держать Revit дольше нескольких секунд.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _timer.Dispose(); } catch { /* ignore */ }
            try
            {
                var task = Task.Run(() =>
                {
                    for (int i = 0; i < 50 && Volatile.Read(ref _busy) == 1; i++) Thread.Sleep(100);
                    Flush();
                });
                if (!task.Wait(TimeSpan.FromSeconds(8)))
                    Diag.Log("Final flush timed out (network share is slow or unavailable)");
            }
            catch (Exception ex)
            {
                Diag.Log("Final flush failed", ex);
            }
        }
    }
}
