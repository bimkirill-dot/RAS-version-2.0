using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Кэш содержимого папок Revit Server для «Добавить модели…» (разработчик, 03.11.2026: «на сервер долго заходит — сделай кэш»):
    /// по файлу на сервер, ключ — версия Revit и путь папки. Окно показывает сохранённое сразу и обновляет его с сервера в фоне;
    /// «Обновить» — заново с сервера. Сломанный файл кэша — как пустой. Без Revit API.
    /// </summary>
    public sealed class ServerCache
    {
        [DataContract]
        private sealed class Entry
        {
            [DataMember(Order = 1)] public string Key { get; set; }
            [DataMember(Order = 2)] public DateTime Time { get; set; }
            [DataMember(Order = 3)] public ServerContents Contents { get; set; }
        }

        private readonly string _file;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private ServerCache(string file) => _file = file;

        /// <summary>Папка кэша на компьютере.</summary>
        public static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAS_v_2.0", "RAS-Clash Analysis", "Кэш Revit Server");

        private static readonly Dictionary<string, ServerCache> Opened = new Dictionary<string, ServerCache>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Кэш сервера — один на время работы Revit (с диска читается один раз).</summary>
        public static ServerCache Of(string host)
        {
            lock (Opened)
            {
                if (!Opened.TryGetValue(host, out var cache)) Opened[host] = cache = Open(DefaultDir, host);
                return cache;
            }
        }

        /// <summary>Кэш сервера host в папке dir (файл «{host}.json»); нет файла или он испорчен — пустой.</summary>
        public static ServerCache Open(string dir, string host)
        {
            var cache = new ServerCache(Path.Combine(dir, string.Concat(host.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json"));
            try
            {
                if (File.Exists(cache._file))
                    using (var stream = File.OpenRead(cache._file))
                        foreach (var e in (List<Entry>)new DataContractJsonSerializer(typeof(List<Entry>)).ReadObject(stream) ?? new List<Entry>())
                            if (e?.Key != null && e.Contents != null)
                            {
                                e.Contents.Folders = e.Contents.Folders ?? new List<ServerFolder>();
                                e.Contents.Models = e.Contents.Models ?? new List<ServerModel>();
                                cache._entries[e.Key] = e;
                            }
            }
            catch (Exception ex) when (ex is SerializationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                cache._entries.Clear();
            }
            return cache;
        }

        private static string Key(int year, IList<string> folder) => year + "|" + string.Join("/", folder);

        /// <summary>Сохранённое содержимое папки и когда получено; нет — false.</summary>
        public bool TryGet(int year, IList<string> folder, out ServerContents contents, out DateTime time)
        {
            if (_entries.TryGetValue(Key(year, folder), out var e))
            {
                contents = e.Contents;
                time = e.Time;
                return true;
            }
            contents = null;
            time = default(DateTime);
            return false;
        }

        /// <summary>Запомнить ответ сервера и записать файл (не записался — кэш просто не сохранится, окно работает).</summary>
        public void Put(int year, IList<string> folder, ServerContents contents, DateTime time)
        {
            _entries[Key(year, folder)] = new Entry { Key = Key(year, folder), Time = time, Contents = contents };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var tmp = _file + ".tmp";
                using (var stream = File.Create(tmp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, false))
                    new DataContractJsonSerializer(typeof(List<Entry>)).WriteObject(writer, _entries.Values.ToList());
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(tmp, _file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // кэш — не главное: в следующий раз окно спросит сервер
            }
        }
    }
}
