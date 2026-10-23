using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Navisworks.Api.Plugins;
using RASv2.Loader;

namespace RASv2.Navisworks.Loader
{
    /// <summary>
    /// Вкладка «RAS v. 2.0» в Navisworks и её кнопки (разметка — Ribbon\RASv2Ribbon.xaml). Нажали кнопку — загрузчик берёт сборку
    /// канала (как загрузчик Revit: BuildResolver, Releases\Navisworks\2023) один раз за сеанс Navisworks, копирует её к себе и
    /// вызывает её вход: Entry.Execute(команда, сборка, папка, канал). Своей логики не содержит; меняется редко — ошибка здесь
    /// чинится только переустановкой (как у загрузчика Revit, PRINCIPLES.md, раздел 2).
    /// </summary>
    [Plugin("RASv2.Navisworks", "RAS", DisplayName = "RAS v. 2.0")]
    [RibbonLayout("RASv2Ribbon.xaml")]
    [RibbonTab("RASv2Tab", DisplayName = "RAS v. 2.0")]
    [Command("ClashAnalysis.FitViews", DisplayName = "Подстроить виды",
        ToolTip = "Подобрать вид каждой коллизии во всех проверках файла: оба элемента видны и не закрывают друг друга",
        CanToggle = false, CallCanExecute = CallCanExecute.Always)]
    public sealed class LoaderPlugin : CommandHandlerPlugin
    {
        public const int Year = 2023;
        private const string EntryType = "RASv2.ClashAnalysis.Navisworks.Entry";

        private static MethodInfo _entry;
        private static BuildChoice _choice;

        public override int ExecuteCommand(string name, params string[] parameters)
        {
            try
            {
                var entry = Entry();
                if (entry == null) return 0;
                return (int)entry.Invoke(null, new object[] { name, _choice.BuildId, _choice.Dir, _choice.Channel });
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                NavisLog.Write("Команда «" + name + "» не выполнена", inner);
                MessageBox.Show("Не удалось выполнить команду: " + inner.Message + Environment.NewLine + Environment.NewLine +
                                "Подробности — в журнале: " + NavisLog.Path, "RAS v. 2.0", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 0;
            }
        }

        public override CommandState CanExecuteCommand(string name) => new CommandState(true);

        public override bool CanExecuteRibbonTab(string name) => true;

        /// <summary>Сборка канала — один раз за сеанс Navisworks (как у Revit — при запуске; здесь — при первом нажатии).</summary>
        private static MethodInfo Entry()
        {
            if (_entry != null) return _entry;
            var settings = NavisLoaderConfig.Load();
            _choice = BuildResolver.Resolve(settings, NavisLog.Write);
            if (_choice.Dir == null)
            {
                NavisLog.Write("Сборки нет: " + _choice.Note);
                MessageBox.Show("Нет сборки RAS v. 2.0 для Navisworks: " + _choice.Note, "RAS v. 2.0", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            NavisLog.Write("Запуск сборки " + _choice.BuildId + ", канал «" + _choice.Channel + "»" + (_choice.Note != null ? " (" + _choice.Note + ")" : ""));
            var asm = Assembly.LoadFrom(Path.Combine(_choice.Dir, BuildResolver.EntryAssembly));
            var type = asm.GetType(EntryType, throwOnError: true);
            _entry = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(EntryType, "Execute");
            BuildResolver.MarkGood(settings.CacheRoot, _choice.BuildId);
            return _entry;
        }
    }

    /// <summary>Иконки кнопок — от папки самой сборки (в пакете — Contents\icons).</summary>
    public static class LoaderIcons
    {
        private static string Dir => Path.Combine(Path.GetDirectoryName(typeof(LoaderIcons).Assembly.Location) ?? "", "icons");

        public static ImageSource FitViews16 => Load("FitViews16.png");
        public static ImageSource FitViews32 => Load("FitViews32.png");

        private static ImageSource Load(string file)
        {
            try { return new BitmapImage(new Uri(Path.Combine(Dir, file))); }
            catch (Exception) { return null; } // нет иконки — кнопка без картинки, но работает
        }
    }

    /// <summary>
    /// Откуда брать сборку: сервер (Releases\Navisworks\2023) или — на компьютере разработчика — папка исходников
    /// (%AppData%\RAS_v_2.0\navisworks-loader.ini: releases=…, channel=dev; пишет navisworks.cmd).
    /// </summary>
    internal static class NavisLoaderConfig
    {
        public static string IniPath => Path.Combine(RasPaths.User, "navisworks-loader.ini");

        public static LoaderSettings Load()
        {
            var s = new LoaderSettings
            {
                ReleasesRoot = Path.Combine(RasPaths.Releases, "Navisworks"),
                TestersFile = Path.Combine(RasPaths.Config, "test-users.txt"),
                CacheRoot = Path.Combine(RasPaths.Local, "Navisworks", "Builds", LoaderPlugin.Year.ToString()),
                Year = LoaderPlugin.Year,
                UserName = Environment.UserName
            };
            if (!File.Exists(IniPath)) return s;
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;
                if (key == "releases") s.ReleasesRoot = value;
                else if (key == "channel") s.Channel = value;
            }
            return s;
        }
    }

    /// <summary>Журнал Navisworks-части: %LocalAppData%\RAS_v_2.0\Logs\navisworks.log (общий с её сборкой).</summary>
    public static class NavisLog
    {
        public static string Path => System.IO.Path.Combine(RasPaths.Logs, "navisworks.log");

        public static void Write(string text) => Write(text, null);

        public static void Write(string text, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(RasPaths.Logs);
                var line = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " [Загрузчик] " + text + Environment.NewLine + (ex != null ? ex + Environment.NewLine : "");
                File.AppendAllText(Path, line, new UTF8Encoding(false));
            }
            catch (Exception) { /* журнал не пишется — не повод ломать Navisworks */ }
        }
    }
}
