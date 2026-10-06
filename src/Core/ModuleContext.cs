using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>Контекст одного модуля: его кнопки, папки и журнал.</summary>
    internal sealed class ModuleContext : IModuleContext
    {
        private readonly IModule _module;

        public ModuleContext(IModule module, UIControlledApplication application, RevitEventHub events)
        {
            _module = module;
            Application = application;
            Log = new ModuleLog(module.Id);
            Events = new ModuleEvents(events, module.Id);
        }

        public UIControlledApplication Application { get; }
        public string BuildId => Host.BuildId;
        public int RevitYear => Host.RevitYear;
        public ModuleLog Log { get; }
        public IRevitEvents Events { get; }
        public string LocalDataDir => Path.Combine(RasPaths.Local, _module.Title);
        public string UserDataDir => Path.Combine(RasPaths.User, _module.Title);
        public string SharedDataDir => Path.Combine(RasPaths.SharedData, _module.Title);

        /// <summary>Объявленные кнопки — ядро ставит их на ленту, когда модуль запустился.</summary>
        public List<PushButtonData> Buttons { get; } = new List<PushButtonData>();

        public PushButtonData AddButton(string name, string text, Type command, string toolTip, string icon = null)
        {
            // команда указывает на ту сборку, из которой модуль уже загружен, — в процессе остаётся одна её копия
            var data = new PushButtonData(_module.Id + "." + name, text, command.Assembly.Location, command.FullName)
            {
                ToolTip = toolTip
            };
            if (icon != null)
            {
                data.LargeImage = RibbonIcons.Load(command.Assembly, icon, 32);
                data.Image = RibbonIcons.Load(command.Assembly, icon, 16);
            }
            Buttons.Add(data);
            return data;
        }
    }
}
