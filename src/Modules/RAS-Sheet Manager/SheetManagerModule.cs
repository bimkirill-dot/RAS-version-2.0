using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.SheetManager
{
    /// <summary>
    /// Модуль «RAS-Sheet Manager» — инструменты для листов (панель «Менеджер листов»; MODULE.md, DECISIONS.md).
    /// Ядро находит его в RASv2.SheetManager.dll и запускает.
    /// </summary>
    public sealed class SheetManagerModule : IModule
    {
        public string Id => "SheetManager";
        public string Title => "RAS-Sheet Manager";

        // Revit 2023 и 2026; различия версий — в ядре (RevitCompat)
        public IReadOnlyList<int> RevitYears => new[] { 2023, 2026 };

        private SignApp _app;

        public void Start(IModuleContext context)
        {
            _app = new SignApp();
            _app.Start(context);
        }

        public void Stop() { }
    }
}
