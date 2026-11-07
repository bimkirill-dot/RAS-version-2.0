using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Чем кончилась NWC или NWF в запуске.</summary>
    public enum ItemResult { Done, Failed, Skipped }

    /// <summary>Ошибка запуска: какая NWC / NWF и почему.</summary>
    [DataContract]
    public sealed class RunIssue
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Path { get; set; }
        [DataMember(Order = 3)] public string Note { get; set; }
    }

    /// <summary>Последний запуск строки (▷ у неё или у строки выше): когда, кто, сколько, ошибки, пропуски и несостыковки.</summary>
    [DataContract]
    public sealed class RowRun
    {
        [DataMember(Order = 1)] public string Key { get; set; }
        [DataMember(Order = 2)] public DateTime At { get; set; } = DateTime.Now;
        [DataMember(Order = 3)] public double Seconds { get; set; }
        [DataMember(Order = 4)] public string By { get; set; }
        [DataMember(Order = 5)] public int Done { get; set; }
        [DataMember(Order = 6)] public int Skipped { get; set; }
        [DataMember(Order = 7)] public List<RunIssue> Errors { get; set; } = new List<RunIssue>();
        [DataMember(Order = 8)] public List<string> Notes { get; set; } = new List<string>();
        /// <summary>Журнал запуска — копия в папке задачи (видна всем), не вышло — у запускавшего.</summary>
        [DataMember(Order = 9)] public string Journal { get; set; }
    }

    /// <summary>Сколько шла NWC / NWF в последний раз — для «Времени» строки.</summary>
    [DataContract]
    public sealed class ItemTime
    {
        [DataMember(Order = 1)] public string Path { get; set; }
        [DataMember(Order = 2)] public double Seconds { get; set; }
        [DataMember(Order = 3)] public DateTime At { get; set; } = DateTime.Now;
    }

    [DataContract]
    public sealed class RunStateFile
    {
        [DataMember(Order = 1)] public List<RowRun> Rows { get; set; } = new List<RowRun>();
        [DataMember(Order = 2)] public List<ItemTime> Items { get; set; } = new List<ItemTime>();
    }

    /// <summary>Модель строки для жёлтого «модель изменилась»: дата модели (null — не узнали) и дата её NWC (null — NWC нет).</summary>
    public struct ModelCheck
    {
        public string Name;
        public DateTime? Model;
        public bool Answered;
        public DateTime? Nwc;
    }

    /// <summary>
    /// Состояние строк «Задач выгрузки» в списке (DECISIONS.md модуля, «Статус, запуск, время — в списке задач»): итог последнего
    /// запуска каждой строки и время каждой NWC / NWF — файлом в папке задачи («Журналы выгрузки\состояние.json»), его видят все,
    /// кто выгружает эту задачу. Без Revit — проверяется тестами.
    /// </summary>
    public static class RunStates
    {
        public const string FileName = "состояние.json";

        /// <summary>Файл состояния задачи; папки задачи нет — null (состояние не хранится).</summary>
        public static string PathOf(ExportTask task)
        {
            var folder = ExportNodes.FolderOf(ExportNodes.Root(task));
            return NwcRunPlan.IsRooted(folder) ? Path.Combine(folder, "Журналы выгрузки", FileName) : null;
        }

        /// <summary>Строка — по названиям от задачи («ArmanQala › Высокая критичность › S1_ВК»): переименовали — состояние с нуля.</summary>
        public static string Key(ExportTask task)
        {
            var names = new List<string>();
            for (var t = task; t != null; t = t.Parent) names.Insert(0, (t.Name ?? "").Trim());
            return string.Join(" › ", names);
        }

        /// <summary>Файла нет или не читается — пусто.</summary>
        public static RunStateFile Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new RunStateFile();
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var f = (RunStateFile)new DataContractJsonSerializer(typeof(RunStateFile)).ReadObject(s);
                    f.Rows = f.Rows ?? new List<RowRun>();
                    f.Items = f.Items ?? new List<ItemTime>();
                    foreach (var r in f.Rows)
                    {
                        r.Errors = r.Errors ?? new List<RunIssue>();
                        r.Notes = r.Notes ?? new List<string>();
                    }
                    return f;
                }
            }
            catch (Exception)
            {
                return new RunStateFile();
            }
        }

        /// <summary>Записать поверх того, что в файле сейчас (коллега мог выгрузить другую строку): строки — по ключу, NWC — по пути.</summary>
        public static void Save(string path, IEnumerable<RowRun> rows, IEnumerable<ItemTime> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var file = Load(path);
            foreach (var r in rows)
            {
                file.Rows.RemoveAll(x => string.Equals(x.Key, r.Key, StringComparison.OrdinalIgnoreCase));
                file.Rows.Add(r);
            }
            foreach (var i in items)
            {
                file.Items.RemoveAll(x => string.Equals(x.Path, i.Path, StringComparison.OrdinalIgnoreCase));
                file.Items.Add(i);
            }
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
            using (var s = File.Create(tmp))
                new DataContractJsonSerializer(typeof(RunStateFile)).WriteObject(s, file);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    // коллега читает файл прямо сейчас — подождать
                    Thread.Sleep(200);
                }
            }
        }

        public static RowRun Find(RunStateFile file, ExportTask task) =>
            file?.Rows.FirstOrDefault(r => string.Equals(r.Key, Key(task), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Итог строки после запуска: её NWC и NWF (paths) из того, что прошло (items). retried — «Повторить ошибки»: ошибки, которые не
        /// повторяли, остаются из прошлого раза (previous).
        /// </summary>
        public static RowRun Record(ExportTask row, ICollection<string> paths, IEnumerable<(string Path, string Name, ItemResult Result, string Note)> items,
            DateTime at, double seconds, string journal, IEnumerable<string> notes, ICollection<string> retried = null, RowRun previous = null)
        {
            var mine = items.Where(i => paths.Contains(i.Path)).ToList();
            var run = new RowRun
            {
                Key = Key(row), At = at, Seconds = seconds, By = Environment.UserName, Journal = journal,
                Done = mine.Count(i => i.Result == ItemResult.Done), Skipped = mine.Count(i => i.Result == ItemResult.Skipped),
                Errors = mine.Where(i => i.Result == ItemResult.Failed).Select(i => new RunIssue { Name = i.Name, Path = i.Path, Note = i.Note }).ToList(),
                Notes = mine.Where(i => i.Result == ItemResult.Skipped && !string.IsNullOrWhiteSpace(i.Note)).Select(i => i.Name + " — " + i.Note)
                    .Concat(notes ?? Enumerable.Empty<string>()).ToList(),
            };
            if (retried != null && previous != null)
            {
                run.Errors.AddRange(previous.Errors.Where(e => !retried.Contains(e.Path)));
                run.Notes = previous.Notes.Concat(run.Notes).Distinct().ToList();
                run.Done += previous.Done;
                run.Seconds = previous.Seconds;
            }
            return run;
        }

        /// <summary>«Время» строки: сумма последних времён её NWC и NWF; ни одной не выгружали — null. unknown — сколько без времени.</summary>
        public static double? Estimate(IEnumerable<string> paths, RunStateFile file, out int unknown)
        {
            unknown = 0;
            double sum = 0;
            var known = 0;
            foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var t = file?.Items.FirstOrDefault(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase));
                if (t == null) unknown++;
                else
                {
                    sum += t.Seconds;
                    known++;
                }
            }
            return known == 0 ? (double?)null : sum;
        }

        /// <summary>«≈ 12 мин», «≈ 1 ч 20 мин»; нет — «—».</summary>
        public static string EstimateText(double? seconds)
        {
            if (seconds == null) return "—";
            var minutes = Math.Max(1, (int)Math.Round(seconds.Value / 60));
            return "≈ " + (minutes < 60 ? minutes + " мин" : minutes / 60 + " ч" + (minutes % 60 > 0 ? " " + minutes % 60 + " мин" : ""));
        }

        /// <summary>Идёт: «4:05», «1:02:10».</summary>
        public static string Elapsed(TimeSpan t) =>
            t.TotalHours >= 1 ? ((int)t.TotalHours) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : ((int)t.TotalMinutes) + t.ToString(@"\:ss", CultureInfo.InvariantCulture);

        /// <summary>
        /// Жёлтый «модель изменилась»: модели, изменённые позже своей NWC (NWC нет — не считается: строку ещё не выгружали). unknown —
        /// есть NWC, а дату модели не узнали (Revit Server не ответил) — серый «?».
        /// </summary>
        public static List<string> Changed(IEnumerable<ModelCheck> models, out bool unknown)
        {
            unknown = false;
            var changed = new List<string>();
            foreach (var m in models)
            {
                if (m.Nwc == null) continue;
                if (!m.Answered || m.Model == null)
                {
                    unknown = true;
                    continue;
                }
                // минута — запас: NWC пишется через секунды после того, как модель скопирована
                if (m.Model.Value > m.Nwc.Value.AddMinutes(1)) changed.Add(m.Name);
            }
            return changed;
        }

        /// <summary>Подсказка к статусу: когда и кто, ошибки, пропуски и несостыковки, изменившиеся модели.</summary>
        public static string Tip(RowRun run, IList<string> changed, bool unknown)
        {
            var lines = new List<string>();
            if (run != null)
            {
                lines.Add($"Запуск {run.At:dd.MM.yyyy HH:mm}, {run.By}" + (run.Seconds > 0 ? ", " + NwcHistory.Duration(run.Seconds) : "") +
                          $": выгружено {run.Done}, с ошибкой {run.Errors.Count}, пропущено {run.Skipped}");
                if (run.Errors.Count > 0)
                {
                    lines.Add("Ошибки:");
                    lines.AddRange(run.Errors.Take(12).Select(e => "  • " + e.Name + " — " + e.Note));
                    if (run.Errors.Count > 12) lines.Add($"  … ещё {run.Errors.Count - 12}");
                }
                if (run.Notes.Count > 0)
                {
                    lines.Add("Пропуски и несостыковки:");
                    lines.AddRange(run.Notes.Take(12).Select(n => "  • " + n));
                    if (run.Notes.Count > 12) lines.Add($"  … ещё {run.Notes.Count - 12}");
                }
            }
            if (changed.Count > 0)
            {
                lines.Add("Изменились после выгрузки:");
                lines.AddRange(changed.Take(12).Select(n => "  • " + n));
                if (changed.Count > 12) lines.Add($"  … ещё {changed.Count - 12}");
            }
            if (unknown) lines.Add("Дату части моделей Revit Server не сообщил — изменились ли они, не известно.");
            if (run != null) lines.Add("Щелчок — журнал запуска");
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }
}
