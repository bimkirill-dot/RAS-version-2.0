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

        // Revit 2023 — позже (19.10.2026: «только для 2026 пока»); под 2023 собирается только этот файл
        public IReadOnlyList<int> RevitYears => new[] { 2026 };

#if !REVIT2023
        private BridgeApp _app;

        public void Start(IModuleContext context)
        {
            _app = new BridgeApp();
            _app.Start(context);
        }

        public void Stop() => _app?.Stop();
#else
        public void Start(IModuleContext context)
        {
        }

        public void Stop()
        {
        }
#endif
    }
}
