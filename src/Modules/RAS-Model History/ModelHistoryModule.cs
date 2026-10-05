using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.ModelHistory
{
    /// <summary>
    /// Модуль «RAS-Model History» — журнал изменений модели (MODULE.md). Ядро находит его в RASv2.ModelHistory.dll,
    /// создаёт панель на вкладке и запускает.
    /// </summary>
    public sealed class ModelHistoryModule : IModule
    {
        public string Id => "ModelHistory";
        public string Title => "RAS-Model History";

        // Revit 2023 — после переноса на .NET Framework (MODULE.md, шаг 6). До тех пор под 2023 собирается только этот файл,
        // и «О плагине» в Revit 2023 показывает, что модуль там не работает
        public IReadOnlyList<int> RevitYears => new[] { 2026 };

#if !REVIT2023
        private App _app;

        public void Start(IModuleContext context)
        {
            _app = new App();
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
