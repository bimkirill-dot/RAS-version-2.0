using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Reports
{
    /// <summary>
    /// Наш статус коллизии — три, как у автора статьи (DECISIONS.md, «Коллизии: пара элементов, три статуса»; числа — в базе,
    /// не менять). Было ещё «Ждёт проверки» (3) — убрано 13.10.2026, в базе стало «Исправлена».
    /// </summary>
    public enum ClashState { Active = 0, Fixed = 1, Accepted = 2 }

    /// <summary>
    /// Правила над отчётом: что считать той же коллизией в следующем прогоне, тем же уровнем, каким статусом.
    /// Без Revit и без базы — общие для сайта и моста; проверяются тестами.
    /// </summary>
    public static class ClashRules
    {
        /// <summary>
        /// «Личность» коллизии — одна через все прогоны: проверка + номера двух элементов (порядок элементов не важен), как у
        /// автора статьи. Несколько результатов Navisworks на одну пару — одна коллизия; номер Navisworks не важен: он
        /// меняется, если проверку пересоздали или собрали новый файл Navisworks. Имя файла модели в «личность» не входит
        /// (14.10.2026): модель переименовали («…_Р3_BIM_Alma.nwc» → «…_Р3.nwc») — коллизии те же, а не «исправлены и найдены
        /// новые». Две разные модели с одинаковыми номерами в одном отчёте — <see cref="IdentityKeys"/>.
        /// Пара неполная (нет номера или файла у элемента) — номер Navisworks (guid), а без него — пара как есть.
        /// </summary>
        public static string IdentityKey(string testName, ClashResult result) =>
            HasFullPair(result.Elements) ? IdsKey(testName, result.Elements)
            : string.IsNullOrEmpty(result.Guid) ? PairKey(testName, result.Elements) : "guid:" + result.Guid;

        /// <summary>
        /// «Личности» результатов одной проверки одного отчёта. Одинаковые номера элементов у разных пар моделей в одном
        /// отчёте — разные коллизии (номер уникален только внутри своего файла): у них ключ с именами моделей (<see cref="PairKey"/>).
        /// </summary>
        public static List<string> IdentityKeys(string testName, IReadOnlyList<ClashResult> results)
        {
            var keys = results.Select(r => IdentityKey(testName, r)).ToList();
            var byIds = Enumerable.Range(0, results.Count).Where(i => keys[i].StartsWith("ids:", StringComparison.Ordinal)).GroupBy(i => keys[i], StringComparer.Ordinal);
            foreach (var g in byIds)
            {
                var pairs = g.Select(i => PairKey(testName, results[i].Elements)).ToList();
                if (pairs.Distinct(StringComparer.Ordinal).Count() < 2) continue;
                var n = 0;
                foreach (var i in g) keys[i] = pairs[n++];
            }
            return keys;
        }

        /// <summary>Проверка + номера элементов, порядок элементов не важен.</summary>
        public static string IdsKey(string testName, IEnumerable<ClashElement> elements)
        {
            var ids = elements.Select(e => e.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "?").OrderBy(k => k, StringComparer.Ordinal);
            return "ids:" + Simplify(testName) + "|" + string.Join("|", ids);
        }

        /// <summary>Проверка + пара элементов «файл модели + номер», порядок элементов не важен.</summary>
        public static string PairKey(string testName, IEnumerable<ClashElement> elements)
        {
            var pair = elements.Select(ElementKey).OrderBy(k => k, StringComparer.Ordinal);
            return "pair:" + Simplify(testName) + "|" + string.Join("|", pair);
        }

        /// <summary>
        /// По паре можно узнавать коллизию, только если у всех элементов есть файл модели и номер:
        /// иначе разные коллизии дали бы одинаковую пару («?»).
        /// </summary>
        public static bool HasFullPair(IReadOnlyCollection<ClashElement> elements) =>
            elements.Count > 0 && elements.All(e => e.ElementId.HasValue && !string.IsNullOrEmpty(e.ModelFile));

        /// <summary>Элемент — «файл модели + номер»: номер уникален только внутри своего файла (запасной ключ — <see cref="IdentityKeys"/>).</summary>
        public static string ElementKey(ClashElement e) =>
            ModelName(e.ModelFile).ToLowerInvariant() + "#" + (e.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "?");

        /// <summary>Имя модели без расширения: «3457_01_01_S8_AR01_01_M3.nwc» → «3457_01_01_S8_AR01_01_M3».</summary>
        public static string ModelName(string modelFile)
        {
            if (string.IsNullOrEmpty(modelFile)) return "";
            var ext = Path.GetExtension(modelFile);
            return ClashReportReader.IsModelExtension(ext) ? modelFile.Substring(0, modelFile.Length - ext.Length) : modelFile;
        }

        private static readonly Regex LevelNumber = new Regex(@"^\s*([-−–]?)0*(\d+)(.*)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Уровень для группировки и фильтров: ведущие нули номера, регистр и лишние пробелы не различаются —
        /// «05 Этаж» = «5 Этаж», «-01 Подвал» = «-1 подвал». Показывается уровень как в отчёте.
        /// Так в одном отчёте (S08, 15.09.2026) модель АР — «5 Этаж», модель ОВ — «05 Этаж».
        /// </summary>
        public static string LevelKey(string level)
        {
            if (string.IsNullOrWhiteSpace(level)) return "";
            var m = LevelNumber.Match(level);
            var key = m.Success ? (m.Groups[1].Length > 0 ? "-" : "") + m.Groups[2].Value + " " + m.Groups[3].Value : level;
            return Simplify(key);
        }

        /// <summary>
        /// Статус новой коллизии по Navisworks: «Исправлено» — «Исправлена» (точки в модели уже нет — то же, что нет в отчёте),
        /// остальное — «Активная». «Подтверждено» в Navisworks — тоже «Активная»: статусы ставит только сайт (решение
        /// разработчика 15.10.2026; до того — «Допущена»).
        /// </summary>
        public static ClashState StateOf(NavisworksStatus status) =>
            status == NavisworksStatus.Resolved ? ClashState.Fixed : ClashState.Active;

        public static string Title(ClashState state)
        {
            switch (state)
            {
                case ClashState.Fixed: return "Исправлена";
                case ClashState.Accepted: return "Допущена";
                default: return "Активная";
            }
        }

        /// <summary>Сколько метров в единице отчёта; неизвестная единица — 1 (метры).</summary>
        public static double MetersPerUnit(string units)
        {
            switch ((units ?? "").Trim().ToLowerInvariant())
            {
                case "mm": return 0.001;
                case "cm": return 0.01;
                case "dm": return 0.1;
                case "ft": return 0.3048;
                case "in": return 0.0254;
                default: return 1;
            }
        }

        /// <summary>Файл снимка коллизии: путь из XML — относительно папки отчёта; null — снимка нет в отчёте.</summary>
        public static string ImageFile(ClashReport report, ClashResult result)
        {
            if (string.IsNullOrEmpty(result.ImageHref) || string.IsNullOrEmpty(report.FilePath)) return null;
            var href = result.ImageHref.Replace('/', '\\');
            return Path.IsPathRooted(href) ? href : Path.Combine(Path.GetDirectoryName(report.FilePath) ?? "", href);
        }

        /// <summary>Нижний регистр, пробелы по одному, без пробелов по краям.</summary>
        private static string Simplify(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            var space = false;
            foreach (var c in s.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    space = true;
                    continue;
                }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
