using System;
using System.Collections.Generic;
using System.Text;

namespace RASv2.ClashAnalysis.Reports
{
    /// <summary>
    /// Какая открытая модель или связь — модель элемента коллизии. В отчёте Navisworks — файл NWC («…_AR01_01_M3.nwc»), в Revit —
    /// модель и её связи («…_AR01_01_M3.rvt»). NWC часто выгружают из отсоединённой копии или с припиской: «…_M3_отсоединено.nwc»,
    /// «…_M3_с лестницей_BIM_Alma.nwc» (MODULE.md, «Имена файлов .nwc — не всегда имена хранилищ»). Общее для моста в Revit и сайта
    /// («Коллизии элемента»: элемент из Revit — его коллизии на сайте) — один код. Без Revit API — проверяется тестами.
    /// </summary>
    public static class ModelNames
    {
        private static readonly string[] Extensions = { ".nwc", ".nwd", ".nwf", ".rvt", ".ifc", ".dwg" };

        /// <summary>Имя без папки и расширения модели: «C:\…\Дом_AR01.nwc» → «Дом_AR01».</summary>
        public static string Bare(string file)
        {
            var name = (file ?? "").Trim();
            var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (slash >= 0) name = name.Substring(slash + 1);
            foreach (var ext in Extensions)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - ext.Length).Trim();
            return name;
        }

        /// <summary>
        /// Ключ для сравнения: без папки и расширения, без регистра, «ё» = «е», русские буквы, похожие на латинские, — латинские
        /// («М3» кириллицей = «M3» латиницей: в именах файлов их путают).
        /// </summary>
        public static string Key(string file)
        {
            var bare = Bare(file).ToLowerInvariant();
            var sb = new StringBuilder(bare.Length);
            foreach (var ch in bare) sb.Append(Latin(ch));
            return sb.ToString();
        }

        private static char Latin(char ch)
        {
            switch (ch)
            {
                case 'а': return 'a';
                case 'в': return 'b';
                case 'е': case 'ё': return 'e';
                case 'к': return 'k';
                case 'м': return 'm';
                case 'н': return 'h';
                case 'о': return 'o';
                case 'р': return 'p';
                case 'с': return 'c';
                case 'т': return 't';
                case 'у': return 'y';
                case 'х': return 'x';
                default: return ch;
            }
        }

        private static bool Separator(char ch) => ch == '_' || ch == ' ' || ch == '-' || ch == '.';

        /// <summary>
        /// Номер модели из candidates, к которой относится файл модели из отчёта; нет такой — −1. Сначала — точное совпадение;
        /// иначе — модель, имя которой — начало имени NWC до разделителя («…_M3» для «…_M3_отсоединено»; из нескольких — самое
        /// длинное); иначе — наоборот (открыта отсоединённая копия «…_M3_отсоединено», в отчёте «…_M3»).
        /// </summary>
        public static int Match(string reportModel, IList<string> candidates)
        {
            var key = Key(reportModel);
            if (key.Length == 0 || candidates == null) return -1;
            var keys = new List<string>(candidates.Count);
            foreach (var c in candidates) keys.Add(Key(c));
            for (var i = 0; i < keys.Count; i++)
                if (keys[i] == key) return i;
            var best = -1;
            for (var i = 0; i < keys.Count; i++)
                if (StartsWithPart(key, keys[i]) && (best < 0 || keys[i].Length > keys[best].Length)) best = i;
            if (best >= 0) return best;
            for (var i = 0; i < keys.Count; i++)
                if (StartsWithPart(keys[i], key) && (best < 0 || keys[i].Length < keys[best].Length)) best = i;
            return best;
        }

        /// <summary>«дом_ar01_m3_отсоединено» начинается с «дом_ar01_m3» и дальше разделитель (а не «дом_ar0» от «дом_ar01»).</summary>
        private static bool StartsWithPart(string longer, string start) =>
            start.Length > 0 && longer.Length > start.Length && longer.StartsWith(start, StringComparison.Ordinal) && Separator(longer[start.Length]);
    }
}
