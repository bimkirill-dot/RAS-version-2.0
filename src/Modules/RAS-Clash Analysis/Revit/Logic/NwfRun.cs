using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>NWF строки при «Запустить»: куда, из каких NWC; Skip — почему не собирается.</summary>
    public sealed class NwfPlan
    {
        public ExportTask Row { get; set; }
        public string NwfPath { get; set; }
        public List<string> Nwcs { get; set; } = new List<string>();
        public string Skip { get; set; }
        /// <summary>«Скрыть» строки (свои или от строки выше) — &lt;selectionset&gt;; скрываются в NWF до проверок.</summary>
        public List<HideSet> HideSets { get; set; } = new List<HideSet>();
        /// <summary>Файлы проверок строки (свои или от строки выше).</summary>
        public List<string> Tests { get; set; } = new List<string>();
        /// <summary>Подпись файлов проверок (RunStates.TestsSignature) — запомнить после сборки.</summary>
        public string TestsSignature { get; set; }
        /// <summary>Настройки отчёта — общие (шестерёнка).</summary>
        public ReportOptions ReportOptions { get; set; }
        /// <summary>Отчёты после прогона проверок: формат — папка (папка отчётов строки, {Дата} — время запуска).</summary>
        public List<NwfToolReport> Reports { get; set; } = new List<NwfToolReport>();
        /// <summary>«Выполнять проверки» у строки: нет — проверки не загружаются и не прогоняются.</summary>
        public bool RunTests { get; set; } = true;
        /// <summary>Файл проверок сменился, человек согласился — проверки в NWF заменяются.</summary>
        public bool ReplaceTests { get; set; }
        /// <summary>
        /// Только отчёты (у строки «Отчёты» без «NWF», 08.11.2026): готовая NWF — открыть, прогнать проверки, записать отчёты, сохранить;
        /// модели не добавляются, скрытие не делается.
        /// </summary>
        public bool ReportsOnly { get; set; }
        /// <summary>
        /// «Формировать отчёты» стоит, а отчётов не будет — почему (09.11.2026: раньше — молча): строка NWF после сборки — ошибкой.
        /// </summary>
        public string ReportsSkip { get; set; }
    }

    /// <summary>
    /// Какие NWF собирать после NWC (DECISIONS.md модуля, «Этап 2 — сборка NWF»): строки ветки с галочкой «Формировать NWF» (без
    /// подзадач), NWC — по моделям строки, теми же путями, что у выгрузки NWC. Галочки — и по отдельности (разработчик, 08.11.2026):
    /// «NWF» без «NWC» — NWF обновляется из NWC, какие есть на диске (скрытие, проверки — заново, файл проверок сменился — вопрос);
    /// «Отчёты» без «NWF» — по готовой NWF строки: прогон проверок и отчёты (ReportsOnly).
    /// </summary>
    public static class NwfRunPlan
    {
        /// <param name="runDate">Время запуска — {Дата} папки отчётов (одно на весь запуск: все форматы — в одну папку).</param>
        /// <param name="exists">Есть ли файл (NWF для «только отчётов»); null — File.Exists.</param>
        public static List<NwfPlan> Build(ExportTask start, ExportSettings common, DateTime? runDate = null, Func<string, bool> exists = null)
        {
            var when = runDate ?? DateTime.Now;
            exists = exists ?? System.IO.File.Exists;
            var plans = new List<NwfPlan>();
            foreach (var row in start.Active().Where(r => ExportNodes.MakesNwf(r) || ExportNodes.MakesReports(r)))
            {
                var reportsOnly = !ExportNodes.MakesNwf(row);
                var plan = new NwfPlan
                {
                    Row = row, NwfPath = ExportNodes.NwfPath(row, common), ReportsOnly = reportsOnly,
                    HideSets = reportsOnly ? new List<HideSet>()
                        : (ExportNodes.HideSetsOf(row) ?? new List<HideSet>()).Where(h => !string.IsNullOrWhiteSpace(h.Xml)).ToList(),
                    // «Выполнять проверки» снята — проверок в задании нет (NWF — без них, человек — вручную)
                    Tests = row.Settings.NoTestsRun ? new List<string>()
                        : (ExportNodes.TestsOf(row) ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                    RunTests = !row.Settings.NoTestsRun, ReportOptions = (common.Report ?? ReportOptions.Defaults()).Clone(),
                };
                plan.ReportOptions.DeleteEmpty = row.Settings.DeleteEmptyReports;
                // отчёты — у строки «Формировать отчёты» (и проверки выполняются: отчёт — по их результатам; разработчик, 08.11.2026); не
                // выйдет — причина (разработчик, 09.11.2026: «отчёты не выгрузились, а ошибки не было»)
                if (ExportNodes.MakesReports(row))
                {
                    var formats = ExportNodes.FormatsOf(row) ?? new List<string>();
                    if (!plan.RunTests) plan.ReportsSkip = "отчёты не выгружены: «Выполнять проверки» снята — отчёт строится по результатам проверок";
                    else if (plan.Tests.Count == 0) plan.ReportsSkip = "отчёты не выгружены: у строки нет файлов проверок";
                    else if (formats.Count == 0) plan.ReportsSkip = "отчёты не выгружены: не выбран ни один формат";
                    else if (ExportNodes.HasLabel(ExportNodes.ReportPath(row, common, when, formats[0])))
                        plan.ReportsSkip = "отчёты не выгружены: {Блок} в папке отчётов не определить — у моделей строки разные блоки или блок не найден";
                    else
                        plan.Reports = formats.Select(f => new NwfToolReport { Format = f, Folder = ExportNodes.ReportPath(row, common, when, f) }).ToList();
                }
                plan.Nwcs = (ExportNodes.ModelsOf(row) ?? new List<ExportModel>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.Path))
                    .Select(m => ExportNodes.NwcPath(row, m, common))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ExportNodes.HasLabel(plan.NwfPath))
                    plan.Skip = $"{{Блок}} в папке или имени NWF не определить: у моделей «{row.Name}» разные блоки или блок не найден";
                else if (!NwcRunPlan.IsRooted(plan.NwfPath))
                    plan.Skip = $"не задана папка: ни «Папки задачи», ни полного пути в «Папке для NWF» у «{row.Name}»";
                else if (reportsOnly && !plan.RunTests)
                    plan.Skip = "отчёты — по результатам проверок, а «Выполнять проверки» снята";
                else if (reportsOnly && plan.Tests.Count == 0)
                    plan.Skip = "отчёты — по результатам проверок, а файлов проверок у строки нет";
                else if (reportsOnly && !exists(plan.NwfPath))
                    plan.Skip = "NWF ещё нет — отчёты делаются по готовой NWF; включите «NWF», чтобы собрать её";
                else if (!reportsOnly && plan.Nwcs.Count == 0) plan.Skip = "у строки нет моделей — NWF не из чего собрать";
                plans.Add(plan);
            }
            return plans;
        }
    }
}
