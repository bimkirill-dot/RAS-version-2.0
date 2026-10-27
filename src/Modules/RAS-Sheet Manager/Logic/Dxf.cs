using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RASv2.SheetManager.Logic
{
    /// <summary>Объект DXF: тип и пары «код — значение» по порядку (у POLYLINE — ещё его вершины).</summary>
    public sealed class DxfEntity
    {
        public DxfEntity(string type) => Type = type;

        public string Type { get; }
        public List<KeyValuePair<int, string>> Pairs { get; } = new List<KeyValuePair<int, string>>();
        /// <summary>Вершины POLYLINE (объекты VERTEX до SEQEND).</summary>
        public List<DxfEntity> Vertices { get; } = new List<DxfEntity>();

        public string Str(int code, string fallback = null)
        {
            foreach (var p in Pairs) if (p.Key == code) return p.Value.Trim();
            return fallback;
        }

        public double Num(int code, double fallback = 0)
        {
            var s = Str(code);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public int Int(int code, int fallback = 0)
        {
            var s = Str(code);
            return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public bool Has(int code)
        {
            foreach (var p in Pairs) if (p.Key == code) return true;
            return false;
        }

        public string Layer => Str(8, "0");

        /// <summary>Нормаль (210/220/230); нет — (0,0,1).</summary>
        public Vec3 Extrusion => Has(210) ? new Vec3(Num(210), Num(220), Num(230, 1)) : new Vec3(0, 0, 1);
    }

    /// <summary>Описание блока: точка вставки и объекты.</summary>
    public sealed class DxfBlock
    {
        public string Name;
        public Vec3 Base;
        public List<DxfEntity> Entities = new List<DxfEntity>();
    }

    /// <summary>
    /// Чтение текстового DXF (как его пишет AutoCAD командой DXFOUT): единицы ($INSUNITS), слои (выключенные и
    /// замороженные), блоки и объекты модели. Только то, что нужно для подписи; незнакомое пропускается.
    /// </summary>
    public sealed class DxfDocument
    {
        public int InsUnits { get; private set; } = 4;
        /// <summary>Выключенные или замороженные слои — в нижнем регистре.</summary>
        public HashSet<string> HiddenLayers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DxfBlock> Blocks { get; } = new Dictionary<string, DxfBlock>(StringComparer.OrdinalIgnoreCase);
        public List<DxfEntity> Model { get; } = new List<DxfEntity>();

        public static DxfDocument Load(string path) => Parse(ReadText(path));

        /// <summary>DXF 2007 и новее — UTF-8; старые — в кодировке Windows (у подписей имена латиницей, так что не страшно).</summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        public static DxfDocument Parse(string text)
        {
            var doc = new DxfDocument();
            var pairs = Pairs(text);
            var i = 0;
            while (i < pairs.Count)
            {
                if (pairs[i].Key == 0 && pairs[i].Value == "SECTION" && i + 1 < pairs.Count)
                {
                    var name = pairs[i + 1].Value;
                    i += 2;
                    var end = i;
                    while (end < pairs.Count && !(pairs[end].Key == 0 && pairs[end].Value == "ENDSEC")) end++;
                    if (name == "HEADER") doc.ReadHeader(pairs, i, end);
                    else if (name == "TABLES") doc.ReadLayers(pairs, i, end);
                    else if (name == "BLOCKS") doc.ReadBlocks(pairs, i, end);
                    // в ENTITIES вместе с моделью — и лист (код 67 = 1: рамка, штамп): подпись — только модель
                    else if (name == "ENTITIES") doc.Model.AddRange(Entities(pairs, i, end).Where(e => e.Int(67) != 1));
                    i = end + 1;
                }
                else i++;
            }
            return doc;
        }

        private static List<KeyValuePair<int, string>> Pairs(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var result = new List<KeyValuePair<int, string>>(lines.Length / 2);
            for (var k = 0; k + 1 < lines.Length; k += 2)
            {
                if (!int.TryParse(lines[k].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) break;
                // строковые значения нужны без концевых пробелов (имена), числа разбираются после Trim
                result.Add(new KeyValuePair<int, string>(code, code == 0 || code == 2 || code == 8 || code == 9 ? lines[k + 1].Trim() : lines[k + 1]));
            }
            return result;
        }

        private void ReadHeader(List<KeyValuePair<int, string>> pairs, int from, int to)
        {
            for (var i = from; i + 1 < to; i++)
                if (pairs[i].Key == 9 && pairs[i].Value == "$INSUNITS" && pairs[i + 1].Key == 70 &&
                    int.TryParse(pairs[i + 1].Value.Trim(), out var u))
                    InsUnits = u;
        }

        private void ReadLayers(List<KeyValuePair<int, string>> pairs, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (pairs[i].Key != 0 || pairs[i].Value != "LAYER") continue;
                string name = null;
                var flags = 0;
                var color = 7;
                var j = i + 1;
                for (; j < to && pairs[j].Key != 0; j++)
                {
                    if (pairs[j].Key == 2) name = pairs[j].Value;
                    else if (pairs[j].Key == 70) int.TryParse(pairs[j].Value.Trim(), out flags);
                    else if (pairs[j].Key == 62) int.TryParse(pairs[j].Value.Trim(), out color);
                }
                // заморожен — флаг 1; выключен — отрицательный цвет
                if (name != null && ((flags & 1) != 0 || color < 0)) HiddenLayers.Add(name);
                i = j - 1;
            }
        }

        private void ReadBlocks(List<KeyValuePair<int, string>> pairs, int from, int to)
        {
            var i = from;
            while (i < to)
            {
                if (pairs[i].Key == 0 && pairs[i].Value == "BLOCK")
                {
                    var block = new DxfBlock();
                    var j = i + 1;
                    double bx = 0, by = 0, bz = 0;
                    for (; j < to && pairs[j].Key != 0; j++)
                    {
                        var v = pairs[j].Value.Trim();
                        if (pairs[j].Key == 2) block.Name = pairs[j].Value;
                        else if (pairs[j].Key == 10) bx = D(v);
                        else if (pairs[j].Key == 20) by = D(v);
                        else if (pairs[j].Key == 30) bz = D(v);
                    }
                    block.Base = new Vec3(bx, by, bz);
                    var end = j;
                    while (end < to && !(pairs[end].Key == 0 && pairs[end].Value == "ENDBLK")) end++;
                    block.Entities = Entities(pairs, j, end);
                    if (block.Name != null) Blocks[block.Name] = block;
                    i = end + 1;
                }
                else i++;
            }
        }

        /// <summary>Объекты между from и to; вершины POLYLINE — в его Vertices, атрибуты вставок пропускаются.</summary>
        private static List<DxfEntity> Entities(List<KeyValuePair<int, string>> pairs, int from, int to)
        {
            var list = new List<DxfEntity>();
            DxfEntity current = null, polyline = null;
            for (var i = from; i < to; i++)
            {
                var p = pairs[i];
                if (p.Key == 0)
                {
                    current = new DxfEntity(p.Value);
                    if (p.Value == "VERTEX" && polyline != null) polyline.Vertices.Add(current);
                    else if (p.Value == "SEQEND") { polyline = null; current = null; }
                    else if (p.Value == "ATTRIB" || p.Value == "VERTEX") { /* атрибут вставки, вершина без полилинии — не нужны */ }
                    else
                    {
                        list.Add(current);
                        polyline = p.Value == "POLYLINE" ? current : null;
                    }
                    continue;
                }
                current?.Pairs.Add(p);
            }
            return list;
        }

        private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        /// <summary>Множитель единиц чертежа в миллиметры ($INSUNITS): дюймы, футы, мм, см, м; неизвестно — как мм.</summary>
        public double ToMillimetres
        {
            get
            {
                switch (InsUnits)
                {
                    case 1: return 25.4;
                    case 2: return 304.8;
                    case 5: return 10;
                    case 6: return 1000;
                    default: return 1;
                }
            }
        }
    }
}
