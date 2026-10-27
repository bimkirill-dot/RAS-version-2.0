using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.SheetManager.Logic
{
    /// <summary>
    /// Фамилия из имени файла подписи и что в ней допустимо (перенос из «Добавить подписи», surname_from_file, ok_name).
    /// </summary>
    public static class SignNames
    {
        /// <summary>Знаки, запрещённые в именах параметров Revit.</summary>
        public const string BadChars = "\\:{}[]|;<>?`~";
        public const string Kazakh = "ӘәҒғҚқҢңӨөҰұҮүҺһІі";
        private const string KazakhAsRussian = "АаГгКкНнОоУуУуХхИи";

        /// <summary>
        /// «#КЖ_Подпись_Иванов_clean.dwg» → «Иванов»: без расширения и «_clean», текст после слова word («Подпись_» или
        /// «Подпись »), лишнее «dwg» в конце (опечатка) — убрать, «_» — пробел, запрещённые знаки — убрать. Слова нет — всё
        /// имя файла (фамилию в окне можно поправить).
        /// </summary>
        public static string FromFile(string fileName, string word, out string note)
        {
            note = null;
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
            if (name.EndsWith(SignFolder.Suffix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - SignFolder.Suffix.Length);
            if (!string.IsNullOrWhiteSpace(word))
            {
                var m = Regex.Match(name, Regex.Escape(word.Trim()) + "[ _]+(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value;
                else note = "в имени файла нет «" + word.Trim() + "» — проверьте фамилию";
            }
            if (name.Length > 3 && name.EndsWith("dwg", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 3).TrimEnd('.', '_', ' ');
                note = "в имени файла лишнее «dwg» — убрано";
            }
            return Clean(name.Replace('_', ' '));
        }

        public static string Clean(string name) => new string((name ?? "").Where(ch => BadChars.IndexOf(ch) < 0).ToArray()).Trim();

        public static bool HasKazakh(string s) => (s ?? "").Any(ch => Kazakh.IndexOf(ch) >= 0);

        /// <summary>Казахские буквы → ближайшие русские: таблица выбора в Windows-1251 их не хранит.</summary>
        public static string ToRussian(string s) =>
            new string((s ?? "").Select(ch => { var i = Kazakh.IndexOf(ch); return i >= 0 ? KazakhAsRussian[i] : ch; }).ToArray());

        /// <summary>null — фамилия годится; иначе — почему нет. Допустимы латиница, кириллица, Ё, № и казахские буквы.</summary>
        public static string Problem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "нет фамилии";
            if (name.Any(ch => BadChars.IndexOf(ch) >= 0)) return "в фамилии запрещённые знаки " + BadChars;
            foreach (var ch in ToRussian(name))
                if (!(ch < 128 || (ch >= 'А' && ch <= 'я') || ch == 'Ё' || ch == 'ё' || ch == '№'))
                    return "в фамилии недопустимый знак «" + ch + "»";
            return null;
        }
    }

    /// <summary>
    /// Регламент наименования файла подписи (разработчик, 27.10.2026): «[Отдел]_Подпись_Фамилия.dwg»; главный специалист —
    /// «#» перед отделом («#КЖ_Подпись_Иванов.dwg»); ГИП и ГАП — «!», на месте отдела — ГИП или ГАП («!ГИП_Подпись_Иванов.dwg»,
    /// как у всех 119 подписей 27.10.2026). Отдел — заглавными; список отделов и «руководителей» — в настройках (пусто — любые).
    /// Фамилия — с заглавной буквы, буквы одного алфавита, пробел (инициал: «Ким И»), дефис, точка.
    /// </summary>
    public sealed class SignFileName
    {
        public string Prefix = "", Department = "", Surname = "";
        /// <summary>null — по регламенту; иначе — что не так.</summary>
        public string Problem;

        private static readonly Regex Shape = new Regex("^([#!]?)([^_#!]*)_(.+?)_(.+)$");

        public static SignFileName Check(string fileName, string word, IList<string> departments, IList<string> leaders)
        {
            var r = new SignFileName();
            var ext = System.IO.Path.GetExtension(fileName ?? "");
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
            word = string.IsNullOrWhiteSpace(word) ? "Подпись" : word.Trim();
            if (!string.Equals(ext, ".dwg", StringComparison.OrdinalIgnoreCase)) return r.Fail("расширение — .dwg");
            var m = Shape.Match(name);
            if (!m.Success || !string.Equals(m.Groups[3].Value, word, StringComparison.OrdinalIgnoreCase))
                return r.Fail("нужно «[Отдел]_" + word + "_Фамилия»");
            r.Prefix = m.Groups[1].Value;
            r.Department = m.Groups[2].Value;
            r.Surname = m.Groups[4].Value;
            if (r.Department.Length == 0) return r.Fail("нет отдела перед «_" + word + "»");
            if (!r.Department.All(ch => char.IsLetter(ch) && char.IsUpper(ch))) return r.Fail("отдел «" + r.Department + "» — заглавными буквами, без пробелов");
            var leader = leaders.Any(l => string.Equals(l, r.Department, StringComparison.Ordinal));
            if (leader && r.Prefix != "!") return r.Fail(r.Department + " — со знаком «!» в начале: «!" + r.Department + "_" + word + "_…»");
            if (!leader && r.Prefix == "!") return r.Fail("«!» — только у " + (leaders.Count > 0 ? string.Join(" и ", leaders) : "руководителей"));
            if (!leader && departments.Count > 0 && !departments.Contains(r.Department)) return r.Fail("отдела «" + r.Department + "» нет в списке: " + string.Join(", ", departments));
            if (r.Surname.Length == 0 || !char.IsLetter(r.Surname[0]) || !char.IsUpper(r.Surname[0])) return r.Fail("фамилия — с заглавной буквы");
            if (r.Surname.Any(ch => !(char.IsLetter(ch) || ch == ' ' || ch == '-' || ch == '.'))) return r.Fail("в фамилии — только буквы, пробел, дефис, точка (без «_»)");
            if (r.Surname.Any(ch => ch < 128 && char.IsLetter(ch)) && r.Surname.Any(ch => ch >= 128 && char.IsLetter(ch)))
                return r.Fail("в фамилии смешаны латиница и кириллица («" + r.Surname + "»)");
            return r;
        }

        private SignFileName Fail(string why)
        {
            Problem = why;
            return this;
        }

        /// <summary>«КЖ, ОВ ,  вк» → «КЖ», «ОВ», «вк».</summary>
        public static List<string> List(string text) =>
            (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>Формула параметра подписи: size_lookup("таблица", "столбец", по умолчанию, ключ) = номер.</summary>
    public sealed class SignFormula
    {
        private static readonly Regex Full = new Regex(
            "size_lookup\\(\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]*)\"\\s*,\\s*(-?[\\d.]+)\\s*,\\s*([^)]+?)\\s*\\)\\s*=\\s*(-?\\d+)\\s*$");
        private static readonly Regex AnyTable = new Regex("size_lookup\\(\\s*\"([^\"]+)\"");

        public string Table, Column, Default, Key;
        public int Number;

        public static SignFormula Parse(string formula)
        {
            var m = Full.Match(formula ?? "");
            if (!m.Success) return null;
            return new SignFormula
            {
                Table = m.Groups[1].Value, Column = m.Groups[2].Value, Default = m.Groups[3].Value, Key = m.Groups[4].Value.Trim(),
                Number = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
            };
        }

        public static string Make(string table, string column, string defaultValue, string key, int number) =>
            "size_lookup(\"" + table + "\", \"" + column + "\", " + defaultValue + ", " + key + ") = " + number.ToString(CultureInfo.InvariantCulture);

        /// <summary>Все таблицы, на которые ссылается формула (в том числе во втором size_lookup и внутри if).</summary>
        public static IEnumerable<string> Tables(string formula) => AnyTable.Matches(formula ?? "").Cast<Match>().Select(m => m.Groups[1].Value);
    }

    /// <summary>Кодировки CSV таблицы выбора: Revit читает их по-разному, поэтому после загрузки — самопроверка.</summary>
    public static class TableEncodings
    {
        public const string Utf8Bom = "utf8bom", Utf8 = "utf8", Cp1251 = "cp1251";

        /// <summary>Windows-1251: в .NET 8 — через поставщика кодовых страниц (находится без ссылки на сборку).</summary>
        public static Encoding Windows1251
        {
            get
            {
                try { return Encoding.GetEncoding(1251); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
                {
                    var type = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                    if (type?.GetProperty("Instance")?.GetValue(null) is EncodingProvider provider) Encoding.RegisterProvider(provider);
                    return Encoding.GetEncoding(1251);
                }
            }
        }

        public static string Decode(byte[] data, out string encoding)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                encoding = Utf8Bom;
                return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
            }
            try
            {
                var text = new UTF8Encoding(false, true).GetString(data);
                encoding = Utf8;
                return text;
            }
            catch (DecoderFallbackException)
            {
                encoding = Cp1251;
                return Windows1251.GetString(data);
            }
        }

        public static byte[] Encode(string text, string encoding)
        {
            switch (encoding)
            {
                case Cp1251: return Windows1251.GetBytes(text);
                case Utf8Bom: return new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
                default: return new UTF8Encoding(false).GetBytes(text);
            }
        }

        /// <summary>
        /// В каком порядке пробовать: казахские буквы есть только в Юникоде — сначала UTF-8; иначе — как была записана таблица,
        /// потом остальные. Каждая кодировка — один раз.
        /// </summary>
        public static List<string> Order(bool kazakhInTable, string source, bool hasNonAscii, bool tableExists)
        {
            List<string> order;
            if (kazakhInTable) order = new List<string> { Utf8Bom, Utf8, Cp1251 };
            else if (source == Utf8Bom) order = new List<string> { Utf8Bom, Cp1251, Utf8 };
            else if (source == Utf8 && hasNonAscii && tableExists) order = new List<string> { Utf8, Utf8Bom, Cp1251 };
            else order = new List<string> { Cp1251, Utf8Bom, Utf8 };
            return order;
        }
    }

    /// <summary>
    /// Таблица выбора фамилий (CSV, как её выгружает Revit): заголовок «,Фамилия##other##,Да/Нет##number##general», строки
    /// «Иванов,Иванов,12» — фамилия и её постоянный номер, пустая строка «,,0». Столбцы — по заголовку.
    /// </summary>
    public sealed class SizeTable
    {
        public List<string> Header = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
        public int KeyCol = 1, ValueCol = 2;

        /// <summary>Новая: только заголовок и пустая строка «,,0».</summary>
        public static SizeTable Create(string key, string column)
        {
            var t = new SizeTable { Header = new List<string> { "", key + "##other##", column + "##number##general" } };
            t.Rows.Add(new List<string> { "", "", "0" });
            return t;
        }

        public static SizeTable Parse(string text, string key, string column)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return Create(key, column);
            var t = new SizeTable { Header = Split(lines[0]) };
            t.Rows = lines.Skip(1).Select(Split).ToList();
            for (var i = 1; i < t.Header.Count; i++)
            {
                var name = t.Header[i].Split(new[] { "##" }, StringSplitOptions.None)[0].Trim();
                if (name == key) t.KeyCol = i;
                if (name == column) t.ValueCol = i;
            }
            var n = Math.Max(t.Header.Count, Math.Max(t.KeyCol, t.ValueCol) + 1);
            foreach (var r in t.Rows) while (r.Count < n) r.Add("");
            return t;
        }

        /// <summary>Фамилия → номер (первая строка с этой фамилией).</summary>
        public Dictionary<string, int> NumberByKey()
        {
            var d = new Dictionary<string, int>();
            foreach (var r in Rows)
            {
                var n = Int(r[ValueCol]);
                if (n == null) continue;
                var k = r[KeyCol].Trim();
                if (!d.ContainsKey(k)) d[k] = n.Value;
            }
            return d;
        }

        /// <summary>Номер → фамилия.</summary>
        public Dictionary<int, string> KeyByNumber()
        {
            var d = new Dictionary<int, string>();
            foreach (var r in Rows)
            {
                var n = Int(r[ValueCol]);
                if (n != null && !d.ContainsKey(n.Value)) d[n.Value] = r[KeyCol].Trim();
            }
            return d;
        }

        public void AddRow(string name, int number)
        {
            var r = Enumerable.Repeat("", Math.Max(Header.Count, Math.Max(KeyCol, ValueCol) + 1)).ToList();
            r[0] = name;
            r[KeyCol] = name;
            r[ValueCol] = number.ToString(CultureInfo.InvariantCulture);
            Rows.Add(r);
        }

        public bool HasKazakh => Rows.Any(r => SignNames.HasKazakh(r[KeyCol]));

        /// <summary>Текст CSV; в Windows-1251 казахские буквы заменяются русскими (имена параметров остаются казахскими).</summary>
        public string Text(string encoding)
        {
            Func<string, string> fix = encoding == TableEncodings.Cp1251 ? SignNames.ToRussian : (Func<string, string>)(s => s);
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Header.Select(Cell))).Append("\r\n");
            foreach (var r in Rows) sb.Append(string.Join(",", r.Select(c => Cell(fix(c))))).Append("\r\n");
            return sb.ToString();
        }

        public static int? Int(string s)
        {
            return double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? (int)Math.Round(v)
                : (int?)null;
        }

        private static string Cell(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        /// <summary>Строка CSV с кавычками («"" » внутри — кавычка).</summary>
        public static List<string> Split(string line)
        {
            var res = new List<string>();
            var cur = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cur.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { res.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            res.Add(cur.ToString());
            return res;
        }
    }
}
