using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.BlockMirrorRotation
{
    /// <summary>
    /// Модуль «RAS-Block Mirror Rotation» — отражение блока КЖ без поломки оформления на видах (MODULE.md, DECISIONS.md).
    /// Ядро находит его в RASv2.BlockMirrorRotation.dll и запускает.
    /// </summary>
    public sealed class BlockMirrorRotationModule : IModule
    {
        public string Id => "BlockMirrorRotation";
        public string Title => "RAS-Block Mirror Rotation";

        // Revit 2023 и 2026; различия версий — в ядре (RevitCompat)
        public IReadOnlyList<int> RevitYears => new[] { 2023, 2026 };

        private MirrorApp _app;

        public void Start(IModuleContext context)
        {
            _app = new MirrorApp();
            _app.Start(context);
        }

        public void Stop() { }
    }
}
