using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace RASv2.ClashAnalysis.Reports
{
    /// <summary>
    /// Чтение отчёта Navisworks Clash Detective в формате XML (проверено на Navisworks Manage 2023, русский интерфейс).
    /// Необязательные поля (дата обнаружения, расстояние, оси) — если включены в настройках отчёта.
    /// Непонятное не роняет чтение: пропускается, причина — в warn.
    /// </summary>
    public static class ClashReportReader
    {
        /// <summary>Свойство с номером элемента Revit — названия в русском и английском Navisworks.</summary>
        private static readonly string[] ElementIdTags = { "Объект Id", "Объект ID", "Элемент Id", "Element ID", "Id" };
        private static readonly string[] ElementNameTags = { "Элемент Имя", "Item Name" };
        /// <summary>Файлы моделей в дереве Navisworks.</summary>
        private static readonly string[] ModelExtensions = { ".nwc", ".nwd", ".rvt", ".ifc", ".dwg", ".dgn", ".nwf" };

        /// <summary>Прочитать файл отчёта. Не XML или не отчёт Clash Detective — InvalidDataException с причиной по-русски.</summary>
        public static ClashReport Read(string path, Action<string> warn = null)
        {
            using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
                return Parse(reader, path, File.GetLastWriteTime(path), warn);
        }

        /// <summary>Разобрать отчёт; fileTime — дата отчёта, если в нём нет дат обнаружения.</summary>
        public static ClashReport Parse(TextReader text, string path, DateTime fileTime, Action<string> warn = null)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(text);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("Файл не читается как XML: " + ex.Message, ex);
            }
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "exchange" || !root.Descendants("clashtest").Any())
                throw new InvalidDataException("Это не отчёт Navisworks Clash Detective в формате XML (нет проверок «clashtest»).");

            var report = new ClashReport { FilePath = path, Units = (string)root.Attribute("units") ?? "m" };
            foreach (var t in root.Descendants("clashtest"))
                report.Tests.Add(ReadTest(t, warn));

            var created = report.Tests.SelectMany(t => t.Results).Where(r => r.Created.HasValue).Select(r => r.Created.Value).ToList();
            report.DateFromReport = created.Count > 0;
            report.Date = created.Count > 0 ? created.Max() : fileTime;
            return report;
        }

        private static ClashTest ReadTest(XElement t, Action<string> warn)
        {
            var test = new ClashTest
            {
                Name = ((string)t.Attribute("name"))?.Trim(),
                TestType = (string)t.Attribute("test_type"),
                Tolerance = Number((string)t.Attribute("tolerance")) ?? 0,
                LeftSet = SetName(t.Element("left")),
                RightSet = SetName(t.Element("right")),
            };
            var s = t.Element("summary");
            if (s != null)
            {
                test.Summary.Total = Int(s, "total");
                test.Summary.New = Int(s, "new");
                test.Summary.Active = Int(s, "active");
                test.Summary.Reviewed = Int(s, "reviewed");
                test.Summary.Approved = Int(s, "approved");
                test.Summary.Resolved = Int(s, "resolved");
            }
            // коллизии бывают и внутри групп Navisworks (clashgroup) — берём все
            foreach (var r in t.Descendants("clashresult"))
            {
                test.ResultsInFile++;
                var result = ReadResult(r);
                if (result.Status != NavisworksStatus.Resolved) test.OpenInFile++;
                if (result.Elements.Count == 0)
                    warn?.Invoke("Проверка «" + test.Name + "», «" + result.Name + "»: нет элементов — пропущена.");
                else
                    test.Results.Add(result);
            }
            return test;
        }

        private static ClashResult ReadResult(XElement r)
        {
            var group = r.Ancestors("clashgroup").FirstOrDefault();
            var result = new ClashResult
            {
                Name = (string)r.Attribute("name"),
                Guid = ((string)r.Attribute("guid"))?.Trim().ToLowerInvariant(),
                Status = Status((string)r.Attribute("status")),
                ImageHref = NullIfEmpty((string)r.Attribute("href")),
                GroupName = group == null ? null : (string)group.Attribute("name"),
                Distance = Number((string)r.Attribute("distance") ?? (string)r.Element("distance")),
                Created = Date(r.Element("createddate")?.Element("date")),
                GridLocation = NullIfEmpty(((string)r.Element("gridlocation"))?.Trim()),
                Description = NullIfEmpty(((string)r.Element("description"))?.Trim()),
                AssignedTo = NullIfEmpty(((string)r.Element("assignedto"))?.Trim()),
            };
            var pos = r.Element("clashpoint")?.Element("pos3f");
            if (pos != null)
                result.Point = new ClashPoint
                {
                    X = Number((string)pos.Attribute("x")) ?? 0,
                    Y = Number((string)pos.Attribute("y")) ?? 0,
                    Z = Number((string)pos.Attribute("z")) ?? 0,
                };
            foreach (var o in r.Elements("clashobjects").Elements("clashobject"))
                result.Elements.Add(ReadElement(o));
            return result;
        }

        private static ClashElement ReadElement(XElement o)
        {
            var e = new ClashElement();
            e.Path.AddRange(o.Elements("pathlink").Elements("node").Select(n => ((string)n)?.Trim() ?? ""));
            // путь: …, файл модели, уровень, категория, семейство, тип, …
            var file = e.Path.FindIndex(n => IsModelExtension(Path.GetExtension(n)));
            if (file < 0 && e.Path.Count > 2) file = 2;
            if (file >= 0)
            {
                e.ModelFile = At(e.Path, file);
                e.Level = At(e.Path, file + 1);
                e.Category = At(e.Path, file + 2);
                e.Family = At(e.Path, file + 3);
                e.Type = At(e.Path, file + 4);
            }
            foreach (var tag in o.Elements("smarttags").Elements("smarttag"))
            {
                var name = ((string)tag.Element("name"))?.Trim();
                var value = ((string)tag.Element("value"))?.Trim();
                if (name == null || string.IsNullOrEmpty(value)) continue;
                if (ElementIdTags.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    e.ElementId = id;
                else if (ElementNameTags.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                    e.Name = value;
            }
            return e;
        }

        /// <summary>Расширение файла модели («.nwc», «.rvt»…), без учёта регистра.</summary>
        internal static bool IsModelExtension(string ext) =>
            !string.IsNullOrEmpty(ext) && ModelExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));

        private static string SetName(XElement side)
        {
            var locator = ((string)side?.Descendants("locator").FirstOrDefault())?.Trim();
            if (string.IsNullOrEmpty(locator)) return null;
            const string prefix = "lcop_selection_set_tree/";
            return locator.StartsWith(prefix, StringComparison.Ordinal) ? locator.Substring(prefix.Length) : locator;
        }

        internal static NavisworksStatus Status(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "new": return NavisworksStatus.New;
                case "active": return NavisworksStatus.Active;
                case "reviewed": return NavisworksStatus.Reviewed;
                case "approved": return NavisworksStatus.Approved;
                case "resolved": return NavisworksStatus.Resolved;
                default: return NavisworksStatus.Unknown;
            }
        }

        /// <summary>&lt;date year month day hour minute second/&gt;; неполная или неверная — null.</summary>
        private static DateTime? Date(XElement d)
        {
            if (d == null) return null;
            int Part(string name) => int.TryParse((string)d.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
            int y = Part("year"), mo = Part("month"), day = Part("day");
            int h = Math.Max(Part("hour"), 0), mi = Math.Max(Part("minute"), 0), sec = Math.Max(Part("second"), 0);
            if (y < 1 || mo < 1 || mo > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Min(y, 9999), mo)) return null;
            if (h > 23 || mi > 59 || sec > 59) return null;
            return new DateTime(y, mo, day, h, mi, sec);
        }

        private static double? Number(string s) =>
            double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

        private static int Int(XElement e, string name) =>
            int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static string At(List<string> list, int i) => i < list.Count ? NullIfEmpty(list[i]) : null;

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
