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
                var plan = new NwfPlan { Row = row, NwfPath = ExportNodes.NwfPath(row, common) };
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
