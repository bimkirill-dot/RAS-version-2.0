using System;
using System.IO;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>Контекст одного модуля: его панель, папки и журнал.</summary>
    internal sealed class ModuleContext : IModuleContext
    {
        private readonly IModule _module;

        public ModuleContext(IModule module, UIControlledApplication application, RibbonPanel panel, RevitEventHub events)
        {
            _module = module;
            Application = application;
            Panel = panel;
            Log = new ModuleLog(module.Id);
            Events = new ModuleEvents(events, module.Id);
        }

        public UIControlledApplication Application { get; }
        public string BuildId => Host.BuildId;
        public int RevitYear => Host.RevitYear;
        public RibbonPanel Panel { get; }
        public ModuleLog Log { get; }
        public IRevitEvents Events { get; }
        public string LocalDataDir => Path.Combine(RasPaths.Local, _module.Title);
        public string UserDataDir => Path.Combine(RasPaths.User, _module.Title);
        public string SharedDataDir => Path.Combine(RasPaths.SharedData, _module.Title);

        public PushButton AddButton(string name, string text, Type command, string toolTip)
        {
            // команда указывает на ту сборку, из которой модуль уже загружен, — в процессе остаётся одна её копия
            var data = new PushButtonData(_module.Id + "." + name, text, command.Assembly.Location, command.FullName)
            {
                ToolTip = toolTip
            };
            return (PushButton)Panel.AddItem(data);
        }
    }
}
