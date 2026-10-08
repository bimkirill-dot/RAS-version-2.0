using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Reports
{
    /// <summary>Наш статус коллизии (DECISIONS.md, «Статусы»).</summary>
    public enum ClashState { Active, Fixed, Accepted }

    /// <summary>
    /// Правила над отчётом: что считать той же коллизией в следующем прогоне, тем же уровнем, каким статусом.
    /// Без Revit и без базы — общие для сайта и моста; проверяются тестами.
    /// </summary>
    public static class ClashRules
    {
        /// <summary>
        /// «Личность» коллизии — одна через все прогоны: постоянный номер Navisworks (guid), а без него —
        /// проверка + пара элементов «файл модели + номер» (порядок элементов не важен).
        /// </summary>
        public static string IdentityKey(string testName, ClashResult result)
        {
            if (!string.IsNullOrEmpty(result.Guid)) return "guid:" + result.Guid;
            var pair = result.Elements.Select(ElementKey).OrderBy(k => k, StringComparer.Ordinal);
            return "pair:" + Simplify(testName) + "|" + string.Join("|", pair);
        }

        /// <summary>Элемент — «файл модели + номер»: номер уникален только внутри своего файла.</summary>
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

        /// <summary>Статус Navisworks → наш: «Подтверждено» — «Допущена», «Исправлено» — «Исправлена», остальное — «Активная».</summary>
        public static ClashState StateOf(NavisworksStatus status)
        {
            switch (status)
            {
                case NavisworksStatus.Approved: return ClashState.Accepted;
                case NavisworksStatus.Resolved: return ClashState.Fixed;
                default: return ClashState.Active;
            }
        }

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
