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
            Say("── " + nwf + (item.ReportsOnly ? " — только отчёты" : ""));
            var exists = File.Exists(nwf);
            // только отчёты (08.11.2026): по готовой NWF — NWC не проверяются и не добавляются, скрытие не делается
            if (item.ReportsOnly && !exists) throw new ToolError("NWF ещё нет — отчёты делаются по готовой NWF; включите «NWF», чтобы собрать её");
            var present = item.ReportsOnly ? new List<string>() : item.Nwcs.Where(File.Exists).ToList();
            var missing = item.ReportsOnly ? new List<string>() : item.Nwcs.Where(n => !File.Exists(n)).ToList();
            if (!item.ReportsOnly)
            {
                Console.WriteLine(NwfTool.StepLine(i, "Проверка NWC"));
                foreach (var n in present) Say("   NWC: " + n + " — " + Size(n) + ", записана " + File.GetLastWriteTime(n).ToString("dd.MM.yyyy HH:mm"));
                foreach (var n in missing) Say("   NWC нет: " + n);
                if (present.Count == 0) throw new ToolError("нет ни одной NWC строки — сначала выгрузите NWC");
            }

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
            }
            if (exists && !item.ReportsOnly)
            {
                var c = Stopwatch.StartNew();
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
            else if (!exists)
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

            // скрыть — до проверок (разработчик, 07.11.2026: «сначала скрыть по загруженным условиям, потом проверка»; Navisworks скрытое
            // в проверки не берёт — проверено им); ничего не нашлось — пропуск («что поделать»)
            if (item.HideSets.Count > 0 && !item.ReportsOnly) Hide(app, i, item, notes);
            if (item.Tests.Count > 0) Tests(app, i, item, notes);
            // отчёты — после прогона, по каждому формату (разработчик, 08.11.2026)
            if (item.Tests.Count > 0 && item.Reports.Count > 0) Reports(app, i, item, notes);

            Console.WriteLine(NwfTool.StepLine(i, "Сохранение NWF"));
            Directory.CreateDirectory(Path.GetDirectoryName(nwf));
            var save = Stopwatch.StartNew();
            app.SaveFile(nwf);
            if (!File.Exists(nwf)) throw new ToolError("Navisworks не записал NWF (сохранение прошло без ошибки, а файла нет)");
            Say("   NWF записана: " + nwf + " — " + Size(nwf) + ", " + Sec(save));
            if (missing.Count > 0) notes.Add("нет NWC: " + missing.Count);
            if (item.ReportsOnly) return "только отчёты" + (notes.Count > 0 ? ": " + string.Join("; ", notes) : "");
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

        /// <summary>
        /// Скрыть по наборам «Скрыть»: встроенный импорт наборов Navisworks → модуль программы находит каждый набор по имени, скрывает
        /// найденное и убирает сам набор (в NWF его не остаётся — при следующем обновлении не задвоится).
        /// </summary>
        private static void Hide(NavisworksApplication app, int i, NwfToolItem item, List<string> notes)
        {
            Console.WriteLine(NwfTool.StepLine(i, "Скрытие по наборам поиска"));
            var names = item.HideSets.Select(x => (string)System.Xml.Linq.XElement.Parse(x).Attribute("name") ?? "").ToList();
            var sets = Path.Combine(Path.GetTempPath(), "ras-nwf-sets-" + Guid.NewGuid().ToString("N") + ".xml");
            var result = Path.Combine(Path.GetTempPath(), "ras-nwf-hide-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                // шапка — как у файла экспорта Navisworks (единицы и схема): без неё встроенный импорт молча ничего не загружает?
                File.WriteAllText(sets, "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<exchange xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                                        "xsi:noNamespaceSchemaLocation=\"http://download.autodesk.com/us/navisworks/schemas/nw-exchange-12.0.xsd\" units=\"ft\">" +
                                        "<selectionsets>" + string.Join("", item.HideSets) + "</selectionsets></exchange>", new UTF8Encoding(false));
                var c = Stopwatch.StartNew();
                app.ExecuteAddInPlugin(NwfTool.ImportSets, sets);
                app.ExecuteAddInPlugin(NwfTool.HidePlugin, new[] { result }.Concat(names).ToArray());
                var lines = File.Exists(result) ? File.ReadAllLines(result, Encoding.UTF8) : new string[0];
                var hidden = 0;
                foreach (var line in lines)
                {
                    var parts = line.Split('\t');
                    var n = parts.Length > 1 && int.TryParse(parts[1], out var x) ? x : -1;
                    Say("   скрыть «" + parts[0] + "»: " + (n < 0 ? "набор не загрузился" + (parts.Length > 2 ? " (наборы в NWF: " + (parts[2].Length == 0 ? "нет" : parts[2]) + ")" : "")
                        : n == 0 ? "ничего не найдено — пропуск" : "скрыто " + n));
                    if (n > 0) hidden += n;
                }
                Say("   скрытие — " + Sec(c));
                notes.Add("скрыто: " + hidden);
            }
            finally
            {
                foreach (var f in new[] { sets, result }) if (File.Exists(f)) File.Delete(f);
            }
        }

        /// <summary>
        /// Проверки: в NWF их нет — загрузить из файлов строки (встроенный импорт Clash Detective; копию — импорт может удалить файл);
        /// есть — не загружать заново (у коллизий остаются статусы и комментарии); затем — прогнать все, итог по каждой — в журнал.
        /// </summary>
        private static void Tests(NavisworksApplication app, int i, NwfToolItem item, List<string> notes)
        {
            var info = Path.Combine(Path.GetTempPath(), "ras-nwf-tests-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                if (item.ReplaceTests)
                {
                    // файл проверок сменился, человек согласился — прежние проверки (с их результатами) — убрать
                    Console.WriteLine(NwfTool.StepLine(i, "Замена проверок"));
                    app.ExecuteAddInPlugin(NwfTool.TestsPlugin, info, "clear");
                    Say("   прежние проверки убраны — файл проверок сменился (подтверждено)");
                    notes.Add("проверки заменены");
                }
                app.ExecuteAddInPlugin(NwfTool.TestsPlugin, info, "count");
                var have = File.Exists(info) && int.TryParse(File.ReadAllText(info).Trim(), out var n) ? n : 0;
                if (have > 0) Say("   проверок в NWF: " + have + " — заново не загружаются (статусы и комментарии коллизий остаются), только прогон");
                else
                    foreach (var file in item.Tests)
                    {
                        Console.WriteLine(NwfTool.StepLine(i, "Загрузка проверок: " + Path.GetFileName(file)));
                        if (!File.Exists(file))
                        {
                            Say("   файла проверок нет: " + file);
                            notes.Add("нет файла проверок: " + Path.GetFileName(file));
                            continue;
                        }
                        // копией (импорт может удалить файл) с тем же именем: Navisworks дописывает имя файла к своим наборам проверок
                        var dir = Path.Combine(Path.GetTempPath(), "ras-nwf-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(dir);
                        var copy = Path.Combine(dir, Path.GetFileName(file));
                        File.Copy(file, copy, true);
                        try
                        {
                            app.ExecuteAddInPlugin(NwfTool.ImportTests, copy);
                            Say("   загружены проверки: " + file);
                        }
                        finally
                        {
                            try { Directory.Delete(dir, true); } catch (Exception) { /* временная — не важно */ }
                        }
                    }
                Console.WriteLine(NwfTool.StepLine(i, "Прогон проверок"));
                var c = Stopwatch.StartNew();
                app.ExecuteAddInPlugin(NwfTool.TestsPlugin, info, "run");
                var lines = File.Exists(info) ? File.ReadAllLines(info, Encoding.UTF8) : new string[0];
                int tests = 0, total = 0, fresh = 0;
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    tests++;
                    total += int.TryParse(p[2], out var t) ? t : 0;
                    fresh += int.TryParse(p[3], out var f) ? f : 0;
                    Say($"   проверка «{p[0]}»: {p[1]}, коллизий {p[2]}, новых {p[3]}");
                }
                Say("   прогон — " + Sec(c));
                notes.Add($"проверок {tests}, коллизий {total}, новых {fresh}");
            }
            finally
            {
                if (File.Exists(info)) File.Delete(info);
            }
        }

        /// <summary>Отчёты Clash Detective: по формату — в его папку (модуль программы ReportPlugin); не вышло — пометка, NWF всё равно сохраняется.</summary>
        private static void Reports(NavisworksApplication app, int i, NwfToolItem item, List<string> notes)
        {
            var made = 0;
            foreach (var r in item.Reports)
            {
                Console.WriteLine(NwfTool.StepLine(i, "Отчёт: " + r.Format));
                var result = Path.Combine(Path.GetTempPath(), "ras-nwf-report-" + Guid.NewGuid().ToString("N") + ".txt");
                var options = Path.Combine(Path.GetTempPath(), "ras-nwf-report-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var c = Stopwatch.StartNew();
                    NwfTool.Write(options, item.ReportOptions ?? ReportOptions.Defaults());
                    app.ExecuteAddInPlugin(NwfTool.ReportPlugin, result, r.Folder, r.Format, options);
                    var lines = File.Exists(result) ? File.ReadAllLines(result, Encoding.UTF8) : new[] { "ошибка: модуль отчётов не ответил" };
                    var ok = lines.FirstOrDefault() == "ok";
                    var files = Directory.Exists(r.Folder) ? Directory.GetFiles(r.Folder, "*", SearchOption.AllDirectories).Length : 0;
                    Say($"   отчёт «{r.Format}»: " + (ok ? $"записан в {r.Folder} — файлов {files}, {Sec(c)}" : lines.FirstOrDefault()));
                    foreach (var l in lines.Skip(1)) Say("     " + l);
                    if (ok) made++;
                    else notes.Add("отчёт «" + r.Format + "» не записан");
                }
                finally
                {
                    foreach (var f in new[] { result, options }) if (File.Exists(f)) File.Delete(f);
                }
            }
            notes.Add("отчётов: " + made + " из " + item.Reports.Count);
        }

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
