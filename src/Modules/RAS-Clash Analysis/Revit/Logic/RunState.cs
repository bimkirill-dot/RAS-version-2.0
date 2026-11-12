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

    /// <summary>Итог одной функции в запуске строки (разработчик, 07.11.2026: «статус чего — NWC, NWF, отчётов?»): «NWC», «NWF»…</summary>
    [DataContract]
    public sealed class StepRun
    {
        [DataMember(Order = 1)] public string Kind { get; set; }
        [DataMember(Order = 2)] public int Done { get; set; }
        [DataMember(Order = 3)] public int Failed { get; set; }
        [DataMember(Order = 4)] public int Skipped { get; set; }
        /// <summary>Сколько шли её NWC / NWF (сумма, без копий наперёд).</summary>
        [DataMember(Order = 5)] public double Seconds { get; set; }
    }

    /// <summary>Чем кончилась функция в последнем запуске: не было / без ошибок / с ошибками.</summary>
    public enum StepMark { None, Ok, Errors }

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
        /// <summary>По функциям — для значка рядом с её галочкой; запуски до 07.11.2026 — без них (StepOf считает по ошибкам).</summary>
        [DataMember(Order = 10)] public List<StepRun> Steps { get; set; } = new List<StepRun>();
    }

    /// <summary>Сколько шла NWC / NWF в последний раз — для «Времени» строки.</summary>
    [DataContract]
    public sealed class ItemTime
    {
        [DataMember(Order = 1)] public string Path { get; set; }
        [DataMember(Order = 2)] public double Seconds { get; set; }
        [DataMember(Order = 3)] public DateTime At { get; set; } = DateTime.Now;
    }

    /// <summary>С какими файлами проверок собрана NWF: подпись (содержимое файлов) — заметить, что файл сменился.</summary>
    [DataContract]
    public sealed class NwfTests
    {
        [DataMember(Order = 1)] public string Nwf { get; set; }
        [DataMember(Order = 2)] public string Signature { get; set; }
        [DataMember(Order = 3)] public List<string> Files { get; set; } = new List<string>();
        [DataMember(Order = 4)] public DateTime At { get; set; } = DateTime.Now;
    }

    [DataContract]
    public sealed class RunStateFile
    {
        [DataMember(Order = 1)] public List<RowRun> Rows { get; set; } = new List<RowRun>();
        [DataMember(Order = 2)] public List<ItemTime> Items { get; set; } = new List<ItemTime>();
        [DataMember(Order = 3)] public List<NwfTests> Tests { get; set; } = new List<NwfTests>();
        /// <summary>Готовые NWC подрядчика: что скопировано в папку NWC последним (12.11.2026, ReadyNwc).</summary>
        [DataMember(Order = 4)] public List<ReadyCopy> Ready { get; set; } = new List<ReadyCopy>();
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

        /// <summary>Состояние задач без папки — на этом компьютере (по названию задачи).</summary>
        public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAS_v_2.0", "RAS-Clash Analysis", "Выгрузка NWC", "Состояние");

        /// <summary>
        /// Файл состояния задачи: в её папке; папки нет (разработчик, 09.11.2026: «задачу можно создать и вовсе без папки — для
        /// пересохранения папки не нужны») — на этом компьютере, по названию задачи; без названия — null (не хранится).
        /// </summary>
        public static string PathOf(ExportTask task)
        {
            var root = ExportNodes.Root(task);
            var folder = ExportNodes.FolderOf(root);
            if (NwcRunPlan.IsRooted(folder)) return Path.Combine(folder, "Журналы выгрузки", FileName);
            var bad = Path.GetInvalidFileNameChars();
            var name = new string((root.Name ?? "").Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return name.Length == 0 ? null : Path.Combine(LocalDir, name + ".json");
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
                    f.Tests = f.Tests ?? new List<NwfTests>();
                    f.Ready = f.Ready ?? new List<ReadyCopy>();
                    foreach (var r in f.Rows)
                    {
                        r.Errors = r.Errors ?? new List<RunIssue>();
                        r.Notes = r.Notes ?? new List<string>();
                        r.Steps = r.Steps ?? new List<StepRun>();
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
        public static void Save(string path, IEnumerable<RowRun> rows, IEnumerable<ItemTime> items, IEnumerable<NwfTests> tests = null,
            IEnumerable<ReadyCopy> ready = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var file = Load(path);
            foreach (var r in ready ?? Enumerable.Empty<ReadyCopy>())
            {
                file.Ready.RemoveAll(x => string.Equals(x.Target, r.Target, StringComparison.OrdinalIgnoreCase));
                file.Ready.Add(r);
            }
            foreach (var t in tests ?? Enumerable.Empty<NwfTests>())
            {
                file.Tests.RemoveAll(x => string.Equals(x.Nwf, t.Nwf, StringComparison.OrdinalIgnoreCase));
                file.Tests.Add(t);
            }
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

        /// <summary>
        /// Подпись файлов проверок — по их содержимому (файл поправили на месте — подпись другая) и порядку; файла нет — его имя. Пусто —
        /// пустая строка.
        /// </summary>
        public static string TestsSignature(IEnumerable<string> files, Func<string, byte[]> read = null)
        {
            read = read ?? (f => File.Exists(f) ? File.ReadAllBytes(f) : null);
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var parts = new List<string>();
                foreach (var f in files.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    byte[] data;
                    try { data = read(f); } catch (Exception) { data = null; }
                    parts.Add(data == null ? "нет:" + ExportNodes.FileName(f).ToLowerInvariant() : BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""));
                }
                return string.Join("|", parts);
            }
        }

        /// <summary>
        /// Файл проверок строки сменился с прошлой сборки этой NWF (разработчик, 08.11.2026: «окно с предупреждением, что файл проверок
        /// изменился, — да или нет»): прошлая подпись есть и другая. Не собирали с проверками — нет (спрашивать не о чем).
        /// </summary>
        public static NwfTests TestsChanged(RunStateFile file, string nwf, string signature) =>
            file?.Tests.FirstOrDefault(x => string.Equals(x.Nwf, nwf, StringComparison.OrdinalIgnoreCase) && x.Signature != signature);

        public static RowRun Find(RunStateFile file, ExportTask task) =>
            file?.Rows.FirstOrDefault(r => string.Equals(r.Key, Key(task), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Итог строки после запуска: её NWC и NWF (paths) из того, что прошло (items). retried — «Повторить ошибки»: ошибки, которые не
        /// повторяли, остаются из прошлого раза (previous).
        /// </summary>
        public static RowRun Record(ExportTask row, ICollection<string> paths, IEnumerable<(string Path, string Name, ItemResult Result, string Note)> items,
            DateTime at, double seconds, string journal, IEnumerable<string> notes, ICollection<string> retried = null, RowRun previous = null,
            IDictionary<string, double> times = null)
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
            run.Steps = mine.GroupBy(i => KindOf(i.Path)).Select(g => new StepRun
            {
                Kind = g.Key, Done = g.Count(i => i.Result == ItemResult.Done), Failed = g.Count(i => i.Result == ItemResult.Failed),
                Skipped = g.Count(i => i.Result == ItemResult.Skipped),
                Seconds = times == null ? 0 : g.Sum(i => times.TryGetValue(i.Path, out var s) ? s : 0),
            }).OrderBy(x => x.Kind).ToList();
            if (retried != null && previous != null)
            {
                run.Errors.AddRange(previous.Errors.Where(e => !retried.Contains(e.Path)));
                run.Notes = previous.Notes.Concat(run.Notes).Distinct().ToList();
                run.Done += previous.Done;
                run.Seconds = previous.Seconds;
                // функции, которые не повторяли, — как были; повторённые — сделанное прежде и теперь, ошибки — какие остались
                foreach (var kind in new[] { "NWC", "NWF", "RVT", "RESAVE" })
                {
                    var before = StepOf(previous, kind);
                    var now = run.Steps.FirstOrDefault(x => x.Kind == kind);
                    if (before == null && now == null) continue;
                    var merged = new StepRun
                    {
                        Kind = kind, Done = (before?.Done ?? 0) + (now?.Done ?? 0), Skipped = before?.Skipped ?? 0,
                        Seconds = before?.Seconds ?? now?.Seconds ?? 0, Failed = run.Errors.Count(e => KindOf(e.Path) == kind),
                    };
                    run.Steps.RemoveAll(x => x.Kind == kind);
                    run.Steps.Add(merged);
                }
                run.Steps = run.Steps.OrderBy(x => x.Kind).ToList();
            }
            return run;
        }

        /// <summary>Итог пересохранения модели — путь модели с этим окончанием (сам путь модели — «.rvt», как у выгрузки RVT).</summary>
        public const string ResaveSuffix = "|пересохранение";

        public static string ResaveKey(string modelPath) => (modelPath ?? "") + ResaveSuffix;

        /// <summary>Функция по файлу: NWF — «.nwf», пересохранение — ResaveSuffix, RVT — «.rvt» (08.11.2026), остальное — NWC.</summary>
        public static string KindOf(string path) =>
            (path ?? "").EndsWith(".nwf", StringComparison.OrdinalIgnoreCase) ? "NWF"
            : (path ?? "").EndsWith(ResaveSuffix, StringComparison.Ordinal) ? "RESAVE"
            : (path ?? "").EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? "RVT" : "NWC";

        /// <summary>Название функции для людей.</summary>
        public static string KindTitle(string kind) => kind == "RESAVE" ? "Пересохранение" : kind;

        /// <summary>Итог функции в запуске; запуск до 07.11.2026 (без Steps) — по ошибкам и числу выгруженных (тогда — NWC).</summary>
        public static StepRun StepOf(RowRun run, string kind)
        {
            if (run == null) return null;
            var step = run.Steps?.FirstOrDefault(x => x.Kind == kind);
            if (step != null) return step;
            if (run.Steps != null && run.Steps.Count > 0) return null;
            var failed = run.Errors.Count(e => KindOf(e.Path) == kind);
            var done = kind == "NWC" ? run.Done : 0;
            return failed == 0 && done == 0 ? null : new StepRun { Kind = kind, Done = done, Failed = failed };
        }

        /// <summary>Значок функции: ошибки — красный; что-то сделано без ошибок — зелёный; не было — нет.</summary>
        public static StepMark MarkOf(RowRun run, string kind)
        {
            var step = StepOf(run, kind);
            return step == null ? StepMark.None : step.Failed > 0 ? StepMark.Errors : step.Done > 0 ? StepMark.Ok : StepMark.None;
        }

        /// <summary>Подсказка к значку функции: когда, кто, сколько; её ошибки; у NWC — пропуски и изменившиеся модели.</summary>
        public static string StepTip(RowRun run, string kind, IList<string> changed, bool unknown)
        {
            var lines = new List<string>();
            var step = StepOf(run, kind);
            var made = kind == "NWF" ? "собрано" : kind == "RESAVE" ? "пересохранено" : "выгружено";
            if (step != null)
            {
                lines.Add($"{KindTitle(kind)}: запуск {run.At:dd.MM.yyyy HH:mm}, {run.By}" + (step.Seconds > 0 ? ", " + NwcHistory.Duration(step.Seconds) : ""));
                lines.Add($"{made} {step.Done}, с ошибкой {step.Failed}, пропущено {step.Skipped}");
                var errors = run.Errors.Where(e => KindOf(e.Path) == kind).ToList();
                if (errors.Count > 0)
                {
                    lines.Add("Ошибки:");
                    lines.AddRange(errors.Take(12).Select(e => "  • " + e.Name + " — " + e.Note));
                    if (errors.Count > 12) lines.Add($"  … ещё {errors.Count - 12}");
                    lines.Add("↻ — повторить только их");
                }
                if (kind == "NWC" && run.Notes.Count > 0)
                {
                    lines.Add("Пропуски и несостыковки:");
                    lines.AddRange(run.Notes.Take(12).Select(n => "  • " + n));
                    if (run.Notes.Count > 12) lines.Add($"  … ещё {run.Notes.Count - 12}");
                }
            }
            if (kind == "NWC")
            {
                if (changed.Count > 0)
                {
                    lines.Add("Модели изменились после выгрузки NWC:");
                    lines.AddRange(changed.Take(12).Select(n => "  • " + n));
                    if (changed.Count > 12) lines.Add($"  … ещё {changed.Count - 12}");
                }
                if (unknown) lines.Add("Дату части моделей Revit Server не сообщил — изменились ли они, не известно.");
            }
            if (step != null || changed.Count > 0) lines.Add("Щелчок — предупреждения строки (там и журнал запуска)");
            return lines.Count == 0 ? null : string.Join("\n", lines);
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

        /// <summary>
        /// «Время» строки (разработчик, 07.11.2026: «вряд ли задача выгружалась 1 минуту»): её последний запуск целиком — с копиями
        /// моделей, запуском фонового Revit, NWF (сумма времён NWC была меньше настоящего: копии наперёд идут параллельно, запуск Revit
        /// в неё не входил); строку саму не запускали — сумма подзадач; у строки без подзадач — сумма последних времён её NWC и NWF.
        /// Ничего не известно — null. source — откуда, для подсказки.
        /// </summary>
        public static double? EstimateRow(ExportTask row, RunStateFile file, Func<ExportTask, IEnumerable<string>> paths, out string source)
        {
            source = null;
            var run = Find(file, row);
            if (run != null && run.Seconds > 0)
            {
                source = $"прошлый запуск — {NwcHistory.Duration(run.Seconds)} ({run.At:dd.MM.yyyy HH:mm})";
                return run.Seconds;
            }
            if (row.Children.Count > 0)
            {
                double sum = 0;
                var known = 0;
                foreach (var c in row.Children)
                {
                    var s = EstimateRow(c, file, paths, out _);
                    if (s == null) continue;
                    sum += s.Value;
                    known++;
                }
                if (known == 0) return null;
                source = "сумма подзадач" + (known < row.Children.Count ? $" (без {row.Children.Count - known} — их ещё не выгружали)" : "");
                return sum;
            }
            var items = Estimate(paths(row), file, out var unknown);
            if (items == null) return null;
            source = "по прошлым выгрузкам NWC и NWF строки, без копирования моделей — на деле дольше" +
                     (unknown > 0 ? $"; без {unknown} — их ещё не выгружали" : "");
            return items;
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
