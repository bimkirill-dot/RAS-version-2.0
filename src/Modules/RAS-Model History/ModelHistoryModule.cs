using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.Core;

namespace RASv2.ModelHistory
{
    /// <summary>
    /// Модуль «RAS-Model History» — журнал изменений модели. Пока заготовка: код журнала переносится
    /// из прежнего отдельного плагина следующими шагами (MODULE.md).
    /// </summary>
    public sealed class ModelHistoryModule : IModule
    {
        public string Id => "ModelHistory";
        public string Title => "RAS-Model History";

        // заготовка без Revit API, поэтому запускается в обеих версиях — так проверяется загрузчик и в Revit 2023;
        // после переноса кода — только 2026, пока журнал не перенесён на Revit 2023
        public IReadOnlyList<int> RevitYears => new[] { 2023, 2026 };

        internal static IModuleContext Context { get; private set; }

        public void Start(IModuleContext context)
        {
            Context = context;
            context.AddButton("Check", "Проверка\nмодуля", typeof(CheckCommand),
                "Заготовка модуля: показывает, что модуль запущен и работает с той же копией ядра, что и загрузчик");
            context.Log.Info("Модуль запущен");
        }

        public void Stop()
        {
        }
    }

    /// <summary>Временная кнопка: проверка схемы загрузки (модуль и ядро — те же копии, что запустил загрузчик).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var ok = Host.IsStarted && ModelHistoryModule.Context != null;
            var text = ok
                ? "Модуль запущен и работает с той же копией ядра, что и загрузчик.\n\nСборка " + Host.BuildId +
                  ", Revit " + Host.RevitYear + ".\nМодуль: " + typeof(CheckCommand).Assembly.Location
                : "⚠ Кнопка вызвала другую копию сборки, чем запустил загрузчик. Сообщите разработчику.\n\n" +
                  "Модуль: " + typeof(CheckCommand).Assembly.Location + "\n" + AboutDiagnostics();
            TaskDialog.Show("RAS-Model History", text);
            return Result.Succeeded;
        }

        private static string AboutDiagnostics()
        {
            var asm = typeof(Host).Assembly;
            var text = "Ядро: " + asm.Location;
#if NET
            text += "; контекст загрузки: " + System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name;
#endif
            return text;
        }
    }
}
