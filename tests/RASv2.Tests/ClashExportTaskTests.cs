using System;
using System.Collections.Generic;
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
                var t = new ExportTask { ModelFiles = Models(6) };
                Check(t.Summary() == "6 моделей · не выгружалось", t.Summary());
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
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ", "AR", "" }) == null, "коды верные");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ01" }) != null && ExportNodes.CheckDepartmentCodes(new[] { "KJ", "kj" }) != null,
                    "цифры в коде и повтор");
            });
            yield return Case("строка: папки и вид — каждая своя или от ближайшей строки выше", () =>
            {
                var roots = Sample(out var block1, out var high);
                var project = roots[0];
                project.Own.NwcFolder = @"\\srv\ArmanQala\NWC\{Блок}";
                project.Own.NwfFolder = @"\\srv\ArmanQala\NWF";
                block1.Own.NwfFolder = @"\\srv\ArmanQala\NWF\{Блок}";
                var (nwc, from) = ExportNodes.Inherited(block1, s => s.NwcFolder);
                Check(from == project && nwc.EndsWith("{Блок}"), "папка NWC у блока — от задачи");
                Check(ExportNodes.Effective(block1, s => s.NwfFolder).EndsWith(@"NWF\{Блок}"), "своя папка NWF");
                project.Own.NwcFolder = @"\\srv2\NWC\{Блок}";
                Check(ExportNodes.Effective(block1, s => s.NwcFolder).StartsWith(@"\\srv2"), "поменяли у задачи — блок следом");
                Check(ExportNodes.Effective(block1, s => s.ViewName) == null, "вид нигде не задан — из общих");
                project.Own.ViewName = "3D_NW_Проект";
                Check(ExportNodes.Effective(high, s => s.ViewName) == "3D_NW_Проект", "вид — от задачи");
            });
            yield return Case("проверки: у задачи на все блоки; блок снимает ненужные; имя NWF и папка отчётов", () =>
            {
                var roots = Sample(out var block1, out _);
                var project = roots[0];
                var high = new CheckSet { Name = "Высокая критичность", ReportFolder = @"\\srv\{Дата}\Высокая критичность\XML\{Блок}" };
                var mid = new CheckSet { Name = "Средняя критичность", NwfName = "{Блок}_средняя" };
                project.Own.Checks = new List<CheckSet> { high, mid };
                var block2 = project.Children[1];
                block2.SkippedChecks.Add(mid.Id);
                Check(ExportNodes.ActiveChecks(block1).Count == 2, "у блока 1 — обе");
                Check(ExportNodes.ActiveChecks(block2).Count == 1 && ExportNodes.ActiveChecks(block2)[0] == high, "у блока 2 средняя снята");
                mid.Name = "Средняя";
                Check(ExportNodes.ActiveChecks(block2).Count == 1, "переименовали — снятая так и осталась снятой (по ключу)");
                Check(ExportNodes.ChecksOf(roots[1]).Count == 0, "у другой задачи проверок нет");
                Check(high.EffectiveNwfName == "{Блок}_Высокая критичность" && mid.EffectiveNwfName == "{Блок}_средняя", "имя NWF");
                var v = new Dictionary<string, string> { ["Блок"] = "S1", ["Дата"] = "01.10.2026" };
                Check(ExportNames.Fill(high.EffectiveNwfName, v) == "S1_Высокая критичность", "NWF блока");
                Check(ExportNames.Fill(high.ReportFolder, v) == @"\\srv\01.10.2026\Высокая критичность\XML\S1", "папка отчётов блока");
                Check(ExportNodes.CheckChecks(project.Own.Checks) == null, "проверки верные");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = " " } }) != null, "без названия");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = "А" }, new CheckSet { Name = "а " } }) != null, "повтор названия");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = "А", NwfName = "{Блок}" }, new CheckSet { Name = "Б", NwfName = "{Блок}" } }) != null,
                    "одно имя NWF у двух проверок");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = "А", NwfName = "{Отдел}" } }) != null, "{Отдел} в имени NWF");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = "А", NwfName = "a:b" } }) != null, "знаки в имени NWF");
                Check(ExportNodes.CheckChecks(new List<CheckSet> { new CheckSet { Name = "А", ReportFolder = @"\\srv\{Отдел}" } }) != null, "{Отдел} в папке отчётов");
                Check(ExportNodes.CheckNwfFolder(@"\\srv\NWF\{Блок}\{Дата}") == null && ExportNodes.CheckNwfFolder(@"\\srv\{Отдел}") != null, "папка NWF");
                project.ModelFiles.Clear();
                Check(project.Summary().EndsWith("проверки: Высокая критичность, Средняя"), project.Summary());
                Check(!block1.Summary().Contains("проверки"), "у блока проверок в строке нет");
            });
            yield return Case("модели — у одной строки на ветке; папка NWC — не ниже неё; модель — один раз на проект", () =>
            {
                var roots = Sample(out var block1, out var high);
                var project = roots[0];
                Check(ExportNodes.CanHaveModels(block1) && !ExportNodes.CanHaveModels(high) && !ExportNodes.CanHaveModels(project),
                    "у блока — да, у критичности и задачи — нет");
                Check(ExportNodes.ModelsAbove(high) == block1, "у критичности модели — блока");
                Check(string.Join(",", ExportNodes.ModelsBelow(project).Select(t => t.Name)) == "Блок 1,Блок 2", "у задачи — модели блоков");
                Check(ExportNodes.HasNwcFolder(block1) && ExportNodes.HasNwcFolder(project) && !ExportNodes.HasNwcFolder(high),
                    "папка NWC — у блока и выше, у критичности — нет");
                var empty = new ExportTask { Name = "Новый проект" };
                Check(ExportNodes.CanHaveModels(empty) && ExportNodes.HasNwcFolder(empty), "проект без блоков держит модели сам");
                var path = block1.ModelFiles[0].Path;
                Check(ExportNodes.FindModel(project, path.ToUpperInvariant(), project.Children[1]) == block1, "модель уже в блоке 1");
                Check(ExportNodes.FindModel(project, path, block1) == null, "у себя — не повтор");
            });
            yield return Case("модели: имя NWC руками или из имени; Revit Server; имена и папки", () =>
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
                Check(ExportNodes.CheckNwcFolder(@"\\srv\NWC\{ блок }\{Отдел}") == null, "папка NWC — блок и отдел");
                Check(ExportNodes.CheckNwcFolder(@"\\srv\NWC\{Дата}") != null, "{Дата} в папке NWC — нельзя");
                Check(ExportNodes.CheckReportFolder(@"\\srv\{Дата}\Высокая критичность\XML\{Блок}") == null, "папка отчётов — дата и блок");
                Check(ExportNodes.CheckReportFolder(@"\\srv\{Отдел}") != null, "{Отдел} в папке отчётов — нельзя");
                var bad = ExportNodes.CheckReportFolder(@"\\srv\{Проект}");
                Check(bad != null && bad.Contains("{Проект}"), "неизвестная метка: " + bad);
                var v = ExportNames.LabelValues(m.Path, new ExportSettings { Departments = { new DepartmentTemplate { Department = "KJ" } } },
                    new DateTime(2026, 10, 1));
                Check(ExportNames.Fill(@"\\srv\NWC\{Блок}\{Отдел}\{Дата}", v) == @"\\srv\NWC\S6\KJ\01.10.2026", "метки по модели");
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
