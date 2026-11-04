using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Одна NWC запуска: какая модель, куда, с какими параметрами; Skip — почему не выгружается (тогда не открывается).</summary>
    public sealed class NwcJob
    {
        public ExportModel Model { get; set; }
        public string NwcPath { get; set; }
        public ExportSettings Export { get; set; }
        /// <summary>Вид для выгрузки (при «Экспорт: Вид»).</summary>
        public string View { get; set; }
        public ModelDateFilter Edited { get; set; }
        /// <summary>Строки дерева, которым нужна эта NWC: по первой взяты настройки.</summary>
        public List<string> Rows { get; } = new List<string>();
        public string Skip { get; set; }

        public string ModelName => ExportNames.BareName(Model.Path);
    }

    /// <summary>
    /// Что выгружать при «Запустить» у строки (DECISIONS.md модуля, «Выгрузка NWC — первая версия»): модели строк без подзадач этой
    /// ветки, у каждой — её папка, вид, параметры; одна NWC — один раз (по пути файла). Модели строк с подзадачами, которых нет ни
    /// у одной строки ниже, — тоже, по настройкам своей строки. Несостыковки — в notes (в журнал и в окно перед запуском).
    /// </summary>
    public static class NwcRunPlan
    {
        public static List<NwcJob> Build(ExportTask start, ExportSettings common, int revitYear, List<string> notes)
        {
            var jobs = new List<NwcJob>();
            var byPath = new Dictionary<string, NwcJob>(StringComparer.OrdinalIgnoreCase);
            var rows = start.Self().ToList();
            var leaves = rows.Where(r => r.Children.Count == 0).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in leaves.Concat(rows.Where(r => r.Children.Count > 0)))
            {
                var leaf = row.Children.Count == 0;
                foreach (var model in ExportNodes.ModelsOf(row) ?? new List<ExportModel>())
                {
                    if (string.IsNullOrWhiteSpace(model.Path)) continue;
                    // у строки с подзадачами — только модели, не нужные ни одной строке ниже (их выгрузят подзадачи)
                    if (!leaf && used.Contains(model.Path)) continue;
                    used.Add(model.Path);
                    var export = ExportNodes.ExportOf(row, common);
                    var job = new NwcJob
                    {
                        Model = model, Export = export, View = ExportNodes.ViewOf(row, common), Edited = ExportNodes.EditedOf(row),
                        NwcPath = ExportNodes.NwcPath(row, model, common),
                    };
                    if (byPath.TryGetValue(job.NwcPath, out var same))
                    {
                        same.Rows.Add(row.Name);
                        if (!string.Equals(same.Model.Path, model.Path, StringComparison.OrdinalIgnoreCase))
                            notes.Add($"Одна NWC «{ExportNodes.FileName(job.NwcPath)}» — у двух моделей: «{same.Model.Path}» и «{model.Path}» (строка «{row.Name}»). Выгружается первая — проверьте имена NWC.");
                        else if (Signature(same) != Signature(job))
                            notes.Add($"«{ExportNodes.FileName(job.NwcPath)}»: у строк «{same.Rows[0]}» и «{row.Name}» разные вид или «Настройки NWC Exporter» — выгружается по «{same.Rows[0]}».");
                        continue;
                    }
                    if (!leaf)
                        notes.Add($"Модель «{job.ModelName}» есть у «{row.Name}», но ни у одной подзадачи — выгружается по настройкам «{row.Name}».");
                    job.Rows.Add(row.Name);
                    job.Skip = SkipReason(model, revitYear);
                    byPath[job.NwcPath] = job;
                    jobs.Add(job);
                }
            }
            foreach (var g in jobs.GroupBy(j => j.Model.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                notes.Add($"Модель «{ExportNames.BareName(g.Key)}» выгружается {g.Count()} раза — в разные папки: " +
                          string.Join(", ", g.Select(j => j.NwcPath)) + ". Модель откроется столько же раз.");
            return jobs;
        }

        /// <summary>
        /// Модель Revit Server другой версии — в этом Revit не выгружается: у каждой версии свой Revit Server, и тот же путь в службе
        /// этой версии — другая модель или никакой.
        /// </summary>
        public static string SkipReason(ExportModel model, int revitYear) =>
            ExportNodes.IsServerPath(model.Path) && model.Year > 0 && model.Year != revitYear
                ? $"модель Revit Server {model.Year} — выгружается в Revit {model.Year}"
                : null;

        /// <summary>Вид и параметры выгрузки одной строкой — сравнить две строки дерева.</summary>
        public static string Signature(NwcJob job)
        {
            var e = job.Export;
            var nwc = string.Join(";", typeof(NwcOptions).GetProperties().OrderBy(p => p.Name)
                .Select(p => p.Name + "=" + Convert.ToString(p.GetValue(e.Nwc), CultureInfo.InvariantCulture)));
            return $"{job.View}|{nwc}|{e.RemoveRevitLinks}{e.RemoveCad}{e.RemovePointClouds}{e.RemoveIfc}";
        }

        /// <summary>Выгружать ли по отбору «отредактированные»: нет NWC — всегда; нет даты модели — всегда (Passes).</summary>
        public static bool Due(NwcJob job, DateTime? modelModified, bool nwcExists) =>
            !nwcExists || job.Edited == null || !job.Edited.On || job.Edited.Passes(modelModified);

        /// <summary>
        /// NWC актуальна — модель не копировать (разработчик, 04.11.2026: «можно ли ускорить» — копия с Revit Server идёт 2–3 минуты):
        /// NWC есть и записана позже последнего изменения модели, а вид и параметры выгрузки — те же, что в прошлый раз на этом
        /// компьютере (нет записи — NWC выгружал другой человек: по дате). Дата модели не известна — не актуальна. Почему
        /// актуальна — строкой; null — выгружать.
        /// </summary>
        public static string Fresh(NwcJob job, DateTime? modelModified, DateTime? nwcWritten, NwcHistory.Entry last)
        {
            if (modelModified == null || nwcWritten == null || nwcWritten <= modelModified) return null;
            if (last != null && last.Signature != null && last.Signature != Signature(job)) return null;
            return $"модель не менялась с прошлой выгрузки (изменена {modelModified:dd.MM.yyyy HH:mm}, NWC от {nwcWritten:dd.MM.yyyy HH:mm})";
        }
    }

    /// <summary>
    /// Прошлые выгрузки NWC на этом компьютере (размер, элементов в виде, время) — сравнить с новой: NWC резко меньше или в виде
    /// заметно меньше элементов — несостыковка в журнале (вид настроен не так, модель открылась не вся, связи не удалились…).
    /// </summary>
    [DataContract]
    public sealed class NwcHistory
    {
        [DataContract]
        public sealed class Entry
        {
            [DataMember(Order = 1)] public string Nwc { get; set; }
            [DataMember(Order = 2)] public long Size { get; set; }
            [DataMember(Order = 3)] public int Elements { get; set; }
            [DataMember(Order = 4)] public double Seconds { get; set; }
            [DataMember(Order = 5)] public DateTime At { get; set; }
            /// <summary>Вид и параметры выгрузки этой NWC (NwcRunPlan.Signature) — поменялись, NWC выгружается заново.</summary>
            [DataMember(Order = 6, EmitDefaultValue = false)] public string Signature { get; set; }
            /// <summary>Когда менялась модель, из которой выгружена NWC.</summary>
            [DataMember(Order = 7, EmitDefaultValue = false)] public DateTime? ModelModified { get; set; }
        }

        [DataMember(Order = 1)] public List<Entry> Entries { get; set; } = new List<Entry>();

        public Entry Last(string nwc) => Entries.FirstOrDefault(e => string.Equals(e.Nwc, nwc, StringComparison.OrdinalIgnoreCase));

        public void Put(Entry entry)
        {
            // без даты запись не сохранится в JSON (DateTime.MinValue в UTC) — ставим «сейчас»
            if (entry.At == default(DateTime)) entry.At = DateTime.Now;
            Entries.RemoveAll(e => string.Equals(e.Nwc, entry.Nwc, StringComparison.OrdinalIgnoreCase));
            Entries.Add(entry);
        }

        /// <summary>Несостыковки новой выгрузки с прошлой: NWC меньше на треть и больше, элементов в виде меньше на 10 % и больше.</summary>
        public static List<string> Compare(Entry before, Entry now)
        {
            var notes = new List<string>();
            if (before == null) return notes;
            if (before.Size > 0 && now.Size < before.Size * 2 / 3)
                notes.Add($"NWC меньше прошлой: {SizeText(now.Size)} против {SizeText(before.Size)} ({before.At:dd.MM.yyyy HH:mm})");
            if (before.Elements > 0 && now.Elements < before.Elements * 0.9)
                notes.Add($"элементов в виде меньше прошлого раза: {now.Elements} против {before.Elements}");
            if (before.Seconds > 60 && now.Seconds > before.Seconds * 2)
                notes.Add($"выгрузка дольше прошлой в {now.Seconds / before.Seconds:0.#} раза: {Duration(now.Seconds)} против {Duration(before.Seconds)}");
            return notes;
        }

        public static NwcHistory Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new NwcHistory();
                using (var s = File.OpenRead(path))
                    return (NwcHistory)new DataContractJsonSerializer(typeof(NwcHistory)).ReadObject(s) ?? new NwcHistory();
            }
            catch (Exception)
            {
                return new NwcHistory();   // испорчен — начинаем заново: история только для сравнения
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var s = File.Create(tmp))
                new DataContractJsonSerializer(typeof(NwcHistory)).WriteObject(s, this);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string SizeText(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0#", CultureInfo.GetCultureInfo("ru-RU")) + " ГБ"
            : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " МБ"
            : (bytes / 1024.0).ToString("0", CultureInfo.GetCultureInfo("ru-RU")) + " КБ";

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    /// <summary>
    /// Журнал запуска выгрузки (разработчик, 04.11.2026: «сразу заведи логи, журналы, которые замеряют время, ищут ошибки и
    /// несостыковки»): каждая строка — время и сколько прошло от начала; шаги модели — с длительностью и памятью Revit; в конце —
    /// итог: модели, ошибки, несостыковки, время по шагам. Пишется сразу на диск — упадёт Revit, журнал останется.
    /// </summary>
    public sealed class ExportJournal : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, List<double>> _steps = new Dictionary<string, List<double>>();
        private readonly object _lock = new object();

        public string FilePath { get; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public TimeSpan Elapsed => _clock.Elapsed;

        public ExportJournal(string path)
        {
            FilePath = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // читать (окно «Журнал», копия в папку задачи) — можно, пока пишется
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };
        }

        public void Line(string text)
        {
            lock (_lock)
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{Stamp(_clock.Elapsed)}  {text}");
        }

        public void Warn(string text)
        {
            Warnings.Add(text);
            Line("НЕСОСТЫКОВКА: " + text);
        }

        public void Error(string text, Exception ex = null)
        {
            Errors.Add(text);
            Line("ОШИБКА: " + text + (ex == null ? "" : Environment.NewLine + ex));
        }

        /// <summary>Начало шага: «→ имя».</summary>
        public void StepStart(string name) => Line("   → " + name);

        /// <summary>Конец шага: «✓ имя — 0:12, память …» (не вышел — «✗»); длительность — в итог по шагам.</summary>
        public void StepDone(string name, double seconds, bool ok, string memory = null)
        {
            lock (_lock)
            {
                if (!_steps.TryGetValue(name, out var list)) _steps[name] = list = new List<double>();
                list.Add(seconds);
            }
            Line($"   {(ok ? "✓" : "✗")} {name} — {NwcHistory.Duration(seconds)} ({seconds:0.0} с){(memory == null ? "" : ", " + memory)}");
        }

        /// <summary>Время по шагам: всего, среднее, самое долгое — по убыванию общего.</summary>
        public string StepTotals()
        {
            var sb = new StringBuilder();
            foreach (var p in _steps.OrderByDescending(p => p.Value.Sum()))
                sb.AppendLine($"  {p.Key,-28} всего {NwcHistory.Duration(p.Value.Sum()),8}   раз {p.Value.Count,3}   в среднем {p.Value.Average(),7:0.0} с   дольше всего {p.Value.Max(),7:0.0} с");
            return sb.ToString();
        }

        /// <summary>Журнал как есть — и пока он пишется (File.ReadAllText и File.Copy открытый на запись файл не читают).</summary>
        public string ReadAll()
        {
            lock (_lock)
                using (var s = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
        }

        public void Dispose() => _writer.Dispose();

        private static string Stamp(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }
}
