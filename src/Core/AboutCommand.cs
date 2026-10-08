using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>«О плагине»: сборка, канал, модули и их состояние, журнал ошибок.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var text = new StringBuilder();
            if (!Host.IsStarted)
            {
                // кнопка попала во вторую копию сборки ядра — схема загрузки сломана, это надо чинить
                text.AppendLine("⚠ Кнопка вызвала другую копию ядра, чем запустил загрузчик. Сообщите разработчику.");
                text.AppendLine(Diagnostics());
                text.AppendLine();
            }
            text.AppendLine("Сборка " + Host.BuildId + ", канал «" + ChannelText(Host.Channel) + "», Revit " + Host.RevitYear + ".");
            if (Host.LoaderNote != null) text.AppendLine("Загрузчик: " + Host.LoaderNote + ".");
            if (Host.ConfigNote != null) text.AppendLine("Общие настройки модулей: " + Host.ConfigNote + ".");
            text.AppendLine();
            text.AppendLine("Модули:");
            foreach (var slot in Host.Modules) text.AppendLine("• " + slot.Title + " — " + slot.State);
            if (Host.Modules.Count == 0) text.AppendLine("• нет");
            if (RibbonBuilder.CurrentDepartment != null)
            {
                text.AppendLine();
                text.AppendLine("Отдел на ленте: «" + RibbonBuilder.CurrentDepartment + "».");
            }

            // коды кнопок и подписи модулей — для раскладки ленты (ribbon.txt)
            text.AppendLine();
            text.AppendLine("Кнопки (код — подпись):");
            foreach (var button in Host.DeclaredButtons)
                text.AppendLine("• " + button.Key + " — «" + button.Value.Replace("\n", " ") + "»");

            var dialog = new TaskDialog("RAS v. 2.0")
            {
                MainInstruction = "RAS v. 2.0",
                MainContent = text.ToString().TrimEnd(),
                FooterText = "Журнал ошибок: " + Log.FilePath
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Показать журнал ошибок",
                "Откроется папка с файлом — его можно переслать разработчику");
            if (dialog.Show() == TaskDialogResult.CommandLink1) ShowLog();
            return Result.Succeeded;
        }

        private static string ChannelText(string channel)
        {
            switch (channel)
            {
                case "all": return "все";
                case "test": return "тест";
                case "dev": return "разработка";
                default: return channel ?? "?";
            }
        }

        /// <summary>Где лежит эта копия ядра — чтобы понять, откуда взялась вторая.</summary>
        internal static string Diagnostics()
        {
            var asm = typeof(Host).Assembly;
            var text = "Ядро: " + asm.Location;
#if NET
            text += "; контекст загрузки: " + System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name;
#endif
            return text;
        }

        private static void ShowLog()
        {
            try
            {
                if (File.Exists(Log.FilePath)) Process.Start("explorer.exe", "/select,\"" + Log.FilePath + "\"");
                else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Log.FilePath) + "\"");
            }
            catch (Exception ex)
            {
                Log.Write("Ядро", "Не удалось открыть папку журнала", ex);
            }
        }
    }
}
