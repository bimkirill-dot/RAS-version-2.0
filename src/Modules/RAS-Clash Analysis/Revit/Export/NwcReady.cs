using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Готовые NWC подрядчика (разработчик, 12.11.2026) — в папку NWC задачи, после выгрузки своих и до NWF; что копировать — ReadyNwc.
    /// Что скопировано — в файлы состояния задач запуска: в следующий раз видно, сменился ли файл подрядчика.
    /// </summary>
    internal static class NwcReady
    {
        public static void Header(ExportJournal j, List<NwcJob> ready)
        {
            if (ready.Count == 0) return;
            j.Line($"Готовые NWC (от подрядчика — модель не открывается, файл копируется в папку NWC): {ready.Count}");
            foreach (var r in ready)
                j.Line($"       {r.Ready}  →  {r.NwcPath}  (строки: {string.Join(", ", r.Rows)})" + (r.Skip != null ? " — " + r.Skip : ""));
        }

        public static void Run(List<NwcProgress> items, IList<ExportTask> tasks, ExportJournal j, Action<NwcProgress> changed, bool cancelled)
        {
            var ready = items.Where(p => p.Job.Ready != null && p.Job.Skip == null).ToList();
            if (ready.Count == 0) return;
            j.Line("════ ГОТОВЫЕ NWC");
            var states = tasks.Select(RunStates.PathOf).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = states.SelectMany(s => RunStates.Load(s).Ready).ToList();
            var records = new List<ReadyCopy>();
            foreach (var p in ready)
            {
                var source = p.Job.Ready;
                var target = p.Job.NwcPath;
                try
                {
                    if (cancelled)
                        Set(p, NwcState.Skipped, "выгрузка остановлена");
                    else if (p.Job.Off)
                        Set(p, NwcState.Skipped, "выгрузка NWC выключена (галочка «NWC»)");
                    else
                    {
                        var last = known.LastOrDefault(k => string.Equals(k.Target, target, StringComparison.OrdinalIgnoreCase));
                        var action = ReadyNwc.Decide(source, target, last, File.Exists, ReadyNwc.StampOf, out var note);
                        if (action == ReadyAction.Copy)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            // через временный файл рядом: прерванное копирование не оставит половину NWC
                            var temp = target + ".ras-tmp";
                            File.Copy(source, temp, true);
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(temp, target);
                            Set(p, NwcState.Done, note);
                        }
                        else Set(p, action == ReadyAction.Lost ? NwcState.Failed : NwcState.Skipped, note);
                        if (File.Exists(source))
                            records.Add(new ReadyCopy { Target = target, Source = source, Stamp = ReadyNwc.StampOf(source) });
                    }
                }
                catch (Exception ex)
                {
                    Set(p, NwcState.Failed, "готовая NWC не скопировалась: " + ex.Message);
                    j.Error($"{p.Job.ModelName}: готовая NWC «{source}» → «{target}» не скопировалась — {ex.Message}", ex);
                }
                j.Line($"  {NwcExporter.StateText(p.State),-12} {p.Job.ModelName}  →  {target} — {p.Note}");
                changed(p);
            }
            if (records.Count == 0) return;
            foreach (var s in states)
            {
                try
                {
                    RunStates.Save(s, new List<RowRun>(), new List<ItemTime>(), null, records);
                }
                catch (Exception ex)
                {
                    j.Error("что скопировано из готовых NWC — не записано в «" + s + "»: " + ex.Message, ex);
                }
            }
        }

        private static void Set(NwcProgress p, NwcState state, string note)
        {
            p.State = state;
            p.Nwc = state;
            p.Step = p.Note = p.NwcNote = note;
        }
    }
}
