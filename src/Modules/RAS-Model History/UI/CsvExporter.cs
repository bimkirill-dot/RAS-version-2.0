using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RASv2.ModelHistory.UI
{
    /// <summary>Экспорт в CSV (UTF-8 с BOM, разделитель «;» — открывается в Excel без настройки).</summary>
    public static class CsvExporter
    {
        private static readonly string[] Header =
        {
            "Время", "Действие", "Статус", "Автор", "Категория", "Тип объекта", "Имя", "Тип / семейство", "ID", "UniqueId",
            "Уровень", "Рабочий набор", "Вид-владелец", "Команда Revit", "Источник", "Создатель элемента",
            "Последним синхронизировал", "Компьютер", "Изменения параметров"
        };

        /// <param name="paramName">Параметр из столбца «значение параметра» (null — без столбца).</param>
        public static void Export(string path, IEnumerable<ChangeRow> rows, string paramName = null)
        {
            var sb = new StringBuilder();
            var header = string.IsNullOrEmpty(paramName) ? Header : Header.Concat(new[] { paramName }).ToArray();
            sb.AppendLine(string.Join(";", header.Select(Escape)));
            foreach (var r in rows)
            {
                var rec = r.Record;
                var parameters = rec.HasParams
                    ? string.Join(" | ", rec.Params.Select(p => $"{p.Name}: {Dash(p.Old)} → {Dash(p.New)}"))
                    : "";
                var cells = new[]
                {
                    r.TimeText, r.KindText, r.StatusText, r.AuthorText, rec.Category, r.GroupText, rec.Name, rec.TypeName,
                    r.IdText, rec.UniqueId, rec.Level, rec.Workset, rec.OwnerView, rec.Transaction, r.SourceText,
                    rec.Creator, rec.LastChangedBy, rec.Machine, parameters
                };
                if (!string.IsNullOrEmpty(paramName)) cells = cells.Concat(new[] { r.ParamValue }).ToArray();
                sb.AppendLine(string.Join(";", cells.Select(Escape)));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Dash(string s) => string.IsNullOrEmpty(s) ? "—" : s;

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // защита от формул в Excel
            if (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@') s = "'" + s;
            if (s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static string DefaultFileName(string modelTitle)
        {
            var safe = string.Concat((modelTitle ?? "model").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            return $"RAS-Model History_{safe}_{System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture)}.csv";
        }
    }
}
