using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// «Запустить вместе» (разработчик, 11.11.2026): NWC другой задачи с той же моделью, видом и настройками (NwcJob.CopyOf) — копия
    /// выгруженной NWC, после всех NWC и до NWF. Исходная выгружена — копировать; пропущена при выгрузке (отбор «отредактированные») —
    /// копировать, если копии нет или она старше; ошибка или пропуск до выгрузки — у копии то же.
    /// </summary>
    internal static class NwcCopies
    {
        public static void Header(ExportJournal j, List<NwcJob> copies)
        {
            if (copies.Count == 0) return;
            j.Line($"NWC копией (та же модель, вид и настройки у другой задачи — модель не открывается заново): {copies.Count}");
            foreach (var c in copies)
                j.Line($"       {c.ModelName}  →  {c.NwcPath}  (копия «{c.CopyOf.NwcPath}»; строки: {string.Join(", ", c.Rows)})");
        }

        public static void Run(List<NwcProgress> items, ExportJournal j, Action<NwcProgress> changed, bool cancelled)
        {
            var copies = items.Where(p => p.Job.CopyOf != null).ToList();
            if (copies.Count == 0) return;
            j.Line("════ КОПИИ NWC");
            foreach (var p in copies)
            {
                var source = items.FirstOrDefault(s => s.Job == p.Job.CopyOf);
                var target = p.Job.NwcPath;
                var from = p.Job.CopyOf.NwcPath;
                try
                {
                    var state = source == null ? NwcState.Failed : source.Nwc ?? source.State;
                    if (cancelled)
                        Set(p, NwcState.Skipped, "выгрузка остановлена");
                    else if (state == NwcState.Failed || state == NwcState.Waiting || state == NwcState.Working)
                    {
                        var why = source?.NwcNote ?? source?.Note;
                        Set(p, NwcState.Failed, "не выгрузилась NWC, копией которой она была бы: «" + ExportNodes.FileName(from) + "»" +
                                                (why != null ? " — " + why : ""));
                    }
                    else if (state == NwcState.Skipped && (source.Job.Skip != null || !File.Exists(from)))
                        Set(p, NwcState.Skipped, source.Job.Skip ?? source.NwcNote ?? source.Note ?? "исходная NWC пропущена");
                    else if (state == NwcState.Skipped && File.Exists(target) && File.GetLastWriteTime(target) >= File.GetLastWriteTime(from))
                        Set(p, NwcState.Skipped, "NWC актуальна — как «" + ExportNodes.FileName(from) + "»" + ((source.NwcNote ?? source.Note) is string note ? ": " + note : ""));
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        // через временный файл рядом: прерванное копирование не оставит половину NWC
                        var temp = target + ".ras-tmp";
                        File.Copy(from, temp, true);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(temp, target);
                        Set(p, NwcState.Done, "копия «" + ExportNodes.FileName(from) + "» — модель не открывалась заново");
                    }
                }
                catch (Exception ex)
                {
                    Set(p, NwcState.Failed, "копия не записалась: " + ex.Message);
                    j.Error($"{p.Job.ModelName}: копия NWC «{target}» не записалась — {ex.Message}", ex);
                }
                j.Line($"  {NwcExporter.StateText(p.State),-12} {p.Job.ModelName}  →  {target} — {p.Note}");
                changed(p);
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
