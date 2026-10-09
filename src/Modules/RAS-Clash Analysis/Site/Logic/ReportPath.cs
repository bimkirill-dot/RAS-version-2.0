using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>Где лежит отчёт внутри папки проекта: критичность, блок, дата прогона (из названий папок).</summary>
    public sealed class ReportPlace
    {
        /// <summary>«Высокая», «Средняя», «Низкая»; "" — не определена.</summary>
        public string Criticality { get; set; } = "";
        /// <summary>«S1», «Блок 2», «Секция 3», «Корпус 1»; "" — не определён.</summary>
        public string Block { get; set; } = "";
        /// <summary>Дата прогона из названия папки («08.10.2026»); null — нет.</summary>
        public DateTime? RunDate { get; set; }
    }

    /// <summary>
    /// Понимание папок с отчётами по их названиям — в любом порядке вложенности (DECISIONS.md модуля, «Папки отчётов»):
    /// «Арман Кала\08.10.2026\Высокая критичность\XML\S1\отчёт.xml», «Высокая\S08\XML\отчёт.xml» — одинаково понятны.
    /// Папки форматов (XML, HTML) и прочие — не важны. Без базы — проверяется тестами.
    /// </summary>
    public static class ReportPath
    {
        public const string DateFromXml = "xml";
        public const string DateFromFolder = "folder";
        public const string DateFromFile = "file";

        private static readonly Regex BlockS = new Regex(@"^\s*[SС][\s\-_]*0*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BlockWord = new Regex(@"^\s*(блок|секция|корпус)[\s\-_№]*0*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DateDmy = new Regex(@"(?<!\d)(\d{1,2})[.\-_](\d{1,2})[.\-_](\d{4}|\d{2})(?!\d)", RegexOptions.CultureInvariant);
        private static readonly Regex DateYmd = new Regex(@"(?<!\d)(\d{4})[.\-_](\d{1,2})[.\-_](\d{1,2})(?!\d)", RegexOptions.CultureInvariant);

        /// <summary>Разобрать путь отчёта относительно папки проекта; ближайшая к файлу подходящая папка главнее.</summary>
        public static ReportPlace Parse(string projectFolder, string filePath)
        {
            var place = new ReportPlace();
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var root = (projectFolder ?? "").TrimEnd('\\', '/');
            var relative = root.Length > 0 && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? dir.Substring(root.Length) : dir;
            var segments = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in segments)
            {
                var c = Criticality(s);
                if (c != null) place.Criticality = c;
                var b = Block(s);
                if (b != null) place.Block = b;
                var d = Date(s);
                if (d != null) place.RunDate = d;
            }
            return place;
        }

        /// <summary>
        /// «Высокая критичность», «высокая», «Высокий приоритет» → «Высокая»; не критичность — null.
        /// Терпит опечатку «Срдняя» (так написано в сообщении разработчика 09.10.2026 — может быть и в названии папки).
        /// </summary>
        public static string Criticality(string segment)
        {
            var s = (segment ?? "").ToLowerInvariant();
            if (s.Contains("высок")) return "Высокая";
            if (s.Contains("сред") || s.Contains("срдн")) return "Средняя";
            if (s.Contains("низк")) return "Низкая";
            return null;
        }

        /// <summary>«S1», «S08», «С 2» (русская С) → «S1», «S8», «S2»; «Блок 02» → «Блок 2»; не блок — null.</summary>
        public static string Block(string segment)
        {
            var m = BlockS.Match(segment ?? "");
            if (m.Success) return "S" + m.Groups[1].Value;
            m = BlockWord.Match(segment ?? "");
            if (m.Success)
            {
                var word = m.Groups[1].Value.ToLowerInvariant();
                return char.ToUpperInvariant(word[0]) + word.Substring(1) + " " + m.Groups[2].Value;
            }
            return null;
        }

        /// <summary>Дата в названии папки: «08.10.2026», «8.10.26», «2026-10-08», «Прогон 08.10.2026»; нет — null.</summary>
        public static DateTime? Date(string segment)
        {
            var s = segment ?? "";
            var m = DateYmd.Match(s);
            if (m.Success && TryDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, out var ymd)) return ymd;
            m = DateDmy.Match(s);
            if (m.Success)
            {
                var year = m.Groups[3].Value.Length == 2 ? "20" + m.Groups[3].Value : m.Groups[3].Value;
                if (TryDate(year, m.Groups[2].Value, m.Groups[1].Value, out var dmy)) return dmy;
            }
            return null;
        }

        /// <summary>
        /// Дата отчёта: из самого отчёта (даты обнаружения коллизий) → из папки прогона (с временем файла — для порядка
        /// внутри дня) → время файла. source — откуда взята (DateFromXml / DateFromFolder / DateFromFile).
        /// </summary>
        public static DateTime ReportDate(ClashReport report, DateTime fileTime, DateTime? runDate, out string source)
        {
            if (report.DateFromReport)
            {
                source = DateFromXml;
                return report.Date;
            }
            if (runDate.HasValue)
            {
                source = DateFromFolder;
                return runDate.Value.Date + fileTime.TimeOfDay;
            }
            source = DateFromFile;
            return fileTime;
        }

        /// <summary>Порядок критичностей: высокая, средняя, низкая, не определена.</summary>
        public static int CriticalityOrder(string c) => c == "Высокая" ? 0 : c == "Средняя" ? 1 : c == "Низкая" ? 2 : 3;

        /// <summary>Порядок блоков: по номеру; не определён — в конце.</summary>
        public static int BlockOrder(string b)
        {
            var digits = new string((b ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
        }

        public static string CriticalityTitle(string c) => string.IsNullOrEmpty(c) ? "Без критичности" : c + " критичность";

        public static string BlockTitle(string b) => string.IsNullOrEmpty(b) ? "Без блока" : b;

        private static bool TryDate(string y, string m, string d, out DateTime date)
        {
            date = default;
            if (!int.TryParse(y, out var yy) || !int.TryParse(m, out var mm) || !int.TryParse(d, out var dd)) return false;
            if (yy < 2000 || yy > 2100 || mm < 1 || mm > 12 || dd < 1 || dd > DateTime.DaysInMonth(yy, mm)) return false;
            date = new DateTime(yy, mm, dd);
            return true;
        }
    }
}
