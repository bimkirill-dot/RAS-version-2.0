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
        /// <summary>«Выполнять проверки» у строки: нет — проверки не загружаются и не прогоняются.</summary>
        public bool RunTests { get; set; } = true;
        /// <summary>Файл проверок сменился, человек согласился — проверки в NWF заменяются.</summary>
        public bool ReplaceTests { get; set; }
    }

    /// <summary>
    /// Какие NWF собирать после NWC (DECISIONS.md модуля, «Этап 2 — сборка NWF»): строки ветки с галочкой «Формировать NWF» (без
    /// подзадач), NWC — по моделям строки, теми же путями, что у выгрузки NWC.
    /// </summary>
    public static class NwfRunPlan
    {
        public static List<NwfPlan> Build(ExportTask start, ExportSettings common)
        {
            var plans = new List<NwfPlan>();
            foreach (var row in start.Active().Where(ExportNodes.MakesNwf))
            {
                var plan = new NwfPlan
                {
                    Row = row, NwfPath = ExportNodes.NwfPath(row, common),
                    HideSets = (ExportNodes.HideSetsOf(row) ?? new List<HideSet>()).Where(h => !string.IsNullOrWhiteSpace(h.Xml)).ToList(),
                    // «Выполнять проверки» снята — проверок в задании нет (NWF — без них, человек — вручную)
                    Tests = row.Settings.NoTestsRun ? new List<string>()
                        : (ExportNodes.TestsOf(row) ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                    RunTests = !row.Settings.NoTestsRun,
                };
                plan.Nwcs = (ExportNodes.ModelsOf(row) ?? new List<ExportModel>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.Path))
                    .Select(m => ExportNodes.NwcPath(row, m, common))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (plan.Nwcs.Count == 0) plan.Skip = "у строки нет моделей — NWF не из чего собрать";
                else if (!NwcRunPlan.IsRooted(plan.NwfPath))
                    plan.Skip = $"не задана папка: ни «Папки задачи», ни полного пути в «Папке для NWF» у «{row.Name}»";
                plans.Add(plan);
            }
            return plans;
        }
    }
}
