using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Когда менялась модель — для жёлтого «модель изменилась после выгрузки» в списке задач: Revit Server — сведения о модели
    /// (служба её версии; версия не известна — этого Revit), файл — дата файла. Без Revit API — можно звать из фонового потока.
    /// </summary>
    internal static class ModelDates
    {
        /// <returns>Дата и ответил ли сервер: не ответил — (null, false); файла нет — (null, true).</returns>
        public static async Task<(DateTime? Date, bool Answered)> GetAsync(ExportModel model, int revitYear)
        {
            try
            {
                if (!ExportNodes.IsServerPath(model.Path))
                    return (File.Exists(model.Path) ? File.GetLastWriteTime(model.Path) : (DateTime?)null, true);
                var parts = model.Path.Trim().Substring("RSN://".Length).Split('/');
                var year = model.Year > 0 ? model.Year : revitYear;
                var info = await RevitServerClient.ModelInfoAsync(parts[0], year, parts.Skip(1).Take(parts.Length - 2).ToList(), parts[parts.Length - 1])
                    .ConfigureAwait(false);
                return (info.DateModified, true);
            }
            catch (Exception)
            {
                return (null, false);
            }
        }
    }
}
