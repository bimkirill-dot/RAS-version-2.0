using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.ClashAnalysis
{
    /// <summary>
    /// Модуль «RAS-Clash Analysis» в Revit — мост для сайта коллизий: «Показать в Revit» (MODULE.md, DECISIONS.md).
    /// Ядро находит его в RASv2.ClashAnalysis.dll и запускает.
    /// </summary>
    public sealed class ClashAnalysisModule : IModule
    {
        public string Id => "ClashAnalysis";
        public string Title => "RAS-Clash Analysis";

        // Revit 2023 и 2026 (19.10.2026: сначала только 2026, в тот же день — и 2023); различия версий — в ядре (RevitCompat)
        public IReadOnlyList<int> RevitYears => new[] { 2023, 2026 };

        private BridgeApp _app;

        public void Start(IModuleContext context)
        {
            _app = new BridgeApp();
            _app.Start(context);
        }

        public void Stop() => _app?.Stop();
    }
}
