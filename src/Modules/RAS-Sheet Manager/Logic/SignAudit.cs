using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RASv2.SheetManager.Logic
{
    /// <summary>Что не так в семействе подписей (находка «Актуализировать»).</summary>
    public enum SignIssueKind
    {
        /// <summary>Файла подписи нет в папке — убрать человека (параметр, DWG, строку таблицы).</summary>
        NoFile,
        /// <summary>DWG вставлен вручную (не очищенный) — убрать человека; файл в папке есть — его можно добавить заново.</summary>
        Manual,
        /// <summary>Строка таблицы без человека (параметра) — убрать строку.</summary>
        OrphanRow,
        /// <summary>Фамилия в таблице дважды — лишнюю строку убрать.</summary>
        DuplicateRow,
        /// <summary>Один номер — у двух фамилий в таблице (только сообщить).</summary>
        NumberConflict,
        /// <summary>Номер в формуле не совпадает с номером в таблице — переписать формулу.</summary>
        FormulaMismatch,
        /// <summary>Параметр есть, подписи (DWG) нет — добавить из папки (отмечается в списке окна).</summary>
        MissingDwg,
        /// <summary>У человека две вставки DWG и больше — оставить одну.</summary>
        DoubleDwg,
        /// <summary>Вставка DWG ни к кому не привязана — удалить.</summary>
        OrphanDwg,
        /// <summary>Тип вставки DWG без вставок (от удалённых подписей) — удалить: лишний вес файла.</summary>
        LeftoverType,
        /// <summary>Очищенный файл новее подписи в семействе (перечистили «Обновить») — заменить (отмечается в списке окна).</summary>
        OutdatedDwg,
        /// <summary>Текст для штампа (столбец «Фамилия И.» у ГИП) пустой или не с этой фамилии — вписать из справочника инициалов.</summary>
        Display,
        /// <summary>Очищенный файл в «clean», исходного для которого в папке нет, — удалить файл.</summary>
        CleanOrphan,
    }

    /// <summary>Находка «Актуализировать».</summary>
    public sealed class SignIssue
    {
        public SignIssueKind Kind;
        public string Name;
        public string Text;
        /// <summary>Плагин может исправить сам (иначе — только сообщить или отметить в списке окна).</summary>
        public bool Fixable;
        /// <summary>Номер строки таблицы (DuplicateRow, OrphanRow) и что вписать (FormulaMismatch — номер, Display — текст).</summary>
        public int Row = -1;
        public int Number;
        public string Value;
    }

    /// <summary>Человек в семействе — что о нём нужно проверке (читает Revit-часть).</summary>
    public sealed class SignPerson
    {
        public string Name;
        /// <summary>Номер из формулы на выбранную таблицу; null — формулы на неё нет.</summary>
        public int? Number;
        /// <summary>Имена файлов вставленных DWG (по типу вставки).</summary>
        public List<string> Dwg = new List<string>();
        /// <summary>Когда вставлен DWG (по отметке плагина); null — неизвестно (вставлен раньше или вручную).</summary>
        public DateTime? InsertedUtc;
    }

    /// <summary>Подпись в папке — для проверки.</summary>
    public sealed class SignSource
    {
        public string Name;
        public string CleanFile;
        public DateTime? CleanTimeUtc;
    }

    /// <summary>
    /// Проверка семейства подписей — «Актуализировать» (разработчик, 27.10.2026: «давай актуализируем всё, что ты предложил»):
    /// люди без файла в папке и вставленные вручную, строки таблицы без людей и дубли, один номер у двух фамилий, номер формулы
    /// не как в таблице, параметр без подписи, две вставки у человека, вставки ни к кому не привязанные, типы вставок без
    /// вставок, подпись старее очищенного файла, пустой или чужой текст для штампа у ГИП. Сдвинутые с общей точки подписи не
    /// трогаются (сдвиг бывает намеренным). Без Revit API — проверяется тестами.
    /// </summary>
    public static class SignAudit
    {
        public static List<SignIssue> Check(SizeTable table, List<SignPerson> people, List<SignSource> folder, int orphanDwg, int leftoverTypes,
            int displayColumn, IDictionary<string, string> initials)
        {
            var issues = new List<SignIssue>();
            var inFolder = folder.GroupBy(f => SignNames.ToRussian(f.Name)).ToDictionary(g => g.Key, g => g.First());
            var byName = people.ToDictionary(p => p.Name);
            var known = new HashSet<string>(people.SelectMany(p => new[] { p.Name, SignNames.ToRussian(p.Name) }));

            foreach (var p in people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                inFolder.TryGetValue(SignNames.ToRussian(p.Name), out var src);
                if (src == null)
                {
                    issues.Add(new SignIssue { Kind = SignIssueKind.NoFile, Name = p.Name, Fixable = true, Text = "файла подписи нет в папке — убрать из семейства" });
                    continue;
                }
                if (p.Dwg.Any(d => d.IndexOf(SignFolder.Suffix, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    issues.Add(new SignIssue { Kind = SignIssueKind.Manual, Name = p.Name, Fixable = true, Text = "подпись вставлена вручную (не очищена) — убрать; затем добавить из папки как обычно" });
                    continue;
                }
                if (p.Dwg.Count == 0)
                    issues.Add(new SignIssue { Kind = SignIssueKind.MissingDwg, Name = p.Name, Text = "параметр есть, подписи нет — отмечена в списке для добавления" });
                if (p.Dwg.Count > 1)
                    issues.Add(new SignIssue { Kind = SignIssueKind.DoubleDwg, Name = p.Name, Fixable = true, Text = "вставок подписи — " + p.Dwg.Count + ": оставить одну" });
                if (p.Dwg.Count > 0 && p.InsertedUtc != null && src.CleanTimeUtc != null && src.CleanTimeUtc > p.InsertedUtc.Value.AddSeconds(5))
                    issues.Add(new SignIssue { Kind = SignIssueKind.OutdatedDwg, Name = p.Name, Text = "очищенный файл новее подписи в семействе — отмечена в списке для замены" });
            }

            // строки таблицы: без человека, дубли, номер у двух фамилий, формула не как в таблице
            var seen = new Dictionary<string, int>();
            var numbers = new Dictionary<int, string>();
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var r = table.Rows[i];
                var key = r[table.KeyCol].Trim();
                if (key.Length == 0) continue; // пустая строка «,,0» — нужна
                var n = SizeTable.Int(r[table.ValueCol]);
                if (seen.TryGetValue(key, out var first))
                {
                    issues.Add(new SignIssue { Kind = SignIssueKind.DuplicateRow, Name = key, Row = i, Fixable = true, Text = "фамилия в таблице дважды — лишнюю строку убрать" });
                    continue;
                }
                seen[key] = i;
                if (!known.Contains(key))
                {
                    issues.Add(new SignIssue { Kind = SignIssueKind.OrphanRow, Name = key, Row = i, Fixable = true, Text = "строка таблицы без подписи в семействе — убрать" });
                    continue;
                }
                if (n != null)
                {
                    if (numbers.TryGetValue(n.Value, out var other))
                        issues.Add(new SignIssue { Kind = SignIssueKind.NumberConflict, Name = key, Text = "номер " + n.Value + " — и у «" + other + "»: одна формула включит обе подписи" });
                    else numbers[n.Value] = key;
                }
                var person = people.FirstOrDefault(p => p.Name == key || SignNames.ToRussian(p.Name) == key);
                if (person?.Number != null && n != null && person.Number != n)
                    issues.Add(new SignIssue { Kind = SignIssueKind.FormulaMismatch, Name = person.Name, Number = n.Value, Fixable = true,
                        Text = "в формуле номер " + person.Number + ", в таблице — " + n.Value + ": переписать формулу" });

                // текст для штампа (у ГИП — «Фамилия И.»)
                if (displayColumn >= 0 && displayColumn < r.Count && person != null)
                {
                    var shown = r[displayColumn].Trim();
                    var ok = shown.Length > 0 && shown.StartsWith(key + " ", StringComparison.CurrentCulture);
                    if (!ok)
                    {
                        initials.TryGetValue(key, out var ini);
                        var value = ini != null ? Display(key, ini) : null;
                        issues.Add(new SignIssue
                        {
                            Kind = SignIssueKind.Display, Name = key, Row = i, Value = value, Fixable = value != null,
                            Text = (shown.Length == 0 ? "нет текста для штампа" : "текст для штампа «" + shown + "» — не с этой фамилии") +
                                   (value != null ? " — вписать «" + value + "»" : " — инициал неизвестен: впишите его справа"),
                        });
                    }
                }
            }
            if (orphanDwg > 0)
                issues.Add(new SignIssue { Kind = SignIssueKind.OrphanDwg, Name = "", Fixable = true, Text = "вставок DWG ни к кому не привязано — " + orphanDwg + ": удалить" });
            if (leftoverTypes > 0)
                issues.Add(new SignIssue { Kind = SignIssueKind.LeftoverType, Name = "", Fixable = true, Text = "типов DWG без вставок (от удалённых подписей) — " + leftoverTypes + ": удалить" });
            return issues;
        }

        /// <summary>«Байгараева», «Д» → «Байгараева Д.»; «А.Б» → «А.Б.».</summary>
        public static string Display(string surname, string initial)
        {
            var i = (initial ?? "").Trim().Trim('.').Trim();
            if (i.Length == 0) return null;
            return surname + " " + i + ".";
        }

        /// <summary>Инициал из текста для штампа: «Байгараева Д.» и фамилия «Байгараева» → «Д.»; не с этой фамилии — null.</summary>
        public static string Initial(string surname, string shown)
        {
            shown = (shown ?? "").Trim();
            if (!shown.StartsWith(surname + " ", StringComparison.CurrentCulture)) return null;
            var rest = shown.Substring(surname.Length).Trim();
            return rest.Length == 0 ? null : rest;
        }

        /// <summary>
        /// Столбец «текст для штампа» (у таблицы ГИП — «Инициалы»: «Байгараева Д.»): текстовый столбец, кроме ключа, где у
        /// большинства заполненных строк текст начинается с фамилии и пробела. Нет такого — -1.
        /// </summary>
        public static int DisplayColumn(SizeTable t)
        {
            foreach (var c in t.ExtraColumns())
            {
                if (t.Header[c].IndexOf("##other", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var filled = t.Rows.Where(r => r[t.KeyCol].Trim().Length > 0 && c < r.Count && r[c].Trim().Length > 0).ToList();
                if (filled.Count == 0) continue;
                var like = filled.Count(r => r[c].Trim().StartsWith(r[t.KeyCol].Trim() + " ", StringComparison.CurrentCulture));
                if (like * 2 >= filled.Count) return c;
            }
            return -1;
        }
    }

    /// <summary>
    /// Справочник инициалов (фамилия → инициал, «Д.») — общий для всех семейств подписей ГИП: инициал вписывают один раз,
    /// дальше он подставляется сам. Пополняется из таблиц ГИП (у кого текст для штампа уже есть) и из окна. Файл — рядом с
    /// общими настройками на сервере. Без Revit API — проверяется тестами.
    /// </summary>
    public static class SignInitials
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (line.TrimStart().StartsWith("#") || eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (k.Length > 0 && v.Length > 0) d[k] = v;
            }
            return d;
        }

        public static string Write(IDictionary<string, string> d) =>
            "# Справочник инициалов для подписей ГИП: фамилия = инициал (RAS-Sheet Manager, «Подпись»)\r\n" +
            string.Join("\r\n", d.OrderBy(kv => kv.Key, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).Select(kv => kv.Key + " = " + kv.Value)) + "\r\n";

        /// <summary>Взять инициалы из таблицы (столбец текста для штампа). Сколько новых.</summary>
        public static int Learn(IDictionary<string, string> d, SizeTable t, int displayColumn)
        {
            if (displayColumn < 0) return 0;
            var added = 0;
            foreach (var r in t.Rows)
            {
                var key = r[t.KeyCol].Trim();
                if (key.Length == 0 || displayColumn >= r.Count) continue;
                var ini = SignAudit.Initial(key, r[displayColumn]);
                if (ini == null || d.ContainsKey(key)) continue;
                d[key] = ini;
                added++;
            }
            return added;
        }
    }
}
