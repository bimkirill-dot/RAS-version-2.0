#if NAVISWORKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Interop;
using NwApp = Autodesk.Navisworks.Api.Application;
using Autodesk.Navisworks.Api.Plugins;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.NwfBuild
{
    /// <summary>
    /// Отчёты Clash Detective (разработчик, 08.11.2026: «сделаем, чтобы можно было и отчёты выгружать»): та же функция, что кнопка
    /// «Записать отчёт» вкладки «Отчёт» (LcClClashReport.WriteReport — не из документированного API), без окна: вопросы «куда
    /// сохранить» — своим посредником (папка и имя файла — из параметров). Параметры: файл итога, папка, формат (название, как в
    /// Navisworks: «XML», «HTML», «HTML (Табличное представление)», «Текст», «Как точки обзора»). Отчёт — по всем проверкам,
    /// каждая — своим файлом (как отчёты разработчика: «АР Стены_МЕР Воздуховоды.xml»). Итог — строки: «ok» / «ошибка …», затем
    /// доступные форматы и виды отчёта (разобрать, если формат не нашёлся).
    /// </summary>
    [Plugin("RasNwfReport", "RASV", DisplayName = "RAS v. 2.0 — отчёты NWF")]
    [AddInPlugin(AddInLocation.None)]
    public class ReportPlugin : AddInPlugin
    {
        private sealed class Answers : LcClClashGUIProxy
        {
            private readonly string _folder;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Files = new List<string>();

            public Answers(string folder) => _folder = folder;

            public override bool PromptForFilename(string extension, string suggestedName, string filter, out string out_filename)
            {
                var name = string.IsNullOrWhiteSpace(suggestedName) ? "Отчёт" : Path.GetFileName(suggestedName);
                if (!string.IsNullOrEmpty(extension) && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    name += extension.StartsWith(".") ? extension : "." + extension;
                out_filename = Path.Combine(_folder, name);
                Files.Add(out_filename);
                return true;
            }

            public override bool PromptForFolder(string title, string startFolder, out string out_folder)
            {
                out_folder = _folder;
                Files.Add(_folder + Path.DirectorySeparatorChar);
                return true;
            }

            public override void ReportError(string error_msg) => Errors.Add(error_msg);
        }

        // проверки, результаты, группы — вглубь
        private static IEnumerable<SavedItem> All(IEnumerable<SavedItem> items)
        {
            foreach (var i in items)
            {
                yield return i;
                if (i is GroupItem g)
                    foreach (var x in All(g.Children)) yield return x;
            }
        }

        /// <summary>
        /// «Удалять пустые» у строки (разработчик, 11.11.2026): отчёт проверки без результатов или со всеми «Исправлено» — удалить
        /// (файл по названию проверки, NwfTool.ReportFilesOf). Папка опустела — и её. Что удалено и что не нашлось — строками итога.
        /// </summary>
        private static void DeleteEmpty(string folder, List<string> lines)
        {
            var tests = NwApp.ActiveDocument.GetClash().TestsData.Tests.OfType<ClashTest>().ToList();
            var empty = tests.Where(t => !All(t.Children).OfType<ClashResult>().Any(r => r.Status != ClashResultStatus.Resolved)).ToList();
            if (empty.Count == 0)
            {
                lines.Add("пустых отчётов нет (проверок: " + tests.Count + ")");
                return;
            }
            var entries = Directory.GetFiles(folder).Concat(Directory.GetDirectories(folder)).ToList();
            var deleted = new List<string>();
            var lost = new List<string>();
            foreach (var t in empty)
            {
                var mine = NwfTool.ReportFilesOf(entries, t.DisplayName);
                if (mine.Count == 0)
                {
                    lost.Add(t.DisplayName);
                    continue;
                }
                foreach (var m in mine)
                {
                    if (Directory.Exists(m)) Directory.Delete(m, true);
                    else File.Delete(m);
                }
                deleted.Add(t.DisplayName);
            }
            if (deleted.Count > 0)
                lines.Add(NwfTool.EmptyDeleted + " " + deleted.Count + " из " + tests.Count + " — " + string.Join(", ", deleted));
            if (lost.Count > 0)
                lines.Add("пустые, но файла отчёта не нашлось (не удалены): " + string.Join(", ", lost) + "; в папке: " +
                          string.Join(", ", entries.Select(e => Path.GetFileName(e))));
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }

        public override int Execute(params string[] parameters)
        {
            var result = parameters[0];
            var folder = parameters[1];
            var format = parameters[2];
            var options = parameters.Length > 3 && File.Exists(parameters[3]) ? NwfTool.Read<ReportOptions>(parameters[3]) : ReportOptions.Defaults();
            var lines = new List<string>();
            try
            {
                if (!LcClClashReport.IsInitialised()) LcClClashReport.Initialise();
                var formatters = Enumerable.Range(0, LcClClashReport.NumReportFormatters()).Select(i =>
                {
                    LcClClashReport.GetReportFormatterName(i, out var n);
                    LcClClashReport.GetReportFormatterInternalName(i, out var inner);
                    return (Index: i, Name: n ?? "", Inner: inner ?? "");
                }).ToList();
                var drivers = Enumerable.Range(0, LcClClashReport.NumReportDrivers()).Select(i =>
                {
                    LcClClashReport.GetReportDriverName(i, out var n);
                    LcClClashReport.GetReportDriverInternalName(i, out var inner);
                    return (Index: i, Name: n ?? "", Inner: inner ?? "");
                }).ToList();
                var f = formatters.FirstOrDefault(x => string.Equals(x.Name.Trim(), format.Trim(), StringComparison.OrdinalIgnoreCase));
                if (f.Name == null || f.Name.Length == 0)
                    f = formatters.FirstOrDefault(x => x.Name.IndexOf(format, StringComparison.OrdinalIgnoreCase) >= 0 && (format != "HTML" || x.Name.IndexOf("(", StringComparison.Ordinal) < 0));
                // все проверки, каждая — своим файлом: вид отчёта «раздельно» (по внутреннему имени или названию)
                var d = drivers.FirstOrDefault(x => x.Inner.IndexOf("separate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    x.Name.IndexOf("раздельн", StringComparison.OrdinalIgnoreCase) >= 0);
                if (f.Name == null || f.Name.Length == 0)
                    lines.Add("ошибка: формата «" + format + "» нет");
                else if (d.Name == null || d.Name.Length == 0)
                    lines.Add("ошибка: вида отчёта «все проверки раздельно» нет");
                else
                {
                    Directory.CreateDirectory(folder);
                    LcClClashReport.SetCurrentReportFormatter(f.Index);
                    LcClClashReport.SetCurrentReportDriver(d.Index);
                    // настройки отчёта (шестерёнка → «Настройки отчётов»): статусы, содержимое, группы, «только отфильтрованные»; без окна
                    // Navisworks ни один статус не отмечен — в отчёте была бы только сводка
                    LcClClashReport.SetCurrentFilterMode(options.FilteredOnly);
                    var statuses = new List<string>();
                    for (var s = 0; s < LcClClashReport.NumStatusFlags(); s++)
                    {
                        var flag = LcClClashReport.GetStatusFlag(s);
                        flag.GetUserNameW(out var sn);
                        var on = options.Statuses.Any(x => string.Equals(x.Trim(), (sn ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                        flag.SetEnabled(on);
                        statuses.Add(sn + (on ? "+" : "-"));
                    }
                    var fields = new List<string>();
                    for (var s = 0; s < LcClClashReport.NumReportFields(); s++)
                    {
                        var field = LcClClashReport.GetReportField(s);
                        field.GetUserNameW(out var fn);
                        var on = options.Fields.Any(x => string.Equals(x.Trim(), (fn ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                        field.SetEnabled(on);
                        fields.Add(fn + (on ? "+" : "-"));
                    }
                    for (var g = 0; g < LcClClashReport.NumGroupModes(); g++)
                    {
                        LcClClashReport.GetGroupModeName(g, out var gn);
                        if (string.Equals((gn ?? "").Trim(), (options.GroupMode ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) LcClClashReport.SetCurrentGroupMode(g);
                    }
                    // в отчёт — только результаты из выбора (окно Clash Detective кладёт туда все): все результаты и группы всех проверок
                    var selector = new LcClashReportResultSelector();
                    var results = 0;
                    foreach (var item in All(NwApp.ActiveDocument.GetClash().TestsData.Tests))
                    {
                        selector.AddGuid(item.Guid);
                        if (item is ClashResult) results++;
                    }
                    var answers = new Answers(folder);
                    var ok = LcClClashReport.WriteReport(LcOpState.GetActiveInstance(), answers, selector, "");
                    lines.Insert(0, ok && answers.Errors.Count == 0 ? "ok" : "ошибка: " + (answers.Errors.Count > 0 ? string.Join("; ", answers.Errors) : "отчёт не записан"));
                    lines.Add("куда: " + string.Join(" | ", answers.Files) + "; результатов в выборе: " + results);
                    if (ok && answers.Errors.Count == 0 && options.DeleteEmpty) DeleteEmpty(folder, lines);
                    lines.Add("статусы: " + string.Join(", ", statuses));
                    lines.Add("поля: " + string.Join(", ", fields));
                }
                lines.Add("форматы: " + string.Join(" | ", formatters.Select(x => x.Name + " [" + x.Inner + "]")));
                lines.Add("виды: " + string.Join(" | ", drivers.Select(x => x.Name + " [" + x.Inner + "]")));
                var groups = Enumerable.Range(0, LcClClashReport.NumGroupModes()).Select(i => { LcClClashReport.GetGroupModeName(i, out var g); return g; }).ToList();
                lines.Add("группы: " + string.Join(" | ", groups) + "; сейчас: " + LcClClashReport.GetCurrentGroupMode());
            }
            catch (Exception ex)
            {
                lines.Insert(0, "ошибка: " + ex.Message);
                lines.Add(ex.ToString());
            }
            File.WriteAllLines(result, lines, new UTF8Encoding(false));
            return 0;
        }
    }
}
#endif
