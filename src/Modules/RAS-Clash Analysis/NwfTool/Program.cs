using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using RASv2.ClashAnalysis.Logic;
#if NAVISWORKS
using Autodesk.Navisworks.Api.Automation;
#endif

namespace RASv2.ClashAnalysis.NwfBuild
{
    /// <summary>
    /// Сборка постоянных NWF (DECISIONS.md модуля, «Этап 2 — сборка NWF»). Аргумент — файл задания (NwfToolJob). Navisworks
    /// запускается один раз без окна и собирает NWF по очереди: NWF нет — из NWC строки; есть — открыть (Navisworks берёт NWC с
    /// диска такими, какие они сейчас), добавить NWC, которых в ней нет, сохранить на место. Скрытие, проверки, их результаты и
    /// статусы коллизий остаются в NWF. Ход — строками в стандартный вывод (NwfTool.StepLine / DoneLine), их читает модуль.
    /// </summary>
    internal static class Program
    {
        private static NwfToolJob _job;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                _job = NwfTool.Read<NwfToolJob>(args[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("задание не прочитано: " + ex.Message);
                return 2;
            }
            // Navisworks API — из папки Navisworks этого компьютера (с программой не копируется)
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = Path.Combine(_job.NavisworksDir ?? "", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            try
            {
                return Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ОШИБКА: " + ex);
                return 1;
            }
        }

#if NAVISWORKS
        // отдельно от Main: типы Navisworks загружаются только после того, как подключён поиск сборок в папке Navisworks
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run()
        {
            var clock = Stopwatch.StartNew();
            Say("Navisworks: " + _job.NavisworksDir + " — запуск без окна");
            using (var app = new NavisworksApplication())
            {
                app.DisableProgress();
                app.AddPluginAssembly(Assembly.GetExecutingAssembly().Location);
                Say("Navisworks запущен за " + Sec(clock));
                for (var i = 0; i < _job.Items.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_job.CancelFile) && File.Exists(_job.CancelFile))
                    {
                        Console.WriteLine(NwfTool.DoneLine(i, NwfTool.Skip, 0, "выгрузка остановлена"));
                        continue;
                    }
                    var one = Stopwatch.StartNew();
                    try
                    {
                        var note = One(app, i, _job.Items[i]);
                        Console.WriteLine(NwfTool.DoneLine(i, NwfTool.Ok, one.Elapsed.TotalSeconds, note));
                    }
                    catch (Exception ex)
                    {
                        Say("ОШИБКА: " + _job.Items[i].Nwf + " — " + ex);
                        Console.WriteLine(NwfTool.DoneLine(i, NwfTool.Fail, one.Elapsed.TotalSeconds,
                            ex is ToolError ? ex.Message : "Navisworks: " + ex.Message));
                    }
                }
            }
            Say("Navisworks закрыт; всего " + Sec(clock));
            return 0;
        }

