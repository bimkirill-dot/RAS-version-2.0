using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Что плагин скопировал в папку NWC последним: файл подрядчика и его отпечаток (дата и размер) — в файле состояния задачи.</summary>
    [DataContract]
    public sealed class ReadyCopy
    {
        [DataMember(Order = 1)] public string Target { get; set; }
        [DataMember(Order = 2)] public string Source { get; set; }
        [DataMember(Order = 3)] public string Stamp { get; set; }
    }

    public enum ReadyAction
    {
        /// <summary>Скопировать файл подрядчика в папку NWC.</summary>
        Copy,
        /// <summary>NWC в папке остаётся как есть.</summary>
        Keep,
        /// <summary>Ни файла подрядчика, ни NWC в папке — в NWF её не будет.</summary>
        Lost,
    }

    /// <summary>
    /// Готовая NWC (разработчик, 12.11.2026: «модель S1 АР сделана в другой компании, к их серверу не подключиться, они скинули
    /// готовую NWC — в NWF её не будет»): модель задачи с путём к «.nwc» — Revit её не открывает, плагин копирует файл в папку NWC
    /// задачи, NWF берёт копию. Копирует, когда в папке её нет, выбран другой файл или файл подрядчика по тому же пути обновился;
    /// NWC, заменённую в папке руками, не трогает («если путь такой же — само обновится», «NWC подрядчика можно обновлять и вручную,
    /// и плагином»; по «новее / не новее» не решается). Ни файла подрядчика, ни NWC в папке — перед запуском вопрос.
    /// </summary>
    public static class ReadyNwc
    {
        /// <summary>Отпечаток файла: дата изменения и размер.</summary>
        public static string StampOf(string path)
        {
            var f = new FileInfo(path);
            return f.LastWriteTimeUtc.Ticks + "|" + f.Length;
        }

        public static ReadyAction Decide(string source, string target, ReadyCopy last, Func<string, bool> exists, Func<string, string> stamp,
            out string note)
        {
            var name = ExportNodes.FileName(source);
            if (!exists(source))
            {
                if (exists(target))
                {
                    note = "файла подрядчика «" + source + "» нет — в NWF идёт NWC, что лежит в папке";
                    return ReadyAction.Keep;
                }
                note = "готовой NWC нет: ни файла подрядчика «" + source + "», ни NWC в папке — в NWF её не будет";
                return ReadyAction.Lost;
            }
            if (!exists(target))
            {
                note = "готовая NWC «" + name + "» скопирована в папку NWC";
                return ReadyAction.Copy;
            }
            if (last == null)
            {
                note = "готовая NWC уже в папке — не заменяется (файл подрядчика обновится — скопируется)";
                return ReadyAction.Keep;
            }
            if (!string.Equals(last.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                note = "выбран другой файл подрядчика «" + name + "» — скопирован в папку NWC";
                return ReadyAction.Copy;
            }
            if (last.Stamp != stamp(source))
            {
                note = "файл подрядчика «" + name + "» обновился — скопирован заново";
                return ReadyAction.Copy;
            }
            note = "готовая NWC в папке актуальна (файл подрядчика не менялся)";
            return ReadyAction.Keep;
        }

        /// <summary>Готовые NWC, которых не будет в NWF: ни файла подрядчика, ни NWC в папке — для вопроса перед запуском.</summary>
        public static List<NwcJob> Lost(IEnumerable<NwcJob> jobs, Func<string, bool> exists) =>
            jobs.Where(j => j.Ready != null && j.Skip == null && !j.Off && !exists(j.Ready) && !exists(j.NwcPath)).ToList();
    }
}
