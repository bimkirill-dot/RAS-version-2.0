using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.Tests
{
    /// <summary>
    /// RAS-Clash Analysis: «Задачи выгрузки» — проект с моделями → задачи (критичности) → блоки из имён моделей (ExportTask,
    /// ExportNodes): строки, краткая информация, где что лежит, проверки окон; имя NWC и папки с метками (ExportNames); файл
    /// настроек и общее хранилище.
    /// </summary>
    internal static class ClashExportTaskTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        // модели блока: по отделу на модель — пути разные
        private static List<ExportModel> Models(string block, params string[] departments) =>
            departments.Select(d => new ExportModel { Path = "0000_01_01_" + block + "_" + d + "01_01_M3.rvt" }).ToList();

        // ArmanQala: S1 (3 модели), S2 (2), S10 (1), Генплан без блока; задачи «Высокая критичность», «Средняя критичность».
        // ЖК Нурлы: S1 (2), одна задача
        private static List<ExportTask> Sample(out ExportTask high)
        {
            var project = new ExportTask { Name = "ArmanQala" };
            project.ModelFiles.AddRange(Models("S10", "KJ"));
            project.ModelFiles.AddRange(Models("S1", "KJ", "AR", "OV"));
            project.ModelFiles.AddRange(Models("S2", "KJ", "AR"));
            project.ModelFiles.Add(new ExportModel { Path = @"\\srv\Генплан.rvt" });
            high = project.Add(new ExportTask { Name = "Высокая критичность" });
            high.Options.TestsFiles.Add(@"\\srv\Проверки\КЖ-АР.xml");
            project.Add(new ExportTask { Name = "Средняя критичность" });
            var nurly = new ExportTask { Name = "ЖК Нурлы", ModelFiles = Models("S1", "KJ", "AR") };
            nurly.Add(new ExportTask { Name = "Высокая критичность" });
            return new List<ExportTask> { project, nurly };
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            var common = new ExportSettings();
            yield return Case("строки: проект 1, задача 1.1, её блоки 1.1.1; свёрнутые — без вложенных", () =>
            {
                var roots = Sample(out var high);
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>(), common);
                var numbers = string.Join(" ", rows.Select(r => r.Number + ":" + (r.Block?.Name ?? r.Item.Name)));
                Check(numbers == "1:ArmanQala 1.1:Высокая критичность 1.1.1:S1 1.1.2:S2 1.1.3:S10 1.2:Средняя критичность 1.2.1:S1 1.2.2:S2 " +
                      "1.2.3:S10 2:ЖК Нурлы 2.1:Высокая критичность 2.1.1:S1", numbers);
                Check(rows.Where(r => r.Block != null).All(r => r.Level == 2) && rows[2].Item == high, "блок — строка своей задачи");
                rows = ExportNodes.Rows(roots, new HashSet<ExportTask> { high, roots[1] }, common);
                numbers = string.Join(" ", rows.Select(r => r.Number));
                Check(numbers == "1 1.1 1.2 1.2.1 1.2.2 1.2.3 2", "свёрнуты задача и проект: " + numbers);
            });
            yield return Case("блоки — из имён моделей: по номерам, без блока — последним; вписанный руками; снятые задачей", () =>
            {
                var roots = Sample(out var high);
                var project = roots[0];
                var blocks = ExportNodes.Blocks(project, common);
                Check(string.Join(",", blocks.Select(b => (b.Name.Length == 0 ? "—" : b.Name) + b.Models.Count)) == "S13,S22,S101,—1",
                    string.Join(",", blocks.Select(b => b.Name + b.Models.Count)));
                project.ModelFiles.Last().Block = " S1 ";
                Check(ExportNodes.Blocks(project, common)[0].Models.Count == 4 && ExportNodes.Blocks(project, common).Count == 3, "Генплан — в S1 руками");
                high.Options.SkippedBlocks.Add("s2");
                Check(string.Join(",", ExportNodes.TaskBlocks(high, common).Select(b => b.Name)) == "S1,S10", "S2 задача не делает");
                Check(ExportNodes.NaturalCompare("S2", "S10") < 0 && ExportNodes.NaturalCompare("S10", "S9") > 0, "S2 раньше S10");
            });
            yield return Case("перенос — проекты между собой, задачи внутри своего проекта", () =>
            {
                var roots = Sample(out var high);
                var mid = roots[0].Children[1];
                Check(ExportTask.MoveTo(roots[0].Children, mid, high) && roots[0].Children[0] == mid, "средняя — на место высокой");
                Check(ExportTask.MoveTo(roots, roots[1], roots[0]) && roots[0].Name == "ЖК Нурлы", "проект — на место другого");
                Check(!ExportTask.MoveTo(roots, high, roots[1]), "задача — не на уровень проектов");
            });
            yield return Case("краткая информация: проекта — задачи, модели, блоки; задачи — блоки и файлы; блока — модели", () =>
            {
                var roots = Sample(out var high);
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>(), common);
                var s = ExportNodes.Summary(rows[0], common);
                Check(s == "2 задачи · 7 моделей · 3 блока · без блока: 1", s);
                s = ExportNodes.Summary(rows[1], common);
                Check(s == "блоки S1–S10 (3) · КЖ-АР.xml", s);
                s = ExportNodes.Summary(rows[2], common);
                Check(s == "3 модели", s);
                s = ExportNodes.Summary(rows[5], common);
                Check(s == "блоки S1–S10 (3) · файлов проверок нет", s);
                high.Options.SkippedBlocks.Add("S2");
                s = ExportNodes.Summary(ExportNodes.Rows(roots, new HashSet<ExportTask>(), common)[1], common);
                Check(s == "блоки S1, S10 · КЖ-АР.xml", s);
                var empty = new ExportTask { Name = "Новый" };
                var t = empty.Add(new ExportTask { Name = "Высокая" });
                s = ExportNodes.Summary(new TreeRow { Item = t, Level = 1 }, common);
                Check(s == "блоков нет · файлов проверок нет", s);
            });
            yield return Case("«модель / задача / блок» по числу", () =>
            {
                var words = string.Join(" ", new[] { 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111 }.Select(n => n + " " + ExportTask.ModelsWord(n)));
                Check(words == "1 модель 2 модели 4 модели 5 моделей 11 моделей 12 моделей 14 моделей 21 модель 22 модели 25 моделей 111 моделей", words);
                Check(ExportTask.TasksWord(1) == "задача" && ExportTask.TasksWord(3) == "задачи" && ExportTask.BlocksWord(9) == "блоков", "задачи, блоки");
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
            yield return Case("общие настройки: экспортёр — как на снимке; удаление, скрытие, имя, дата; коэффициент фасетизации", () =>
            {
                var s = new ExportSettings();
                var n = s.Nwc;
                Check(n.ConvertElementIds && n.Parameters == NwcParameters.All && n.SharedCoordinates && n.FacetingFactor == 1 &&
                      n.ConvertElementProperties && n.RoomAsAttribute && n.ConvertParts && n.DivideIntoLevels &&
                      n.Scope == NwcScope.View && n.ViewName == "3D_Navisworks", "включённое");
                Check(!n.ConvertUrls && !n.ConvertLights && !n.ConvertLinkedCad && !n.ConvertLinkedFiles && !n.FindMissingMaterials &&
                      !n.RoomGeometry, "выключенное");
                Check(s.RemoveRevitLinks && s.RemoveCad && s.RemovePointClouds && s.RemoveIfc, "перед выгрузкой удаляется всё внешнее");
                Check(s.HideNames.Count == 0, "скрыть в NWF — своих строк компании в коде нет");
                Check(s.TrimName && s.NameParts == 7 && s.BlockPrefix == "S" && s.DateFormat == "dd.MM.yyyy", "имя модели и дата");
                Check(NwcOptions.TryFaceting("0,5", out var f) && f == 0.5, "запятая");
                Check(NwcOptions.TryFaceting(" 2.5 ", out f) && f == 2.5, "точка");
                Check(!NwcOptions.TryFaceting("0", out _) && !NwcOptions.TryFaceting("-1", out _) &&
                      !NwcOptions.TryFaceting("абв", out _) && !NwcOptions.TryFaceting("", out _), "не число или не больше нуля");
                s.Departments.Add(new DepartmentTemplate { Department = "KJ" });
                s.HideNames.Add("временн");
                var copy = s.Clone();
                copy.Nwc.ViewName = "другой";
                copy.Departments[0].Department = "AR";
                copy.HideNames[0] = "другое";
                Check(s.Nwc.ViewName == "3D_Navisworks" && s.Departments[0].Department == "KJ" && s.HideNames[0] == "временн",
                    "копия не трогает исходные");
                var target = new ExportSettings();
                target.CopyFrom(s);
                Check(target.HideNames.Single() == "временн", "взять на место — со скрытием");
            });
            yield return Case("где что лежит: NWC — у проекта, NWF и отчёты — строки задачи с {Блок}, {Дата}, {Формат}", () =>
            {
                var c = new ExportSettings { Departments = { new DepartmentTemplate { Department = "KJ" } } };
                var project = new ExportTask { Name = "ArmanQala" };
                project.Project.Folder = @"\\srv\2025_Арман Кала 7 очередь\";
                project.ModelFiles.Add(new ExportModel { Path = @"RSN://10.10.1.88/AQ/0000_01_01_S1_KJ01_01_M3 еще.rvt" });
                var m = project.ModelFiles[0];
                var high = project.Add(new ExportTask { Name = "Высокая критичность" });
                Check(ExportNodes.NwcPath(high, m, c) == @"\\srv\2025_Арман Кала 7 очередь\S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc", ExportNodes.NwcPath(high, m, c));
                Check(ExportNodes.NwfPath(high, "S1", c) == @"\\srv\2025_Арман Кала 7 очередь\S1\S1.nwf", ExportNodes.NwfPath(high, "S1", c));
                Check(ExportNodes.ReportPath(high, "S1", c, new DateTime(2026, 11, 2), "XML") == @"\\srv\2025_Арман Кала 7 очередь\S1\02.11.2026",
                    "отчёты по умолчанию: " + ExportNodes.ReportPath(high, "S1", c, new DateTime(2026, 11, 2), "XML"));
                high.Options.NwfName = "{Блок}_ВК";
                high.Options.ReportFolder = @"Отчеты\{Дата}\{Блок}\Высокая критичность\{Формат}";
                Check(ExportNodes.NwfPath(high, "S1", c).EndsWith(@"\S1\S1_ВК.nwf"), "регламент: " + ExportNodes.NwfPath(high, "S1", c));
                Check(ExportNodes.ReportPath(high, "S1", c, new DateTime(2026, 11, 2), "HTML") ==
                      @"\\srv\2025_Арман Кала 7 очередь\Отчеты\02.11.2026\S1\Высокая критичность\HTML", ExportNodes.ReportPath(high, "S1", c, new DateTime(2026, 11, 2), "HTML"));
                project.Project.NwcFolder = @"{Блок}\NWC\{Отдел}";
                Check(ExportNodes.NwcPath(high, m, c).EndsWith(@"\S1\NWC\KJ\0000_01_01_S1_KJ01_01_M3.nwc"), "отдел — по модели");
                high.Options.NwfFolder = @"D:\NWF\{Блок}";
                Check(ExportNodes.NwfPath(high, "S1", c) == @"D:\NWF\S1\S1_ВК.nwf", "полный путь — как есть");
                Check(ExportNodes.InProject("", @"{Блок}") == "{Блок}" && ExportNodes.InProject(@"\\srv\P", "") == @"\\srv\P", "пустые части");
                Check(ExportNodes.Short(@"\\srv\2025_Арман Кала 7 очередь\", @"\\srv\2025_Арман Кала 7 очередь\S1\NWC\a.nwc") == @"…\S1\NWC\a.nwc",
                    "пример внутри папки проекта — с «…»");
                Check(ExportNodes.Short(@"\\srv\P", @"D:\NWF\S1\S1.nwf") == @"D:\NWF\S1\S1.nwf" &&
                      ExportNodes.Short(@"\\srv\P", @"\\srv\P2\a") == @"\\srv\P2\a" && ExportNodes.Short("", @"S1\NWC") == @"S1\NWC",
                    "вне папки проекта — целиком");
                Check(ExportNodes.ViewOf(high, c) == "3D_Navisworks", "вид — из общих");
                project.Project.ViewName = "3D_NW";
                Check(ExportNodes.ViewOf(high, c) == "3D_NW", "вид — проекта");
            });
            yield return Case("проверки строк: NWC без даты, NWF постоянная с {Блок}, отчёты без отдела, неизвестная метка", () =>
            {
                Check(ExportNodes.CheckNwcFolder(@"{Блок}\NWC\{Отдел}") == null && ExportNodes.CheckNwcFolder(@"{Блок}\{Дата}") != null, "NWC — без даты");
                Check(ExportNodes.CheckNwfFolder("{Блок}") == null && ExportNodes.CheckNwfFolder(@"{Блок}\{Дата}") != null &&
                      ExportNodes.CheckNwfFolder("{Отдел}") != null, "папка NWF — без даты и отдела");
                Check(ExportNodes.CheckNwfName("{Блок}_ВК") == null, "имя NWF по регламенту");
                Check(ExportNodes.CheckNwfName("ВК") != null && ExportNodes.CheckNwfName(" ") != null, "без {Блок} — у всех блоков один файл; пусто");
                Check(ExportNodes.CheckNwfName("{Блок}_{Дата}") != null && ExportNodes.CheckNwfName("{Блок}:ВК") != null, "дата и недопустимые знаки");
                Check(ExportNodes.CheckReportFolder(@"Отчеты\{Дата}\{Блок}\ВК\{Формат}") == null && ExportNodes.CheckReportFolder(@"{Отдел}") != null,
                    "отчёты — с форматом, без отдела");
                var bad = ExportNodes.CheckReportFolder(@"{Проект}\{Дата}");
                Check(bad != null && bad.Contains("{Проект}"), "неизвестная метка: " + bad);
            });
            yield return Case("задачи: название не повторяется; копия со старым именем NWF — одна NWF на двоих", () =>
            {
                var roots = Sample(out var high);
                var mid = roots[0].Children[1];
                Check(ExportNodes.CheckTaskName(mid, "Низкая") == null, "новое название");
                Check(ExportNodes.CheckTaskName(mid, " высокая критичность ") != null && ExportNodes.CheckTaskName(mid, " ") != null, "повтор и пусто");
                high.Options.NwfName = "{Блок}_ВК";
                mid.Options = high.Options.Clone();
                var taken = ExportNodes.CheckNwfTaken(mid, mid.Options.NwfFolder, mid.Options.NwfName, common);
                Check(taken != null && taken.Contains("Высокая критичность"), "копия как есть: " + taken);
                Check(ExportNodes.CheckNwfTaken(mid, mid.Options.NwfFolder, "{Блок}_СК", common) == null, "своё имя NWF");
                Check(ExportNodes.CheckNwfTaken(mid, @"{Блок}\СК", "{Блок}_ВК", common) == null, "своя папка NWF");
                Check(ExportNodes.CheckProjectName("ArmanQala", new[] { "ЖК Нурлы" }) == null, "новое название проекта");
                Check(ExportNodes.CheckProjectName(" armanqala ", new[] { "ArmanQala" }) != null && ExportNodes.CheckProjectName(" ", new string[0]) != null,
                    "повтор названия проекта и пусто");
            });
            yield return Case("файлы проверок: .xml, без повторов, одна проверка — в одном файле", () =>
            {
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
            });
            yield return Case("модели проекта: добавить без повторов; имя NWC руками или из имени; два NWC в один файл; Revit Server", () =>
            {
                var project = new ExportTask { Name = "ArmanQala" };
                Check(ExportNodes.AddModels(project, new[] { "RSN://srv/AQ/a_S1_KJ01.rvt", "RSN://srv/AQ/b_S1_AR01.rvt", "rsn://SRV/aq/A_S1_KJ01.rvt", " " }) == 2 &&
                      project.Models == 2, "повтор и пустое — мимо");
                var m = new ExportModel { Path = @"\\srv\Проект\0000_01_01_S6_KJ01_01_M3 еще текст.rvt" };
                Check(ExportNodes.NwcNameOf(m, common) == "0000_01_01_S6_KJ01_01_M3", "из имени");
                m.NwcName = " КЖ_свой ";
                Check(ExportNodes.NwcNameOf(m, common) == "КЖ_свой", "руками");
                var rsn = new ExportModel { Path = "RSN://10.10.1.5/Проекты/0000_01_01_S6_AR01_01_M3.rvt" };
                Check(ExportNodes.NwcNameOf(rsn, common) == "0000_01_01_S6_AR01_01_M3", "Revit Server: " + ExportNodes.NwcNameOf(rsn, common));
                Check(ExportNodes.IsServerPath(rsn.Path) && !ExportNodes.IsServerPath(@"\\srv\m.rvt") && !ExportNodes.IsServerPath("RSN://srv/папка"),
                    "путь Revit Server");
                var roots = Sample(out _);
                Check(ExportNodes.CheckNwcNames(roots[0], common) == null, "верные имена");
                roots[0].ModelFiles.Add(new ExportModel { Path = @"\\другой\0000_01_01_S1_KJ01_01_M3 копия.rvt" });
                var twin = ExportNodes.CheckNwcNames(roots[0], common);
                Check(twin != null && twin.Contains("копия"), "две модели — один NWC: " + twin);
                roots[0].ModelFiles.Last().NwcName = "a:b";
                Check(ExportNodes.CheckNwcNames(roots[0], common) != null, "недопустимые знаки");
                roots[0].Project.NwcFolder = @"{Блок}\{Отдел}";
                roots[0].ModelFiles.Last().NwcName = null;
                Check(ExportNodes.CheckNwcNames(roots[0], new ExportSettings { Departments = { new DepartmentTemplate { Department = "KJ" } } }) != null,
                    "одна папка — тот же файл");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ", "AR", "" }) == null, "коды верные");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ01" }) != null && ExportNodes.CheckDepartmentCodes(new[] { "KJ", "kj" }) != null,
                    "цифры в коде и повтор");
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt") == "RSN://10.10.1.88/2025_Ариал",
                    ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt"));
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/ARL.rvt") == "" && ExportNodes.ServerFolderOf(@"\\srv\a.rvt") == "", "нет папки / не сервер");
            });
            yield return Case("файл настроек: общие, проект с моделями и задачами, задача — туда и обратно; версия 1 читается", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "export-files");
                Directory.CreateDirectory(dir);
                var c = new ExportSettings { BlockPrefix = "C", Departments = { new DepartmentTemplate { Department = "KJ", Etalon = @"\\srv\КЖ.rte" } } };
                c.Nwc.ViewName = "3D_NW";
                c.HideNames.Add("временн");
                var path = Path.Combine(dir, "Общие настройки.json");
                ExportFiles.Save(path, ExportFiles.FromCommon(c));
                var back = ExportFiles.Load(path);
                Check(back.Kind == ExportFile.Common && back.Settings.BlockPrefix == "C" && back.Settings.Nwc.ViewName == "3D_NW" &&
                      back.Settings.Departments.Single().Etalon == @"\\srv\КЖ.rte" && back.Settings.HideNames.Single() == "временн", "общие");

                var roots = Sample(out var high);
                var project = roots[0];
                project.Project.Folder = @"\\srv\Арман Кала";
                project.ModelFiles.Last().Block = "S1";
                high.Options.NwfName = "{Блок}_ВК";
                high.Options.Html = false;
                high.Options.SkippedBlocks.Add("S10");
                path = Path.Combine(dir, "Проект.json");
                ExportFiles.Save(path, ExportFiles.FromProject(project.Name, project.Project, project.ModelFiles, project.Children));
                back = ExportFiles.Load(path);
                var copy = ExportFiles.ToProject(back.ProjectData, new[] { "ArmanQala" });
                Check(copy.Name == "ArmanQala (2)" && copy.Project.Folder == @"\\srv\Арман Кала" && copy.Models == 7 &&
                      copy.ModelFiles.Last().Block == "S1", "проект с моделями: " + copy.Name);
                var h = copy.Children[0];
                Check(copy.Children.Count == 2 && h.Parent == copy && h.Options.NwfName == "{Блок}_ВК" && h.Options.Xml && !h.Options.Html &&
                      h.Options.SkippedBlocks.Single() == "S10" && h.Options.TestsFiles.Single() == @"\\srv\Проверки\КЖ-АР.xml", "задачи проекта");
                Check(h.Options != high.Options && copy.Project != project.Project, "копией");

                path = Path.Combine(dir, "Задача.json");
                ExportFiles.Save(path, ExportFiles.FromTask(high));
                back = ExportFiles.Load(path);
                var task = ExportFiles.ToTask(back.TaskData, project.Children.Select(x => x.Name));
                Check(back.Kind == ExportFile.Task && task.Name == "Высокая критичность (2)" && task.Options.NwfName == "{Блок}_ВК" && task.Models == 0,
                    "задача — настройки без моделей: " + task.Name);
                Check(ExportFiles.UniqueName("S1", new[] { "S1", "s1 (2)" }) == "S1 (3)" && ExportFiles.UniqueName(" ", new string[0]) == "Без названия",
                    "уникальные названия");

                // версия 1 (02.11.2026): задачи-блоки со своими моделями — модели переходят в проект, файлы проверок — в задачу
                path = Path.Combine(dir, "Проект v1.json");
                File.WriteAllText(path, "{\"Kind\":\"Проект\",\"Version\":1,\"ProjectData\":{\"Name\":\"Старый\",\"Settings\":{\"Folder\":\"\\\\\\\\srv\\\\П\"," +
                    "\"NwcFolder\":\"{Блок}\\\\NWC\",\"NwfFolder\":\"{Блок}\",\"ReportFolder\":\"{Блок}\\\\{Дата}\",\"TestsFiles\":[\"t.xml\"]}," +
                    "\"Tasks\":[{\"Name\":\"S1\",\"Models\":[{\"Path\":\"a_S1_KJ01.rvt\"},{\"Path\":\"b_S1_AR01.rvt\"}],\"TestsFiles\":[\"s1.xml\"]}," +
                    "{\"Name\":\"S2\",\"Models\":[{\"Path\":\"a_S2_KJ01.rvt\"},{\"Path\":\"A_S1_KJ01.rvt\"}]}]}}");
                var old = ExportFiles.ToProject(ExportFiles.Load(path).ProjectData, new string[0]);
                Check(old.Project.Folder == @"\\srv\П" && old.Models == 3 && old.Children.Count == 2, "модели — в проект, без повторов: " + old.Models);
                Check(old.Children[0].Options.TestsFiles.Single() == "s1.xml" && old.Children[1].Options.TestsFiles.Count == 0 &&
                      old.Children[0].Options.Xml && old.Children[0].Options.Html && old.Children[0].Options.NwfName == "{Блок}", "настройки задач");

                File.WriteAllText(Path.Combine(dir, "чужой.json"), "{\"a\":1}");
                var refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "чужой.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "чужой JSON — понятный отказ");
                File.WriteAllText(Path.Combine(dir, "не json.json"), "не json");
                refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "не json.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "не JSON — понятный отказ");
            });
            yield return Case("общее хранилище: все видят все проекты; правку коллеги не затирает молча; копия .prev; удаление", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "export-store-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var ivanov = new ExportStore(dir, "Иванов");
                    var petrov = new ExportStore(dir, "Петров");
                    Check(ivanov.LoadProjects().Count == 0 && ivanov.LoadCommon().BlockPrefix == "S", "пусто — по умолчанию");

                    ivanov.SaveCommon(new ExportSettings { BlockPrefix = "C" });
                    Check(petrov.LoadCommon().BlockPrefix == "C", "общие настройки — у всех");

                    var mine = new ExportTask { Name = "Жк Нурлы", StoreOrder = 2, ModelFiles = Models("S1", "KJ", "AR") };
                    mine.Add(new ExportTask { Name = "Высокая критичность" });
                    Check(ivanov.SaveProject(mine, false, out _, out _) == SaveResult.Saved && mine.StoreId != null, "новый проект записан");
                    ivanov.SaveProject(new ExportTask { Name = "ArmanQala", StoreOrder = 1 }, false, out _, out _);
                    var seen = petrov.LoadProjects();
                    Check(string.Join(",", seen.Select(x => x.Name + x.StoreOrder)) == "ArmanQala1,Жк Нурлы2",
                        "у коллеги — оба, по порядковому номеру: " + string.Join(",", seen.Select(x => x.Name)));
                    var his = seen.Single(x => x.Name == "Жк Нурлы");
                    Check(his.StoreId == mine.StoreId && his.Models == 2 && his.Children.Single().Name == "Высокая критичность", "тот же проект с моделями и задачами");

                    System.Threading.Thread.Sleep(30);
                    his.Add(new ExportTask { Name = "Средняя критичность" });
                    Check(petrov.SaveProject(his, false, out _, out _) == SaveResult.Saved, "коллега сохранил своё");
                    mine.Children[0].Name = "Высокая";
                    Check(ivanov.SaveProject(mine, false, out var who, out var when) == SaveResult.Conflict && who == "Петров" &&
                          (DateTime.Now - when).TotalMinutes < 5, "моё не затирает его молча: " + who);
                    var fresh = ivanov.Reload(mine);
                    Check(fresh.Children.Count == 2 && fresh.Children[0].Name == "Высокая критичность", "открыть заново — его версия");
                    Check(ivanov.SaveProject(mine, true, out _, out _) == SaveResult.Saved, "поверх — по согласию");
                    Check(petrov.Reload(his).Children.Single().Name == "Высокая", "у коллеги — моя");
                    var path = Path.Combine(ivanov.ProjectsDir, mine.StoreId + ".json");
                    Check(File.Exists(path + ".prev") && !File.Exists(path + ".tmp"), "копия прежнего и без временного");
                    Check(ExportFiles.Load(path).ProjectData.ChangedBy == "Иванов", "кто записал");

                    File.WriteAllText(Path.Combine(ivanov.ProjectsDir, "битый.json"), "не json");
                    var broken = new List<string>();
                    Check(petrov.LoadProjects(broken).Count == 2 && broken.Single() == "битый.json", "битый файл — мимо, остальные читаются");

                    ivanov.DeleteProject(mine);
                    Check(!File.Exists(path) && File.Exists(path + ".prev") && petrov.LoadProjects().Count == 1, "удалён, копия осталась");
                    Check(ivanov.Reload(mine) == null, "удалённый — заново не открыть");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
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
            yield return Case("удаление: задачи проекта", () =>
            {
                var roots = Sample(out var high);
                Check(roots[0].Descendants == 2, "у проекта: " + roots[0].Descendants);
                roots[0].Remove(high);
                Check(roots[0].Descendants == 1 && high.Parent == null, "после удаления: " + roots[0].Descendants);
            });
        }
    }
}
