using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Строка для окна «Предупреждения»: её последний запуск, её NWC и NWF сейчас, изменившиеся модели.</summary>
    public sealed class WarningSource
    {
        public ExportTask Task { get; set; }
        public RowRun Run { get; set; }
        /// <summary>NWC и NWF строки (как у ▷) — чей запуск про файл свежее.</summary>
        public ICollection<string> Paths { get; set; } = new List<string>();
        /// <summary>Модели, изменённые после своей NWC (имена).</summary>
        public IList<string> Changed { get; set; } = new List<string>();
        /// <summary>Дату части моделей Revit Server не сообщил.</summary>
        public bool Unknown { get; set; }
    }

    /// <summary>Одна строка предупреждения: ошибка, изменение или пропуск.</summary>
    public sealed class WarningLine
    {
        /// <summary>Функция у ошибки: «NWC», «NWF», «RVT», «Пересохранение».</summary>
        public string Kind { get; set; } = "";
        /// <summary>Коротко: блок и отдел («S01 KJ»), не нашлись — имя целиком; у пропуска — имена через запятую.</summary>
        public string Name { get; set; } = "";
        /// <summary>Имя целиком (имена) — в подсказке.</summary>
        public string Full { get; set; } = "";
        /// <summary>Причина — первая фраза.</summary>
        public string Text { get; set; } = "";
        /// <summary>Причина целиком — в подсказке.</summary>
        public string Detail { get; set; } = "";
        public string Path { get; set; }
    }

    /// <summary>Предупреждения одной строки.</summary>
    public sealed class WarningGroup
    {
        public ExportTask Task { get; set; }
        public RowRun Run { get; set; }
        public List<WarningLine> Errors { get; } = new List<WarningLine>();
        public List<WarningLine> Changes { get; } = new List<WarningLine>();
        public List<WarningLine> Skips { get; } = new List<WarningLine>();
        public bool Unknown { get; set; }
        /// <summary>Что требует внимания — ошибки и изменения (число на кнопке ⚠).</summary>
        public int Count => Errors.Count + Changes.Count;
        public bool Empty => Errors.Count == 0 && Changes.Count == 0 && Skips.Count == 0 && !Unknown;
    }

    /// <summary>
    /// Окно «Предупреждения» (разработчик, 12.11.2026: «кнопка по общим предупреждениям, сортировка по задачам — нормально,
    /// читаемо, понятно; нажимаешь на задачу — предупреждения по ней»). Запуск строки записывается у неё и у всех подзадач, поэтому
    /// одно и то же — у нескольких строк: каждое предупреждение — у одной строки. Ошибка файла — у строки, чей запуск про этот
    /// файл самый свежий (равны — у нижней); у свежего запуска файл прошёл — ошибки нет нигде. Изменившаяся модель — у нижних
    /// строк с ней. Пропуски — по тексту, так же; одинаковые причины — одной строкой.
    /// </summary>
    public static class RunWarnings
    {
        public static Dictionary<ExportTask, WarningGroup> Build(IList<WarningSource> sources, ExportSettings common)
        {
            var groups = new Dictionary<ExportTask, WarningGroup>();
            WarningGroup Of(WarningSource s)
            {
                if (!groups.TryGetValue(s.Task, out var g)) groups[s.Task] = g = new WarningGroup { Task = s.Task, Run = s.Run };
                return g;
            }
            var ran = sources.Where(s => s.Run != null).ToList();

            foreach (var s in ran)
            {
                foreach (var e in s.Run.Errors)
                {
                    var owner = Latest(ran.Where(x => x == s || x.Paths.Any(p => SamePath(p, e.Path)) || x.Run.Errors.Any(y => SamePath(y.Path, e.Path))));
                    if (owner != s) continue;
                    Of(s).Errors.Add(new WarningLine
                    {
                        Kind = RunStates.KindTitle(RunStates.KindOf(e.Path)), Name = ShortName(e.Name, common), Full = e.Name ?? "",
                        Text = Reason(e.Note), Detail = e.Note ?? "", Path = e.Path,
                    });
                }
                var skips = new List<string>();
                foreach (var n in s.Run.Notes.Distinct())
                    if (Latest(ran.Where(x => x.Run.Notes.Contains(n))) == s) skips.Add(n);
                Of(s).Skips.AddRange(GroupSkips(skips, common));
            }

            foreach (var s in sources)
            {
                var below = sources.Where(x => x != s && IsBelow(x.Task, s.Task)).ToList();
                foreach (var name in s.Changed.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (below.Any(x => x.Changed.Contains(name, StringComparer.OrdinalIgnoreCase))) continue;
                    Of(s).Changes.Add(new WarningLine { Name = ShortName(name, common), Full = name, Text = "изменилась после выгрузки NWC" });
                }
                if (s.Unknown && !below.Any(x => x.Unknown)) Of(s).Unknown = true;
            }

            foreach (var key in groups.Where(g => g.Value.Empty).Select(g => g.Key).ToList()) groups.Remove(key);
            return groups;
        }

        /// <summary>Число на кнопке ⚠: ошибки и изменившиеся модели (модель у двух строк — один раз).</summary>
        public static int Total(Dictionary<ExportTask, WarningGroup> groups) =>
            groups.Values.Sum(g => g.Errors.Count) +
            groups.Values.SelectMany(g => g.Changes.Select(c => c.Full)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        /// <summary>Сколько у строки и всех её подзадач.</summary>
        public static int CountUnder(ExportTask task, Dictionary<ExportTask, WarningGroup> groups) =>
            task.Self().Sum(t => groups.TryGetValue(t, out var g) ? g.Count : 0);

        /// <summary>Коротко: блок и отдел из имени («S01 KJ»); не нашлось ни того, ни другого — имя целиком.</summary>
        public static string ShortName(string name, ExportSettings common)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (name.EndsWith(".nwf", StringComparison.OrdinalIgnoreCase)) return name;
            var block = ExportNames.Block(name, common.BlockMasks);
            var department = ExportNames.Department(name, common.Departments.Select(d => d.Department));
            var s = (block + " " + department).Trim();
            return s.Length == 0 || block.Length == 0 || department.Length == 0 ? name : s;
        }

        /// <summary>Причина — первая фраза: до «. » или «; » (точки в датах — «11.11.2026» — не конец), не длиннее 140 знаков.</summary>
        public static string Reason(string note)
        {
            var s = (note ?? "").Trim();
            var m = Regex.Match(s, @"[.;]\s");
            if (m.Success) s = s.Substring(0, m.Index);
            s = s.TrimEnd('.', ';', ' ');
            return s.Length > 140 ? s.Substring(0, 139) + "…" : s;
        }

        /// <summary>Пропуски «имя — причина»: одинаковые причины — одной строкой, имена через запятую; без имени — как есть.</summary>
        public static List<WarningLine> GroupSkips(IEnumerable<string> notes, ExportSettings common)
        {
            var lines = new List<WarningLine>();
            var byReason = new Dictionary<string, WarningLine>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<WarningLine, List<string>>();
            foreach (var n in notes)
            {
                var at = n.IndexOf(" — ", StringComparison.Ordinal);
                if (at <= 0)
                {
                    lines.Add(new WarningLine { Text = Reason(n), Detail = n });
                    continue;
                }
                var name = n.Substring(0, at).Trim();
                var detail = n.Substring(at + 3).Trim();
                var reason = Reason(detail);
                if (!byReason.TryGetValue(reason, out var line))
                {
                    byReason[reason] = line = new WarningLine { Text = reason, Detail = detail };
                    counts[line] = new List<string>();
                    lines.Add(line);
                }
                counts[line].Add(name);
            }
            foreach (var c in counts)
            {
                var names = c.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                c.Key.Name = string.Join(", ", names.Select(x => ShortName(x, common)));
                c.Key.Full = string.Join("\n", names);
            }
            return lines;
        }

        // самый свежий запуск; равны — у нижней строки
        private static WarningSource Latest(IEnumerable<WarningSource> candidates) =>
            candidates.OrderByDescending(x => x.Run.At).ThenByDescending(x => Level(x.Task)).FirstOrDefault();

        private static int Level(ExportTask t)
        {
            var n = 0;
            for (var p = t.Parent; p != null; p = p.Parent) n++;
            return n;
        }

        private static bool IsBelow(ExportTask task, ExportTask above)
        {
            for (var p = task.Parent; p != null; p = p.Parent)
                if (p == above) return true;
            return false;
        }

        private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