        private static string One(NavisworksApplication app, int i, NwfToolItem item)
        {
            var nwf = item.Nwf;
            Say("── " + nwf);
            Console.WriteLine(NwfTool.StepLine(i, "Проверка NWC"));
            var present = item.Nwcs.Where(File.Exists).ToList();
            var missing = item.Nwcs.Where(n => !File.Exists(n)).ToList();
            foreach (var n in present) Say("   NWC: " + n + " — " + Size(n) + ", записана " + File.GetLastWriteTime(n).ToString("dd.MM.yyyy HH:mm"));
            foreach (var n in missing) Say("   NWC нет: " + n);
            if (present.Count == 0) throw new ToolError("нет ни одной NWC строки — сначала выгрузите NWC");

            var exists = File.Exists(nwf);
            var notes = new List<string>();
            if (exists)
            {
                // открыта у коллеги — сохранить поверх не выйдет; копия — до любых изменений
                try
                {
                    using (File.Open(nwf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException)
                {
                    throw new ToolError("NWF занята — открыта в Navisworks? Закройте её и повторите");
                }
                if (!string.IsNullOrEmpty(_job.BackupDir))
                {
                    Directory.CreateDirectory(_job.BackupDir);
                    var copy = Path.Combine(_job.BackupDir, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " " + Path.GetFileName(nwf));
                    File.Copy(nwf, copy, true);
                    Say("   копия NWF до обновления: " + copy);
                }
                Console.WriteLine(NwfTool.StepLine(i, "Открытие NWF"));
                var c = Stopwatch.StartNew();
                app.OpenFile(nwf);
                Say("   NWF открыта за " + Sec(c) + " (NWC — с диска, какие сейчас)");

                Console.WriteLine(NwfTool.StepLine(i, "Модели в NWF"));
                var list = Path.Combine(Path.GetTempPath(), "ras-nwf-models-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    app.ExecuteAddInPlugin(NwfTool.ModelsPlugin, list);
                    var inside = File.Exists(list) ? File.ReadAllLines(list, Encoding.UTF8).ToList() : new List<string>();
                    Say("   в NWF моделей: " + inside.Count);
                    foreach (var m in inside) Say("     · " + m);
                    NwfTool.Diff(inside, present, out var append, out var elsewhere, out var extra);
                    foreach (var n in elsewhere)
                    {
                        Say("   NWC с тем же именем — в NWF из другой папки, не добавлена: " + n);
                        notes.Add("в NWF «" + Path.GetFileName(n) + "» из другой папки");
                    }
                    foreach (var n in extra)
                    {
                        Say("   в NWF есть модель, которой нет у строки (не убирается — уберите в Navisworks руками): " + n);
                        notes.Add("лишняя модель в NWF: " + Path.GetFileName(n));
                    }
                    foreach (var n in append)
                    {
                        Console.WriteLine(NwfTool.StepLine(i, "Добавить " + Path.GetFileName(n)));
                        c.Restart();
                        app.AppendFile(n);
                        Say("   добавлена: " + n + " — " + Sec(c));
                    }
                    if (append.Count > 0) notes.Add("добавлено NWC: " + append.Count);
                }
                finally
                {
                    if (File.Exists(list)) File.Delete(list);
                }
            }
            else
            {
                Console.WriteLine(NwfTool.StepLine(i, "Сборка: " + Path.GetFileName(present[0])));
                var c = Stopwatch.StartNew();
                app.OpenFile(present[0]);
                Say("   открыта: " + Path.GetFileName(present[0]) + " — " + Sec(c));
                foreach (var n in present.Skip(1))
                {
                    Console.WriteLine(NwfTool.StepLine(i, "Сборка: " + Path.GetFileName(n)));
                    c.Restart();
                    app.AppendFile(n);
                    Say("   добавлена: " + Path.GetFileName(n) + " — " + Sec(c));
                }
            }

            Console.WriteLine(NwfTool.StepLine(i, "Сохранение NWF"));
            Directory.CreateDirectory(Path.GetDirectoryName(nwf));
            var save = Stopwatch.StartNew();
            app.SaveFile(nwf);
            if (!File.Exists(nwf)) throw new ToolError("Navisworks не записал NWF (сохранение прошло без ошибки, а файла нет)");
            Say("   NWF записана: " + nwf + " — " + Size(nwf) + ", " + Sec(save));
            if (missing.Count > 0) notes.Add("нет NWC: " + missing.Count);
            return (exists ? "обновлена" : "собрана") + ": NWC " + present.Count + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        }
#else
        private static int Run()
        {
            Console.WriteLine("ОШИБКА: программа собрана без Navisworks 2023 — сборку NWF она не делает");
            for (var i = 0; i < _job.Items.Count; i++)
                Console.WriteLine(NwfTool.DoneLine(i, NwfTool.Fail, 0, "программа сборки NWF собрана без Navisworks — нужна сборка плагина с компьютера, где он есть"));
            return 1;
        }
#endif

        private static void Say(string s) => Console.WriteLine("  " + s);

        private static string Sec(Stopwatch c) => c.Elapsed.TotalSeconds.ToString("0.0") + " с";

        private static string Size(string file)
        {
            var b = new FileInfo(file).Length;
            return b >= 1048576 ? (b / 1048576.0).ToString("0.0") + " МБ" : Math.Max(1, b / 1024) + " КБ";
        }

        /// <summary>Понятная ошибка — человеку как есть (без «Navisworks: …»).</summary>
        private sealed class ToolError : Exception
        {
            public ToolError(string message) : base(message) { }
        }
    }
}
