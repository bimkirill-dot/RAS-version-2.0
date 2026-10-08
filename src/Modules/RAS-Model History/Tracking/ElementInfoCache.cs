using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Сведения об элементах между сеансами (Cache\&lt;модель&gt;\elements.dat рядом со снимками): при повторном открытии
    /// модуль сразу знает, что за элемент удалили, — не дожидаясь подготовки модели. Сведения из прошлого сеанса —
    /// только запасной источник: подготовка описывает элементы заново. Файл — двоичный, строки — общей таблицей
    /// (категории, уровни, типы повторяются тысячи раз). Без Revit API: проверяется тестами.
    /// </summary>
    public static class ElementInfoCache
    {
        private const int Magic = 0x45494331; // "EIC1"
        private const int Format = 1;

        public static Dictionary<long, ElementInfo> Load(string path)
        {
            var result = new Dictionary<long, ElementInfo>();
            if (!File.Exists(path)) return result;
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (r.ReadInt32() != Magic || r.ReadInt32() != Format) return result; // другой формат — подготовка опишет заново
                var strings = new string[r.ReadInt32()];
                for (var i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
                string S() { var i = r.ReadInt32(); return i < 0 ? null : strings[i]; }
                var count = r.ReadInt32();
                for (var n = 0; n < count; n++)
                {
                    var info = new ElementInfo
                    {
                        Id = r.ReadInt64(),
                        UniqueId = S(),
                        Category = S(),
                        ClassName = S(),
                        Group = (ObjectGroup)r.ReadInt32(),
                        Name = S(),
                        TypeName = S(),
                        Level = S(),
                        Workset = S(),
                        OwnerView = S(),
                        Creator = S(),
                        IsNoise = r.ReadBoolean()
                    };
                    var parents = r.ReadInt32();
                    if (parents > 0)
                    {
                        info.Parents = new long[parents];
                        for (var i = 0; i < parents; i++) info.Parents[i] = r.ReadInt64();
                    }
                    result[info.Id] = info;
                }
            }
            return result;
        }

        /// <summary>Записать во временный файл и заменить: оборванная запись не портит прежний кэш.</summary>
        public static void Save(string path, ICollection<ElementInfo> items)
        {
            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            var strings = new List<string>();
            int Index(string s)
            {
                if (s == null) return -1;
                if (!table.TryGetValue(s, out var i))
                {
                    i = strings.Count;
                    table[s] = i;
                    strings.Add(s);
                }
                return i;
            }
            var rows = new List<int[]>(items.Count);
            foreach (var x in items)
                rows.Add(new[] { Index(x.UniqueId), Index(x.Category), Index(x.ClassName), Index(x.Name), Index(x.TypeName),
                                 Index(x.Level), Index(x.Workset), Index(x.OwnerView), Index(x.Creator) });

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".new";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Format);
                w.Write(strings.Count);
                foreach (var s in strings) w.Write(s);
                w.Write(items.Count);
                var n = 0;
                foreach (var x in items)
                {
                    var s = rows[n++];
                    w.Write(x.Id);
                    w.Write(s[0]); w.Write(s[1]); w.Write(s[2]);
                    w.Write((int)x.Group);
                    w.Write(s[3]); w.Write(s[4]); w.Write(s[5]); w.Write(s[6]); w.Write(s[7]); w.Write(s[8]);
                    w.Write(x.IsNoise);
                    var parents = x.Parents ?? Array.Empty<long>();
                    w.Write(parents.Length);
                    foreach (var p in parents) w.Write(p);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
