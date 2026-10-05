using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RASv2.Loader
{
    /// <summary>
    /// Точка входа плагина в Revit (манифест RASv2.addin). Выбирает сборку, запускает из её локальной копии ядро
    /// и больше ничего не делает (PRINCIPLES.md, раздел 2). Меняется редко: ошибка здесь чинится только переустановкой.
    /// </summary>
    public sealed class LoaderApp : IExternalApplication
    {
#if REVIT2023
        private const int Year = 2023;
#else
        private const int Year = 2026;
#endif
        private const string CoreHost = "RASv2.Core.Host";

        private static string _buildDir;
        private static MethodInfo _stop;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var settings = LoaderConfig.Load(Year);
                var choice = BuildResolver.Resolve(settings, LoaderLog.Write);
                if (choice.Dir == null)
                {
                    LoaderLog.Write("Нет ни одной сборки (" + choice.Note + ") — плагин не запущен");
                    return Result.Succeeded;
                }
                LoaderLog.Write("Запуск сборки " + choice.BuildId + ", канал «" + choice.Channel + "»" +
                                (choice.Note == null ? "" : " (" + choice.Note + ")"));
                try
                {
                    StartCore(application, choice);
                    BuildResolver.MarkGood(settings.CacheRoot, choice.BuildId);
                }
                catch (Exception ex)
                {
                    // повторить с другой сборкой в этом же запуске нельзя: сборки с теми же именами уже загружены
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    LoaderLog.Write("Сборка " + choice.BuildId + " не запустилась", inner);
                    BuildResolver.MarkBad(choice.Dir);
                    TaskDialog.Show("RAS v. 2.0",
                        "Плагин не запустился. При следующем запуске Revit будет взята предыдущая версия.\n\n" + inner.Message);
                }
                BuildResolver.Cleanup(settings.CacheRoot, choice.BuildId);
            }
            catch (Exception ex)
            {
                LoaderLog.Write("Ошибка загрузчика", ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { _stop?.Invoke(null, new object[] { application }); }
            catch (Exception ex) { LoaderLog.Write("Ошибка при остановке ядра", ex); }
            return Result.Succeeded;
        }

        private static void StartCore(UIControlledApplication application, BuildChoice choice)
        {
            _buildDir = choice.Dir;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromBuild;
            var core = LoadAssembly(Path.Combine(choice.Dir, BuildResolver.EntryAssembly));
            var host = core.GetType(CoreHost, true);
            _stop = host.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static);
            var start = host.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)
                        ?? throw new MissingMethodException(CoreHost, "Start");
            start.Invoke(null, new object[] { application, choice.BuildId, choice.Dir, choice.Channel, choice.Note, Year });
        }

        /// <summary>Сборки плагина и их зависимости — из папки текущей копии.</summary>
        private static Assembly ResolveFromBuild(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            var path = Path.Combine(_buildDir ?? "", name + ".dll");
            return File.Exists(path) ? LoadAssembly(path) : null;
        }

        /// <summary>
        /// Загрузить сборку плагина. В процессе Revit должна быть одна её копия: в Revit 2026 загрузчик может жить
        /// в своём контексте загрузки, и сборки плагина грузятся туда же, где сам загрузчик.
        /// </summary>
        internal static Assembly LoadAssembly(string path)
        {
#if NET
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(LoaderApp).Assembly)
                          ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            foreach (var loaded in context.Assemblies)
                if (string.Equals(loaded.Location, path, StringComparison.OrdinalIgnoreCase)) return loaded;
            return context.LoadFromAssemblyPath(path);
#else
            return Assembly.LoadFrom(path);
#endif
        }
    }
}
