using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: «Задачи выгрузки» — дерево (ExportTask): номера, перенос, краткая информация; имя NWC и папки с метками (ExportNames).</summary>
    internal static class ClashExportTaskTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        // пути — свои у каждого блока: одна модель — один раз на проект
        private static List<ExportModel> Models(int n, string block = "S6") =>
            Enumerable.Range(1, n).Select(i => new ExportModel { Path = "0000_01_01_" + block + "_KJ0" + i + "_01_M3.rvt" }).ToList();

        // Арман Кала → Блок 1 (6 моделей: Высокая, Средняя), Блок 2 (5); ЖК Нурлы (12)
        private static List<ExportTask> Sample(out ExportTask block1, out ExportTask high)
        {
            var project = new ExportTask { Name = "Арман Кала" };
            block1 = project.Add(new ExportTask { Name = "Блок 1", ModelFiles = Models(6) });
            high = block1.Add(new ExportTask { Name = "Высокая критичность" });
            block1.Add(new ExportTask { Name = "Средняя критичность" });
            project.Add(new ExportTask { Name = "Блок 2", ModelFiles = Models(5, "S7") });
            return new List<ExportTask> { project, new ExportTask { Name = "ЖК Нурлы", ModelFiles = Models(12, "S1") } };
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("номера строк — сами, по уровням; свёрнутый узел — без вложенных", () =>
            {
                var roots = Sample(out var block1, out _);
                var rows = ExportTask.Rows(roots, new HashSet<ExportTask>());
                var numbers = string.Join(" ", rows.Select(r => r.Number + ":" + r.Level));
                Check(numbers == "1:0 1.1:1 1.1.1:2 1.1.2:2 1.2:1 2:0", numbers);
                rows = ExportTask.Rows(roots, new HashSet<ExportTask> { block1 });
                numbers = string.Join(" ", rows.Select(r => r.Number));
                Check(numbers == "1 1.1 1.2 2", "свёрнут блок 1: " + numbers);
            });
            yield return Case("перенос — только внутри своего уровня, номера пересчитываются", () =>
            {
                var roots = Sample(out var block1, out var high);
                var block2 = roots[0].Children[1];
                Check(ExportTask.MoveTo(roots[0].Children, block2, block1), "блок 2 — на место блока 1");
                var rows = ExportTask.Rows(roots, new HashSet<ExportTask>());
                Check(rows[1].Task == block2 && rows[1].Number == "1.1" && rows[2].Task == block1 && rows[2].Number == "1.2",
                    string.Join(" ", rows.Select(r => r.Number + " " + r.Task.Name)));
                Check(!ExportTask.MoveTo(roots[0].Children, high, block2), "критичность — не на уровень блоков");
                Check(!ExportTask.MoveTo(roots, block1, roots[1]), "блок — не на уровень проектов");
            });
            yield return Case("моделей в выгрузке: свои, сумма вложенных, у критичности — модели блока", () =>
            {
                var roots = Sample(out var block1, out var high);
                Check(block1.AllModels == 6, "блок 1: " + block1.AllModels);
                Check(high.AllModels == 6, "критичность — модели блока: " + high.AllModels);
                Check(roots[0].AllModels == 11, "проект — сумма блоков: " + roots[0].AllModels);
                Check(new ExportTask().AllModels == 0, "пустая задача");
            });
            yield return Case("краткая информация: модели, выгрузка, не вышедшие, идёт у коллеги", () =>
            {
                var project = new ExportTask { Name = "ArmanQala" };
                var t = project.Add(new ExportTask { Name = "S6", ModelFiles = Models(6) });
                Check(t.Summary() == "6 моделей · не выгружалось", t.Summary());
                Check(project.Summary() == "1 задача · 6 моделей · не выгружалось", "у проекта — задачи: " + project.Summary());
                t.LastRun = new DateTime(2026, 10, 28, 14, 20, 0);
                Check(t.Summary() == "6 моделей · выгружено 28.10 14:20", t.Summary());
                t.Failed = 1;
                Check(t.Summary() == "6 моделей · 1 не вышла 28.10 14:20", t.Summary());
                t.Failed = 2;
                Check(t.Summary() == "6 моделей · 2 не вышли 28.10 14:20", t.Summary());
                t.RunningBy = "Иванов А.";
                t.RunningDone = 3;
                Check(t.Summary() == "идёт у Иванов А. — 3 из 6", t.Summary());
            });
            yield return Case("«модель / модели / моделей» по числу", () =>
            {
                var words = string.Join(" ", new[] { 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111 }.Select(n => n + " " + ExportTask.ModelsWord(n)));
                Check(words == "1 модель 2 модели 4 модели 5 моделей 11 моделей 12 моделей 14 моделей 21 модель 22 модели 25 моделей 111 моделей", words);
            });
            yield return Case("имя NWC: первые 7 частей, в последней — до пробела; частей меньше — как есть", () =>
            {
                string Nwc(string file, bool trim = true, int parts = 7) => ExportNames.NwcName(file, trim, parts, out _);
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt"));
                Check(Nwc("0000_01_01_S6_KJ01_01_M3_еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", Nwc("0000_01_01_S6_KJ01_01_M3_еще текст.rvt"));
                Check(Nwc(@"\server\Проект\0000_01_01_S6_KJ01_01_Р3.rvt") == "0000_01_01_S6_KJ01_01_Р3", "кириллица и путь");
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt", trim: false) == "0000_01_01_S6_KJ01_01_M3 еще текст", "без обрезки");
                Check(Nwc("0000_01_01_S6_KJ01_01_M3_x.rvt", parts: 6) == "0000_01_01_S6_KJ01_01", "6 частей");
                var short_ = ExportNames.NwcName("Модель АР.rvt", true, 7, out var matches);
                Check(short_ == "Модель АР" && !matches, "не по шаблону: " + short_);
                ExportNames.NwcName("0000_01_01_S6_KJ01_01_M3.rvt", true, 7, out matches);
                Check(matches, "по шаблону");
            });
            yield return Case("{Блок} и {Отдел} — найденные в имени модели, не по номеру части", () =>
            {
                var codes = new[] { "AR", "KJ", "OV" };
                Check(ExportNames.Block("0000_01_01_S6_KJ01_01_M3 еще текст.rvt", "S") == "S6", "блок S6");
                Check(ExportNames.Block("0000_S10_KJ01.rvt", "s") == "S10", "S10, буква без учёта регистра");
                Check(ExportNames.Block("0000_SX_KJ01.rvt", "S") == "" && ExportNames.Block("Модель АР.rvt", "S") == "", "нет блока");
                Check(ExportNames.Department("0000_01_01_S6_KJ01_01_M3.rvt", codes) == "KJ", "KJ01 → KJ");
                Check(ExportNames.Department("0000_S6_OV_вент.rvt", codes) == "OV", "код без цифр");
                Check(ExportNames.Department("ArmanQala_S6_X01.rvt", codes) == "", "«AR» в «ArmanQala» — не отдел");
                Check(ExportNames.Department("0000_S6_ARX01.rvt", codes) == "", "код с буквами после — не отдел");
                Check(ExportNames.Department("0000_KJ01_AR02.rvt", codes) == "KJ", "два кода — первый слева");
                Check(ExportNames.Department("0000_kj01.rvt", codes) == "KJ", "без учёта регистра — код как в таблице");
            });
            yield return Case("папка — строка с метками: подставить известные, неизвестные — как есть", () =>
            {
                var values = new Dictionary<string, string> { ["Проект"] = "Арман Кала", ["Блок"] = "Блок 1", ["Отдел"] = "" };
                var folder = ExportNames.Fill(@"{Шифр}_{Проект}\{ Блок }\{Отдел}\NWC", values);
                Check(folder == @"{Шифр}_Арман Кала\Блок 1\{Отдел}\NWC", folder);
                var labels = string.Join(",", ExportNames.LabelsIn(@"{Шифр}_{Проект}\{Проект}\{Дата}"));
                Check(labels == "Шифр,Проект,Дата", labels);
            });
            yield return Case("общие настройки: экспортёр — как на снимке; удаление, имя, дата; коэффициент фасетизации", () =>
            {
                var s = new ExportSettings();
                var n = s.Nwc;
                Check(n.ConvertElementIds && n.Parameters == NwcParameters.All && n.SharedCoordinates && n.FacetingFactor == 1 &&
                      n.ConvertElementProperties && n.RoomAsAttribute && n.ConvertParts && n.DivideIntoLevels &&
                      n.Scope == NwcScope.View && n.ViewName == "3D_Navisworks", "включённое");
                Check(!n.ConvertUrls && !n.ConvertLights && !n.ConvertLinkedCad && !n.ConvertLinkedFiles && !n.FindMissingMaterials &&
                      !n.RoomGeometry, "выключенное");
                Check(s.RemoveRevitLinks && s.RemoveCad && s.RemovePointClouds && s.RemoveIfc, "перед выгрузкой удаляется всё внешнее");
                Check(s.TrimName && s.NameParts == 7 && s.BlockPrefix == "S" && s.DateFormat == "dd.MM.yyyy", "имя модели и дата");
                Check(NwcOptions.TryFaceting("0,5", out var f) && f == 0.5, "запятая");
                Check(NwcOptions.TryFaceting(" 2.5 ", out f) && f == 2.5, "точка");
                Check(!NwcOptions.TryFaceting("0", out _) && !NwcOptions.TryFaceting("-1", out _) &&
                      !NwcOptions.TryFaceting("абв", out _) && !NwcOptions.TryFaceting("", out _), "не число или не больше нуля");
                s.Departments.Add(new DepartmentTemplate { Department = "KJ" });
                var copy = s.Clone();
                copy.Nwc.ViewName = "другой";
                copy.Departments[0].Department = "AR";
                Check(s.Nwc.ViewName == "3D_Navisworks" && s.Departments[0].Department == "KJ", "копия не трогает исходные");
            });
            yield return Case("проект: папки внутри папки проекта по регламенту; NWF — по названию задачи; отчёты — на дату запуска", () =>
            {
                var common = new ExportSettings { Departments = { new DepartmentTemplate { Department = "KJ" } } };
                var project = new ExportTask { Name = "ArmanQala" };
                project.Project.Folder = @"\\srv\2025_Арман Кала 7 очередь\";
                var s1 = project.Add(new ExportTask { Name = "S1", ModelFiles = { new ExportModel { Path = @"RSN://10.10.1.88/AQ/0000_01_01_S1_KJ01_01_M3 еще.rvt" } } });
                var m = s1.ModelFiles[0];
                Check(ExportNodes.NwcPath(s1, m, common) == @"\\srv\2025_Арман Кала 7 очередь\S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc", ExportNodes.NwcPath(s1, m, common));
                Check(ExportNodes.NwfPath(s1, common) == @"\\srv\2025_Арман Кала 7 очередь\S1\S1.nwf", ExportNodes.NwfPath(s1, common));
                Check(ExportNodes.ReportPath(s1, common, new DateTime(2026, 11, 2)) == @"\\srv\2025_Арман Кала 7 очередь\S1\02.11.2026",
                    ExportNodes.ReportPath(s1, common, new DateTime(2026, 11, 2)));
                project.Project.NwcFolder = @"{Блок}\NWC\{Отдел}";
                Check(ExportNodes.NwcPath(s1, m, common).EndsWith(@"\S1\NWC\KJ\0000_01_01_S1_KJ01_01_M3.nwc"), "отдел — по модели");
                project.Project.NwfFolder = @"D:\NWF\{Блок}";
                Check(ExportNodes.NwfPath(s1, common) == @"D:\NWF\S1\S1.nwf", "полный путь — как есть");
                Check(ExportNodes.InProject("", @"{Блок}") == "{Блок}" && ExportNodes.InProject(@"\\srv\P", "") == @"\\srv\P", "пустые части");
                Check(ExportNodes.CheckNwcFolder(@"{Блок}\NWC\{Отдел}") == null && ExportNodes.CheckNwcFolder(@"{Блок}\{Дата}") != null,
                    "NWC — без даты");
                Check(ExportNodes.CheckNwfFolder("{Блок}") == null && ExportNodes.CheckNwfFolder(@"{Блок}\{Дата}") != null &&
                      ExportNodes.CheckNwfFolder("{Отдел}") != null, "NWF — постоянный: без даты и отдела");
                Check(ExportNodes.CheckReportFolder(@"{Блок}\{Дата}") == null && ExportNodes.CheckReportFolder(@"{Отдел}") != null,
                    "отчёты — без отдела");
                var bad = ExportNodes.CheckReportFolder(@"{Проект}\{Дата}");
                Check(bad != null && bad.Contains("{Проект}"), "неизвестная метка: " + bad);
            });
            yield return Case("блок задачи — по именам моделей; две задачи на блок — один NWC, у каждой свой NWF и файл проверок", () =>
            {
                var common = new ExportSettings();
                var project = new ExportTask { Name = "ArmanQala" };
                project.Project.TestsFiles = new List<string> { @"\\srv\Проверки.xml" };
                var models = new[] { "0000_01_01_S1_KJ01_01_M3.rvt", "0000_01_01_S1_AR01_01_M3.rvt", "0000_01_01_S0_GP01_01_M3.rvt" };
                var high = project.Add(new ExportTask { Name = "S1 высокая", ModelFiles = models.Select(x => new ExportModel { Path = x }).ToList(),
                    TestsFiles = new List<string> { @"\\srv\Высокая.xml", @"\\srv\МЕР.xml" } });
                var mid = project.Add(new ExportTask { Name = "S1 средняя", ModelFiles = models.Select(x => new ExportModel { Path = x }).ToList() });
                Check(ExportNodes.BlockOf(high, common) == "S1", "блок — чаще всех в именах: " + ExportNodes.BlockOf(high, common));
                Check(ExportNodes.NwcPath(high, high.ModelFiles[0], common) == ExportNodes.NwcPath(mid, mid.ModelFiles[0], common),
                    "одна модель — один NWC");
                Check(ExportNodes.NwfPath(high, common).EndsWith(@"S1\S1 высокая.nwf") && ExportNodes.NwfPath(mid, common).EndsWith(@"S1\S1 средняя.nwf"),
                    "свои NWF");
                Check(ExportNodes.ReportPath(high, common, DateTime.Today) == ExportNodes.ReportPath(mid, common, DateTime.Today),
                    "отчёты — в одну папку прогона");
                Check(ExportNodes.TestsFilesOf(high).Count == 2 && ExportNodes.TestsFilesOf(mid).Single() == @"\\srv\Проверки.xml", "файлы проверок");
                Check(ExportNodes.BlockOf(new ExportTask { Name = " Корпус Б " }, common) == "Корпус Б", "моделей нет — название задачи");
                Check(ExportNodes.ViewOf(high, common) == "3D_Navisworks", "вид — из общих");
                project.Project.ViewName = "3D_NW";
                Check(ExportNodes.ViewOf(mid, common) == "3D_NW", "вид — проекта");
            });
            yield return Case("название задачи — имя NWF; файл проверок; папка проекта на сервере по модели", () =>
            {
                var project = new ExportTask { Name = "ArmanQala" };
                var s1 = project.Add(new ExportTask { Name = "S1" });
                var s2 = project.Add(new ExportTask { Name = "S2" });
                Check(ExportNodes.CheckTaskName(s2, "S3") == null, "новое название");
                Check(ExportNodes.CheckTaskName(s2, " s1 ") != null, "повтор в проекте — один NWF на двоих");
                Check(ExportNodes.CheckTaskName(s2, "S1:высокая") != null && ExportNodes.CheckTaskName(s2, " ") != null, "знаки и пусто");
                Check(ExportNodes.CheckTaskName(project, "Арман: Кала") == null, "у проекта знаки не важны — NWF по нему не называется");
                var names = new Dictionary<string, string[]>
                {
                    [@"\\srv\КЖ-АР.xml"] = new[] { "КЖ Стены/АР Стены", "КЖ Плиты/АР Стены" },
                    [@"\\srv\МЕР.xml"] = new[] { "ОВ/ВК" },
                    [@"\\srv\Повтор.xml"] = new[] { "кж  стены/ар стены" },
                };
                IEnumerable<string> Names(string f) => names.TryGetValue(f, out var n) ? n : throw new IOException("сеть недоступна");
                Check(ExportNodes.CheckTestsFiles(new[] { @"\\srv\КЖ-АР.xml", @"\\srv\МЕР.xml" }, Names) == null, "разные проверки");
                Check(ExportNodes.CheckTestsFiles(new string[0], Names) == null, "файлов нет — можно");
                var twice = ExportNodes.CheckTestsFiles(new[] { @"\\srv\КЖ-АР.xml", @"\\srv\Повтор.xml" }, Names);
                Check(twice != null && twice.Contains("Повтор.xml"), "одна проверка в двух файлах (без учёта регистра и пробелов): " + twice);
                Check(ExportNodes.CheckTestsFiles(new[] { @"\\srv\КЖ-АР.xml", @"\\нет\Недоступен.xml" }, Names) == null,
                    "не прочитался — не сравнивается");
                Check(ExportNodes.CheckTestsFiles(new[] { @"\\srv\a.nwf" }, Names) != null, "не .xml");
                Check(ExportNodes.CheckTestsFiles(new[] { @"\\srv\МЕР.xml", @"\\SRV\мер.xml" }, Names) != null, "один файл дважды");
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt") == "RSN://10.10.1.88/2025_Ариал",
                    ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt"));
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/ARL.rvt") == "" && ExportNodes.ServerFolderOf(@"\\srv\a.rvt") == "", "нет папки / не сервер");
            });
            yield return Case("модели: имя NWC руками или из имени; Revit Server; имена NWC", () =>
            {
                var common = new ExportSettings();
                var m = new ExportModel { Path = @"\\srv\Проект\0000_01_01_S6_KJ01_01_M3 еще текст.rvt" };
                Check(ExportNodes.NwcNameOf(m, common) == "0000_01_01_S6_KJ01_01_M3", "из имени");
                m.NwcName = " КЖ_свой ";
                Check(ExportNodes.NwcNameOf(m, common) == "КЖ_свой", "руками");
                var rsn = new ExportModel { Path = "RSN://10.10.1.5/Проекты/0000_01_01_S6_AR01_01_M3.rvt" };
                Check(ExportNodes.NwcNameOf(rsn, common) == "0000_01_01_S6_AR01_01_M3", "Revit Server: " + ExportNodes.NwcNameOf(rsn, common));
                Check(ExportNodes.IsServerPath(rsn.Path) && !ExportNodes.IsServerPath(@"\\srv\m.rvt") && !ExportNodes.IsServerPath("RSN://srv/папка"),
                    "путь Revit Server");
                Check(ExportNodes.CheckNwcNames(new[] { "a", "b" }) == null, "верные имена");
                Check(ExportNodes.CheckNwcNames(new[] { "a", "A" }) != null, "повтор без учёта регистра");
                Check(ExportNodes.CheckNwcNames(new[] { "a:b" }) != null && ExportNodes.CheckNwcNames(new[] { " " }) != null, "знаки и пусто");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ", "AR", "" }) == null, "коды верные");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ01" }) != null && ExportNodes.CheckDepartmentCodes(new[] { "KJ", "kj" }) != null,
                    "цифры в коде и повтор");
            });
            yield return Case("файл настроек: общие, проект с задачами, задача — туда и обратно; занятые названия — «(2)»", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "export-files");
                Directory.CreateDirectory(dir);
                var common = new ExportSettings { BlockPrefix = "C", Departments = { new DepartmentTemplate { Department = "KJ", Etalon = @"\\srv\КЖ.rte" } } };
                common.Nwc.ViewName = "3D_NW";
                var path = Path.Combine(dir, "Общие настройки.json");
                ExportFiles.Save(path, ExportFiles.FromCommon(common));
                var back = ExportFiles.Load(path);
                Check(back.Kind == ExportFile.Common && back.Settings.BlockPrefix == "C" && back.Settings.Nwc.ViewName == "3D_NW" &&
                      back.Settings.Departments.Single().Etalon == @"\\srv\КЖ.rte", "общие");
                var target = new ExportSettings();
                target.CopyFrom(back.Settings);
                Check(target.BlockPrefix == "C" && target.Departments.Count == 1 && target.Nwc.ViewName == "3D_NW", "взять на место");

                var project = new ExportTask { Name = "ArmanQala" };
                project.Project.Folder = @"\\srv\Арман Кала";
                project.Project.TestsFiles = new List<string> { @"\\srv\Проверки.xml" };
                project.Add(new ExportTask { Name = "S1", ModelFiles = Models(2, "S1"), TestsFiles = new List<string> { @"\\srv\Высокая.xml" } });
                project.Add(new ExportTask { Name = "S2", ModelFiles = Models(1, "S2") });
                path = Path.Combine(dir, "Проект.json");
                ExportFiles.Save(path, ExportFiles.FromProject(project.Name, project.Project, project.Children));
                back = ExportFiles.Load(path);
                var copy = ExportFiles.ToProject(back.ProjectData, new[] { "ArmanQala" });
                Check(copy.Name == "ArmanQala (2)" && copy.Project.Folder == @"\\srv\Арман Кала" && copy.Children.Count == 2, "проект: " + copy.Name);
                Check(copy.Children[0].Models == 2 && copy.Children[0].TestsFiles.Single() == @"\\srv\Высокая.xml" &&
                      copy.Children[1].TestsFiles == null && copy.Children[1].Parent == copy, "задачи проекта");
                Check(ExportFiles.FromProject(project.Name, project.Project, project.Children).ProjectData.Settings != project.Project, "настройки — копией");

                path = Path.Combine(dir, "Задача.json");
                ExportFiles.Save(path, ExportFiles.FromTask(project.Children[0]));
                back = ExportFiles.Load(path);
                var task = ExportFiles.ToTask(back.TaskData, project.Children.Select(c => c.Name));
                Check(back.Kind == ExportFile.Task && task.Name == "S1 (2)" && task.Models == 2, "задача: " + task.Name);
                Check(ExportFiles.UniqueName("S1", new[] { "S1", "s1 (2)" }) == "S1 (3)" && ExportFiles.UniqueName(" ", new string[0]) == "Без названия",
                    "уникальные названия");

                File.WriteAllText(Path.Combine(dir, "чужой.json"), "{\"a\":1}");
                var refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "чужой.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "чужой JSON — понятный отказ");
                File.WriteAllText(Path.Combine(dir, "не json.json"), "не json");
                refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "не json.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "не JSON — понятный отказ");
            });
            yield return Case("Revit Server: серверы из RSN.ini, адрес папки, путь RSN://, ответ службы", () =>
            {
                var hosts = RevitServers.ParseRsn("10.10.1.88\r\n; старый\r\n\r\n26.219.177.116\n10.10.1.88 ");
                Check(string.Join(",", hosts) == "10.10.1.88,26.219.177.116", string.Join(",", hosts));
                Check(RevitServers.ContentsUrl("10.10.1.88", 2026, new List<string>()) ==
                      "http://10.10.1.88/RevitServerAdminRESTService2026/AdminRESTService.svc/%7C/contents", "корень");
                var url = RevitServers.ContentsUrl("10.10.1.88", 2026, new List<string> { "2025_Ариал", "01_AR", "S 03" });
                Check(url == "http://10.10.1.88/RevitServerAdminRESTService2026/AdminRESTService.svc/" +
                      "2025_%D0%90%D1%80%D0%B8%D0%B0%D0%BB%7C01_AR%7CS%2003/contents", url);
                Check(RevitServers.RsnPath("10.10.1.88", new List<string> { "2025_Ариал", "01_AR" }, "ARL_03_01_S3_AR01_01_M3.rvt") ==
                      "RSN://10.10.1.88/2025_Ариал/01_AR/ARL_03_01_S3_AR01_01_M3.rvt", "путь модели");
                var c = RevitServers.ParseContents("{\"Path\":\"\",\"Files\":[{\"Name\":\"super.lock\",\"Size\":0}]," +
                    "\"Folders\":[{\"HasContents\":true,\"LockState\":0,\"Name\":\"2025_Ариал\",\"Size\":4947859366}," +
                    "{\"HasContents\":false,\"Name\":\"Пусто\",\"Size\":0}]," +
                    "\"Models\":[{\"LockState\":0,\"ModelSize\":769982263,\"Name\":\"ARL_03_01_S3_AR01_01_M3.rvt\",\"ProductVersion\":16}]}");
                Check(c.Folders.Count == 2 && c.Folders[0].Name == "2025_Ариал" && c.Folders[0].HasContents && !c.Folders[1].HasContents, "папки");
                Check(c.Models.Count == 1 && c.Models[0].ModelSize == 769982263, "модели");
                var empty = RevitServers.ParseContents("{\"Path\":\"x\"}");
                Check(empty.Folders.Count == 0 && empty.Models.Count == 0, "пустая папка");
                Check(RevitServers.SizeText(769982263) == "734 МБ" && RevitServers.SizeText(1288490189) == "1,2 ГБ" &&
                      RevitServers.SizeText(1000) == "1 МБ", RevitServers.SizeText(769982263) + " " + RevitServers.SizeText(1288490189));
                var lines = RevitServers.Hosts(1999);
                Check(lines.Count == 0, "нет RSN.ini — серверов нет");
            });
            yield return Case("удаление: вложенных на всех уровнях", () =>
            {
                var roots = Sample(out var block1, out _);
                Check(roots[0].Descendants == 4, "у проекта: " + roots[0].Descendants);
                roots[0].Remove(block1);
                Check(roots[0].Descendants == 1 && block1.Parent == null, "после удаления блока 1: " + roots[0].Descendants);
            });
        }
    }
}
