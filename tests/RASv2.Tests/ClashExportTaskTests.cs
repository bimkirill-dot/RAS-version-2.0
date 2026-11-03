using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.Tests
{
    /// <summary>
    /// RAS-Clash Analysis: «Задачи выгрузки» — дерево задач с наследованием настроек (ExportTask, ExportNodes): строки, NWF у строки
    /// без подзадач с «Формировать NWF», краткая информация, где что лежит, проверки окна; имя NWC и папки с метками (ExportNames);
    /// файл настроек (версии 1–3) и общее хранилище.
    /// </summary>
    internal static class ClashExportTaskTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        // модели блока: по отделу на модель — пути разные
        private static List<ExportModel> Models(string block, params string[] departments) =>
            departments.Select(d => new ExportModel { Path = "0000_01_01_" + block + "_" + d + "01_01_M3.rvt" }).ToList();

        // ArmanQala: модели S10 (1), S1 (3), S2 (2), Генплан без блока. «Высокая критичность» (файл КЖ-АР, модели — от задачи) →
        // «S1_ВК» (свои модели S1, NWF), «S2_ВК» (свои модели S2, NWF). «Средняя критичность» — без подзадач: свои модели S1 КЖ и АР,
        // NWF «S1_СК». ЖК Нурлы: без подзадач, S1 (2), NWF нет
        private static List<ExportTask> Sample(out ExportTask high, out ExportTask mid)
        {
            var top = ExportTask.NewTop("ArmanQala");
            top.Settings.Folder = @"\\srv\2025_Арман Кала 7 очередь";
            top.Settings.Models.AddRange(Models("S10", "KJ"));
            top.Settings.Models.AddRange(Models("S1", "KJ", "AR", "OV"));
            top.Settings.Models.AddRange(Models("S2", "KJ", "AR"));
            top.Settings.Models.Add(new ExportModel { Path = @"\\srv\Генплан.rvt" });
            high = top.Add(new ExportTask { Name = "Высокая критичность" });
            high.Settings.TestsFiles = new List<string> { @"\\srv\Проверки\КЖ-АР.xml" };
            high.Add(new ExportTask { Name = "S1_ВК", Settings = { Models = Models("S1", "KJ", "AR", "OV"), MakeNwf = true } });
            high.Add(new ExportTask { Name = "S2_ВК", Settings = { Models = Models("S2", "KJ", "AR"), MakeNwf = true } });
            mid = top.Add(new ExportTask { Name = "Средняя критичность" });
            mid.Settings.TestsFiles = new List<string> { @"\\srv\Проверки\МЕР.xml" };
            mid.Settings.Models = Models("S1", "KJ", "AR");
            mid.Settings.MakeNwf = true;
            mid.Settings.NwfName = "S1_СК";
            var nurly = ExportTask.NewTop("ЖК Нурлы");
            nurly.Settings.Models.AddRange(Models("S1", "KJ", "AR"));
            return new List<ExportTask> { top, nurly };
        }

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            var common = new ExportSettings();
            yield return Case("строки: задача 1, подзадачи 1.1, 1.1.1; свёрнутые — без вложенных", () =>
            {
                var roots = Sample(out var high, out _);
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>());
                var numbers = string.Join(" ", rows.Select(r => r.Number + ":" + r.Item.Name));
                Check(numbers == "1:ArmanQala 1.1:Высокая критичность 1.1.1:S1_ВК 1.1.2:S2_ВК 1.2:Средняя критичность 2:ЖК Нурлы", numbers);
                Check(rows[2].Level == 2 && rows[5].Level == 0, "уровни");
                rows = ExportNodes.Rows(roots, new HashSet<ExportTask> { high, roots[1] });
                numbers = string.Join(" ", rows.Select(r => r.Number));
                Check(numbers == "1 1.1 1.2 2", "свёрнута подзадача: " + numbers);
            });
            yield return Case("наследование: не задано — от уровня выше, задано ниже — перекрывает; любая глубина", () =>
            {
                var roots = Sample(out var high, out var mid);
                var top = roots[0];
                var s1 = high.Children[0];
                Check(ExportNodes.ModelsOf(high).Count == 7 && ExportNodes.OwnerOf(high, s => s.Models) == top, "модели — от задачи");
                Check(ExportNodes.ModelsOf(s1).Count == 3 && ExportNodes.OwnerOf(s1, s => s.Models) == s1, "свои — вместо уровня выше");
                Check(ExportNodes.FolderOf(s1) == @"\\srv\2025_Арман Кала 7 очередь" && ExportNodes.NwcFolderOf(s1) == @"{Блок}\NWC" &&
                      ExportNodes.TestsOf(s1).Single().EndsWith("КЖ-АР.xml"), "третий уровень — от подзадачи и задачи");
                Check(string.Join(",", ExportNodes.FormatsOf(s1)) == "XML,HTML", "форматы по умолчанию — XML и HTML");
                top.Settings.ReportFormats = new List<string> { "HTML" };
                Check(string.Join(",", ExportNodes.FormatsOf(s1)) == "HTML", "форматы — от задачи");
                mid.Settings.ReportFormats = new List<string> { "XML", "HTML (Табличное представление)" };
                Check(ExportNodes.FormatsOf(mid).Count == 2, "форматы — свои");
                Check(ExportNodes.ReportFormatNames.Length == 5 && ExportNodes.ReportFormatNames.Contains("Как точки обзора"),
                    "все форматы отчёта Navisworks");
                Check(ExportNodes.ExportOf(s1, common) == common, "параметры выгрузки — общие");
                high.Settings.Export = ExportOverride.From(common);
                high.Settings.Export.Nwc.FacetingFactor = 2;
                high.Settings.Export.RemoveCad = false;
                var ex = ExportNodes.ExportOf(s1, common);
                Check(ex.Nwc.FacetingFactor == 2 && !ex.RemoveCad && common.Nwc.FacetingFactor == 1 && common.RemoveCad,
                    "параметры выгрузки — от подзадачи, общие не тронуты");
                Check(ExportNodes.ExportOf(mid, common) == common, "у соседней ветки — общие");
                Check(ExportNodes.ViewOf(s1, common) == "3D_Navisworks", "вид — из общих");
                top.Settings.ViewName = "3D_NW";
                Check(ExportNodes.ViewOf(s1, common) == "3D_NW", "вид — от задачи");
                top.Settings.ServerFolder = "RSN://srv/AQ";
                Check(ExportNodes.StartServerFolder(s1) == "RSN://srv/AQ", "откуда добавлять модели — от задачи");
                Check(ExportNodes.HideSetsOf(s1).Count == 0, "скрыть — у новой задачи ничего, своих правил компании в коде нет");
                top.Settings.HideSets.Add(SearchSets.FromName("временн"));
                Check(ExportNodes.HideSetsOf(s1).Single().Name == "Имя содержит «временн»", "скрыть — от задачи подзадачам");
                high.Settings.HideSets = new List<HideSet> { SearchSets.FromName("временн"), SearchSets.FromName("черновик") };
                Check(ExportNodes.HideSetsOf(s1).Count == 2 && ExportNodes.HideSetsOf(mid).Count == 1, "своё у подзадачи — ниже неё");
                Check(!ExportNodes.MakesReports(s1), "отчёты — только с галочкой");
                s1.Settings.MakeReports = true;
                Check(ExportNodes.MakesReports(s1), "отчёты — у строки с NWF и галочкой");
                s1.Settings.MakeNwf = false;
                Check(!ExportNodes.MakesReports(s1), "без NWF отчётов нет");
            });
            yield return Case("NWF: у строки без подзадач с галочкой; имя — своё или название строки, не наследуется", () =>
            {
                var roots = Sample(out var high, out var mid);
                var s1 = high.Children[0];
                Check(ExportNodes.MakesNwf(s1) && ExportNodes.MakesNwf(mid) && !ExportNodes.MakesNwf(high) && !ExportNodes.MakesNwf(roots[1]),
                    "галочка у нижних строк");
                high.Settings.MakeNwf = true;
                Check(!ExportNodes.MakesNwf(high), "у строки с подзадачами галочка не действует");
                Check(ExportNodes.NwfNameOf(s1) == "S1_ВК" && ExportNodes.NwfNameOf(mid) == "S1_СК", "название строки или своё имя");
                high.Settings.NwfName = "чужое";
                Check(ExportNodes.NwfNameOf(s1) == "S1_ВК", "имя NWF не наследуется");
            });
            yield return Case("блоки — из имён моделей: по номерам, без блока — последним; вписанный руками; {Блок} строки", () =>
            {
                var roots = Sample(out var high, out _);
                var blocks = ExportNodes.Blocks(ExportNodes.ModelsOf(high), common);
                Check(string.Join(",", blocks.Select(b => (b.Name.Length == 0 ? "—" : b.Name) + b.Models.Count)) == "S13,S22,S101,—1",
                    string.Join(",", blocks.Select(b => b.Name + b.Models.Count)));
                Check(ExportNodes.BlockOf(high.Children[0].Settings.Models[0], common) == "S1",
                    "{Блок} — общий у моделей строки; разные — пусто");
                roots[0].Settings.Models.Last().Block = " S1 ";
                Check(ExportNodes.Blocks(ExportNodes.ModelsOf(high), common)[0].Models.Count == 4, "Генплан — в S1 руками");
                Check(ExportNodes.NaturalCompare("S2", "S10") < 0 && ExportNodes.NaturalCompare("S10", "S9") > 0, "S2 раньше S10");
            });
            yield return Case("перенос — внутри своего уровня; копия — с вложенными, настройки отдельно", () =>
            {
                var roots = Sample(out var high, out var mid);
                Check(ExportTask.MoveTo(roots[0].Children, mid, high) && roots[0].Children[0] == mid, "средняя — на место высокой");
                Check(ExportTask.MoveTo(roots, roots[1], roots[0]) && roots[0].Name == "ЖК Нурлы", "задача — на место другой");
                Check(!ExportTask.MoveTo(roots, high, roots[1]), "подзадача — не на уровень задач");
                var copy = high.Copy();
                copy.Settings.TestsFiles.Add("другой.xml");
                copy.Children[0].Settings.Models.Clear();
                Check(copy.Children.Count == 2 && copy.Parent == null && high.Settings.TestsFiles.Count == 1 && copy.Settings.Models == null &&
                      high.Children[0].Settings.Models.Count == 3 && copy.Children[0].Settings.MakeNwf, "копия");
                Check(roots[1].Descendants == 4 && roots[1].Self().Count() == 5, "вложенные");
            });
            yield return Case("краткая информация: подзадачи, модели (свои или от кого), файлы, NWF", () =>
            {
                var roots = Sample(out _, out _);
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>());
                string S(int i) => ExportNodes.Summary(rows[i]);
                Check(S(0) == "2 подзадачи · 7 моделей", S(0));
                Check(S(1) == "2 подзадачи · 7 моделей от «ArmanQala» · КЖ-АР.xml", S(1));
                Check(S(2) == "3 модели (свои) · КЖ-АР.xml · S1_ВК.nwf", S(2));
                Check(S(4) == "2 модели (свои) · МЕР.xml · S1_СК.nwf", S(4));
                Check(S(5) == "2 модели", "задача без подзадач, без NWF: " + S(5));
            });
            yield return Case("«модель / подзадача / блок» по числу", () =>
            {
                var words = string.Join(" ", new[] { 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111 }.Select(n => n + " " + ExportTask.ModelsWord(n)));
                Check(words == "1 модель 2 модели 4 модели 5 моделей 11 моделей 12 моделей 14 моделей 21 модель 22 модели 25 моделей 111 моделей", words);
                Check(ExportTask.SubtasksWord(1) == "подзадача" && ExportTask.SubtasksWord(3) == "подзадачи" && ExportTask.BlocksWord(9) == "блоков",
                    "подзадачи, блоки");
            });
            yield return Case("имя NWC: до части «М3» включительно, остальное отрезается; «М3» нет — как есть", () =>
            {
                string Nwc(string file, bool trim = true) => ExportNames.NwcName(file, trim, out _);
                Check(Nwc("0000_01_01_S01_01_М3_BIM_Alma") == "0000_01_01_S01_01_М3", "образец разработчика (русская М): " + Nwc("0000_01_01_S01_01_М3_BIM_Alma"));
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt"));
                Check(Nwc("0000_01_01_S6_KJ01_01_M3_еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", "с отделом и хвостом через «_»");
                Check(Nwc(@"\server\Проект\0000_01_01_S6_KJ01_01_m3.rvt") == "0000_01_01_S6_KJ01_01_m3", "путь и регистр");
                Check(Nwc(@"\\srv\Генплан.rvt") == "Генплан", "«\\\\сервер\\файл» — не пусто (.NET 8)");
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt", trim: false) == "0000_01_01_S6_KJ01_01_M3 еще текст", "без обрезки");
                Check(Nwc("0000_01_01_S6_KJ01_01_M31_x.rvt") == "0000_01_01_S6_KJ01_01_M31_x", "«M31» — не «М3»");
                var short_ = ExportNames.NwcName("Модель АР.rvt", true, out var matches);
                Check(short_ == "Модель АР" && !matches, "не по шаблону: " + short_);
                ExportNames.NwcName("0000_01_01_S6_KJ01_01_M3.rvt", true, out matches);
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
                Check(s.TrimName && s.BlockPrefix == "S" && s.DateFormat == "dd.MM.yyyy", "имя модели и дата");
                Check(NwcOptions.TryFaceting("0,5", out var f) && f == 0.5, "запятая");
                Check(NwcOptions.TryFaceting(" 2.5 ", out f) && f == 2.5, "точка");
                Check(!NwcOptions.TryFaceting("0", out _) && !NwcOptions.TryFaceting("-1", out _) &&
                      !NwcOptions.TryFaceting("абв", out _) && !NwcOptions.TryFaceting("", out _), "не число или не больше нуля");
                s.Departments.Add(new DepartmentTemplate { Department = "KJ" });
                var copy = s.Clone();
                copy.Nwc.ViewName = "другой";
                copy.Departments[0].Department = "AR";
                Check(s.Nwc.ViewName == "3D_Navisworks" && s.Departments[0].Department == "KJ", "копия не трогает исходные");
                var target = new ExportSettings();
                target.CopyFrom(s);
                Check(target.Departments.Single().Department == "KJ", "взять на место");
            });
            yield return Case("где что лежит: папки — строки по дереву с {Блок}, {Отдел}, {Дата}, {Формат}", () =>
            {
                var c = new ExportSettings { Departments = { new DepartmentTemplate { Department = "KJ" } } };
                var roots = Sample(out var high, out var mid);
                var top = roots[0];
                var s1 = high.Children[0];
                var m = s1.Settings.Models[0];
                const string root = @"\\srv\2025_Арман Кала 7 очередь";
                Check(ExportNodes.NwcPath(s1, m, c) == root + @"\S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc", ExportNodes.NwcPath(s1, m, c));
                Check(ExportNodes.NwcPath(s1, m, c) == ExportNodes.NwcPath(mid, mid.Settings.Models[0], c), "одна модель — один NWC на обе критичности");
                Check(ExportNodes.NwfPath(s1, c) == root + @"\NWF\S1_ВК.nwf" && ExportNodes.NwfPath(mid, c) == root + @"\NWF\S1_СК.nwf",
                    ExportNodes.NwfPath(s1, c));
                Check(ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2), "XML") == root + @"\Отчеты\02.11.2026", "отчёты по умолчанию");
                top.Settings.ReportFolder = @"Отчеты\{Дата}\{Формат}";
                s1.Settings.ReportFolder = @"Отчеты\{Дата}\S1\Высокая критичность\{Формат}";
                Check(ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2), "HTML") == root + @"\Отчеты\02.11.2026\S1\Высокая критичность\HTML" &&
                      ExportNodes.ReportPath(mid, c, new DateTime(2026, 11, 2), "XML") == root + @"\Отчеты\02.11.2026\XML", "своя и от задачи");
                top.Settings.NwcFolder = @"{Блок}\NWC\{Отдел}";
                Check(ExportNodes.NwcPath(s1, m, c).EndsWith(@"\S1\NWC\KJ\0000_01_01_S1_KJ01_01_M3.nwc"), "отдел — по модели");
                high.Settings.NwfFolder = @"D:\NWF\S1";
                Check(ExportNodes.NwfPath(s1, c) == @"D:\NWF\S1\S1_ВК.nwf", "полный путь — как есть");
                Check(ExportNodes.InProject("", @"{Блок}") == "{Блок}" && ExportNodes.InProject(@"\\srv\P", "") == @"\\srv\P", "пустые части");
                Check(ExportNodes.Short(root + @"\", root + @"\S1\NWC\a.nwc") == @"…\S1\NWC\a.nwc", "пример внутри папки проекта — с «…»");
                Check(ExportNodes.Short(@"\\srv\P", @"D:\NWF\S1\S1.nwf") == @"D:\NWF\S1\S1.nwf" &&
                      ExportNodes.Short(@"\\srv\P", @"\\srv\P2\a") == @"\\srv\P2\a" && ExportNodes.Short("", @"S1\NWC") == @"S1\NWC",
                    "вне папки проекта — целиком");
            });
            yield return Case("проверки строк: NWC без даты, NWF постоянная, отчёты без отдела, неизвестная метка", () =>
            {
                Check(ExportNodes.CheckNwcFolder(@"{Блок}\NWC\{Отдел}") == null && ExportNodes.CheckNwcFolder(@"{Блок}\{Дата}") != null, "NWC — без даты");
                Check(ExportNodes.CheckNwfFolder(@"NWF\S1") == null && ExportNodes.CheckNwfFolder("{Блок}") != null &&
                      ExportNodes.CheckNwfFolder(@"NWF\{Дата}") != null, "папка NWF — без меток");
                Check(ExportNodes.CheckNwfName("S1_ВК") == null && ExportNodes.CheckNwfName("{Блок}_ВК") != null, "имя NWF — без меток");
                Check(ExportNodes.CheckNwfName(" ") != null && ExportNodes.CheckNwfName("S1:ВК") != null, "пусто и недопустимые знаки");
                Check(ExportNodes.CheckReportFolder(@"Отчеты\{Дата}\S1\ВК\{Формат}") == null && ExportNodes.CheckReportFolder(@"{Отдел}") != null &&
                      ExportNodes.CheckReportFolder(@"{Блок}\{Дата}") != null,
                    "отчёты — с форматом, без отдела");
                var bad = ExportNodes.CheckReportFolder(@"{Проект}\{Дата}");
                Check(bad != null && bad.Contains("{Проект}"), "неизвестная метка: " + bad);
            });
            yield return Case("название не повторяется рядом; NWF: есть модели, {Блок} — один блок, две строки — не одна NWF", () =>
            {
                var roots = Sample(out var high, out _);
                var top = roots[0];
                Check(ExportNodes.CheckName("Низкая", new[] { "Высокая критичность" }) == null, "новое название");
                Check(ExportNodes.CheckName(" высокая критичность ", new[] { "Высокая критичность" }) != null &&
                      ExportNodes.CheckName(" ", new string[0]) != null, "повтор и пусто");
                Check(ExportNodes.CheckNwf(top, common) == null, "у строк свои NWF");
                var copy = high.Children[0].Copy();
                copy.Name = "S1_ВК (2)";
                high.Add(copy);
                Check(ExportNodes.CheckNwf(top, common) == null, "копия — по своему названию");
                copy.Settings.NwfName = "S1_ВК";
                var taken = ExportNodes.CheckNwf(top, common);
                Check(taken != null && taken.Contains("S1_ВК (2)") && taken.Contains("S1_ВК.nwf"), "одна NWF на двоих: " + taken);
                copy.Settings.NwfFolder = "Другая";
                Check(ExportNodes.CheckNwf(top, common) == null, "своя папка NWF");
                copy.Settings.Models = new List<ExportModel>();
                Check(ExportNodes.CheckNwf(top, common)?.Contains("моделей нет") == true, "без моделей");
                high.Remove(copy);
                top.Add(new ExportTask { Name = "Всё вместе", Settings = { MakeNwf = true } });
                Check(ExportNodes.CheckNwf(top, common) == null, "модели разных блоков в одной NWF — можно");
            });
            yield return Case("скрыть: поисковые наборы Navisworks из файла экспорта; простые наборы выбора — мимо; прежние строки", () =>
            {
                const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><exchange units=\"m\"><selectionsets>" +
                    "<selectionset name=\"Временные\" guid=\"a\"><findspec mode=\"all\" disjoint=\"0\"><conditions>" +
                    "<condition test=\"contains\" flags=\"10\"><category><name internal=\"LcOaNode\">Элемент</name></category>" +
                    "<property><name internal=\"LcOaSceneBaseUserName\">Имя</name></property><value><data type=\"wstring\">временн</data></value></condition>" +
                    "<condition test=\"equals\" flags=\"10\"><category><name internal=\"LcRevitData_Element\">Объект</name></category>" +
                    "<property><name internal=\"LcRevitPropertyElementCategory\">Категория</name></property><value><data type=\"wstring\">Обобщённые модели</data></value></condition>" +
                    "</conditions><locator>/</locator></findspec></selectionset>" +
                    "<viewfolder name=\"Папка\"><selectionset name=\"Есть марка\"><findspec mode=\"all\" disjoint=\"0\"><conditions>" +
                    "<condition test=\"defined\" flags=\"10\"><category><name internal=\"LcRevitData_Parameter\">Параметры</name></category>" +
                    "<property><name internal=\"Марка\">Марка</name></property></condition></conditions><locator>/</locator></findspec></selectionset></viewfolder>" +
                    "<selectionset name=\"Выбранные руками\"><selection><locator>lcop_selection_set_tree/0/1</locator></selection></selectionset>" +
                    "</selectionsets></exchange>";
                var sets = SearchSets.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)), out var skipped);
                Check(sets.Count == 2 && skipped == 1, "два поисковых, простой набор — мимо: " + sets.Count + "/" + skipped);
                Check(sets[0].Name == "Временные" && sets[0].Text == "Элемент › Имя содержит «временн»; Объект › Категория = «Обобщённые модели»", sets[0].Text);
                Check(sets[1].Name == "Есть марка" && sets[1].Text == "Параметры › Марка задано", "из папки наборов: " + sets[1].Text);
                Check(sets[0].Xml.StartsWith("<selectionset name=\"Временные\"") && sets[0].Xml.Contains("<findspec"), "набор — как в файле");
                var refused = false;
                try { SearchSets.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<a/>")), out _); } catch (InvalidDataException) { refused = true; }
                Check(refused, "не тот файл — понятный отказ");
                var old = SearchSets.FromName("врем<енн");
                Check(old.Text == "Элемент › Имя содержит «врем<енн»" && old.Xml.Contains("врем&lt;енн"), "прежняя строка — набор «Имя содержит»");
                var back = SearchSets.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<exchange><selectionsets>" + old.Xml + "</selectionsets></exchange>")), out _);
                Check(back.Single().Text == old.Text, "прежняя строка читается как набор Navisworks");
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
            yield return Case("модели: добавить без повторов; имя NWC руками или из имени; два NWC в один файл; Revit Server", () =>
            {
                var list = new List<ExportModel>();
                Check(ExportNodes.AddModels(list, new[] { "RSN://srv/AQ/a_S1_KJ01.rvt", "RSN://srv/AQ/b_S1_AR01.rvt", "rsn://SRV/aq/A_S1_KJ01.rvt", " " }) == 2 &&
                      list.Count == 2, "повтор и пустое — мимо");
                var m = new ExportModel { Path = @"\\srv\Проект\0000_01_01_S6_KJ01_01_M3 еще текст.rvt" };
                Check(ExportNodes.NwcNameOf(m, common) == "0000_01_01_S6_KJ01_01_M3", "из имени");
                m.NwcName = " КЖ_свой ";
                Check(ExportNodes.NwcNameOf(m, common) == "КЖ_свой", "руками");
                var rsn = new ExportModel { Path = "RSN://10.10.1.5/Проекты/0000_01_01_S6_AR01_01_M3.rvt" };
                Check(ExportNodes.NwcNameOf(rsn, common) == "0000_01_01_S6_AR01_01_M3", "Revit Server: " + ExportNodes.NwcNameOf(rsn, common));
                Check(ExportNodes.IsServerPath(rsn.Path) && !ExportNodes.IsServerPath(@"\\srv\m.rvt") && !ExportNodes.IsServerPath("RSN://srv/папка"),
                    "путь Revit Server");
                var roots = Sample(out var high, out _);
                Check(ExportNodes.CheckNwcNames(high, common) == null, "верные имена");
                roots[0].Settings.Models.Add(new ExportModel { Path = @"\\другой\0000_01_01_S1_KJ01_01_M3 копия.rvt" });
                var twin = ExportNodes.CheckNwcNames(high, common);
                Check(twin != null && twin.Contains("копия"), "две модели — один NWC: " + twin);
                roots[0].Settings.Models.Last().NwcName = "a:b";
                Check(ExportNodes.CheckNwcNames(high, common) != null, "недопустимые знаки");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ", "AR", "" }) == null, "коды верные");
                Check(ExportNodes.CheckDepartmentCodes(new[] { "KJ01" }) != null && ExportNodes.CheckDepartmentCodes(new[] { "KJ", "kj" }) != null,
                    "цифры в коде и повтор");
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt") == "RSN://10.10.1.88/2025_Ариал",
                    ExportNodes.ServerFolderOf("RSN://10.10.1.88/2025_Ариал/01_AR/S03/ARL.rvt"));
                Check(ExportNodes.ServerFolderOf("RSN://10.10.1.88/ARL.rvt") == "" && ExportNodes.ServerFolderOf(@"\\srv\a.rvt") == "", "нет папки / не сервер");
            });
            yield return Case("файл настроек: общие, задача с подзадачами, подзадача — туда и обратно; версии 1 и 2 читаются", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "export-files");
                Directory.CreateDirectory(dir);
                var c = new ExportSettings { BlockPrefix = "C", Departments = { new DepartmentTemplate { Department = "KJ", Etalon = @"\\srv\КЖ.rte" } } };
                c.Nwc.ViewName = "3D_NW";
                var path = Path.Combine(dir, "Общие настройки.json");
                ExportFiles.Save(path, ExportFiles.FromCommon(c));
                var back = ExportFiles.Load(path);
                Check(back.Kind == ExportFile.Common && back.Settings.BlockPrefix == "C" && back.Settings.Nwc.ViewName == "3D_NW" &&
                      back.Settings.Departments.Single().Etalon == @"\\srv\КЖ.rte", "общие");

                var roots = Sample(out var high, out var mid);
                var top = roots[0];
                top.Settings.Models.Last().Block = "S1";
                top.Settings.HideSets.Add(SearchSets.FromName("временн"));
                mid.Settings.ReportFormats = new List<string> { "XML" };
                high.Settings.Export = ExportOverride.From(new ExportSettings { RemoveIfc = false });
                path = Path.Combine(dir, "Задача.json");
                ExportFiles.Save(path, ExportFiles.FromTop(top));
                back = ExportFiles.Load(path);
                var copy = ExportFiles.ToTop(back.ProjectData, new[] { "ArmanQala" });
                Check(copy.Name == "ArmanQala (2)" && copy.Settings.Folder == top.Settings.Folder && copy.Settings.Models.Count == 7 &&
                      copy.Settings.Models.Last().Block == "S1" && copy.Settings.NwfName == null &&
                      copy.Settings.HideSets.Single().Text == "Элемент › Имя содержит «временн»", "задача: " + copy.Name);
                var h = copy.Children[0];
                var md = copy.Children[1];
                Check(h.Parent == copy && h.Settings.Models == null && h.Settings.ReportFormats == null && ExportNodes.ModelsOf(h).Count == 7 &&
                      h.Settings.Export != null && !h.Settings.Export.RemoveIfc && h.Settings.Export.Nwc.ViewName == "3D_Navisworks" &&
                      h.Children.Count == 2 && h.Children[0].Settings.Models.Count == 3 && h.Children[0].Settings.MakeNwf, "от задачи — так и осталось от задачи");
                Check(md.Settings.Models.Count == 2 && string.Join(",", md.Settings.ReportFormats) == "XML" && md.Settings.MakeNwf &&
                      md.Settings.NwfName == "S1_СК", "свои — свои");
                Check(copy.Settings != top.Settings && h.Settings.TestsFiles != high.Settings.TestsFiles, "копией");

                path = Path.Combine(dir, "Подзадача.json");
                ExportFiles.Save(path, ExportFiles.FromTask(high));
                back = ExportFiles.Load(path);
                var sub = ExportFiles.ToTask(back.TaskData, top.Children.Select(x => x.Name));
                Check(back.Kind == ExportFile.Task && sub.Name == "Высокая критичность (2)" && sub.Settings.TestsFiles.Count == 1 &&
                      sub.Settings.Models == null && sub.Children.Count == 2, "подзадача с вложенными: " + sub.Name);
                Check(ExportFiles.UniqueName("S1", new[] { "S1", "s1 (2)" }) == "S1 (3)" && ExportFiles.UniqueName(" ", new string[0]) == "Без названия",
                    "уникальные названия");

                // версия 2 (03.11.2026, днём): проект с моделями → задачи с настройками
                path = Path.Combine(dir, "Проект v2.json");
                File.WriteAllText(path, "{\"Kind\":\"Проект\",\"Version\":2,\"ProjectData\":{\"Name\":\"Старый\",\"Settings\":{\"Folder\":\"\\\\\\\\srv\\\\П\"," +
                    "\"NwcFolder\":\"{Блок}\\\\NWC\",\"ServerFolder\":\"RSN://a/b\",\"ViewName\":null},\"Tasks\":[{\"Name\":\"Высокая\",\"Options\":" +
                    "{\"Html\":false,\"NwfFolder\":\"{Блок}\",\"NwfName\":\"{Блок}_ВК\",\"ReportFolder\":\"x\",\"SkippedBlocks\":[]," +
                    "\"TestsFiles\":[\"t.xml\"],\"Xml\":true}}],\"Order\":3,\"Models\":[{\"Block\":null,\"NwcName\":null,\"Path\":\"a_S1_KJ01.rvt\"}]}}");
                var v2 = ExportFiles.ToTop(ExportFiles.Load(path).ProjectData, new string[0]);
                var hv2 = v2.Children.Single();
                Check(v2.Settings.Folder == @"\\srv\П" && v2.Settings.Models.Count == 1 && v2.Settings.ServerFolder == "RSN://a/b" &&
                      v2.Settings.NwfName == null && string.Join(",", v2.Settings.ReportFormats) == "XML,HTML", "проект → задача");
                Check(hv2.Settings.TestsFiles.Single() == "t.xml" && hv2.Settings.NwfName == "ВК" && hv2.Settings.NwfFolder == "NWF" && string.Join(",", hv2.Settings.ReportFormats) == "XML" &&
                      ExportNodes.ModelsOf(hv2).Count == 1, "задача проекта → подзадача, модели — от задачи");

                // 03.11.2026, днём: «скрыть» — строками «имя содержит»
                path = Path.Combine(dir, "Задача со строками скрытия.json");
                File.WriteAllText(path, "{\"Kind\":\"Проект\",\"Version\":3,\"ProjectData\":{\"Name\":\"С\",\"Tasks\":[],\"Node\":{\"Folder\":\"x\"," +
                    "\"HideNames\":[\"временн\"]}}}");
                var hn = ExportFiles.ToTop(ExportFiles.Load(path).ProjectData, new string[0]);
                Check(hn.Settings.HideNames == null && hn.Settings.HideSets.Single().Name == "Имя содержит «временн»", "строки скрытия — в наборы");

                // версия 1 (02.11.2026): задачи-блоки со своими моделями — модели переходят в задачу, файлы проверок — свои у подзадачи
                path = Path.Combine(dir, "Проект v1.json");
                File.WriteAllText(path, "{\"Kind\":\"Проект\",\"Version\":1,\"ProjectData\":{\"Name\":\"Старый\",\"Settings\":{\"Folder\":\"\\\\\\\\srv\\\\П\"," +
                    "\"NwcFolder\":\"{Блок}\\\\NWC\",\"NwfFolder\":\"{Блок}\",\"ReportFolder\":\"{Блок}\\\\{Дата}\",\"TestsFiles\":[\"t.xml\"]}," +
                    "\"Tasks\":[{\"Name\":\"S1\",\"Models\":[{\"Path\":\"a_S1_KJ01.rvt\"},{\"Path\":\"b_S1_AR01.rvt\"}],\"TestsFiles\":[\"s1.xml\"]}," +
                    "{\"Name\":\"S2\",\"Models\":[{\"Path\":\"a_S2_KJ01.rvt\"},{\"Path\":\"A_S1_KJ01.rvt\"}]}]}}");
                var v1 = ExportFiles.ToTop(ExportFiles.Load(path).ProjectData, new string[0]);
                Check(v1.Settings.Folder == @"\\srv\П" && v1.Settings.Models.Count == 3 && v1.Children.Count == 2, "модели — в задачу, без повторов");
                Check(v1.Settings.NwfFolder == "NWF" && v1.Settings.ReportFolder == @"Отчеты\{Дата}" && v1.Settings.NwcFolder == @"{Блок}\NWC",
                    "{Блок} в NWF и отчётах — прежние значения по умолчанию заменены, в NWC — остался");
                Check(v1.Children[0].Settings.TestsFiles.Single() == "s1.xml" && ExportNodes.TestsOf(v1.Children[1]).Single() == "t.xml" &&
                      v1.Children[0].Settings.ReportFormats == null && ExportNodes.FormatsOf(v1.Children[0]).Count == 2, "файлы проверок и форматы");

                File.WriteAllText(Path.Combine(dir, "чужой.json"), "{\"a\":1}");
                var refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "чужой.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "чужой JSON — понятный отказ");
                File.WriteAllText(Path.Combine(dir, "не json.json"), "не json");
                refused = false;
                try { ExportFiles.Load(Path.Combine(dir, "не json.json")); } catch (InvalidDataException) { refused = true; }
                Check(refused, "не JSON — понятный отказ");
            });
            yield return Case("общее хранилище: все видят все задачи; правку коллеги не затирает молча; копия .prev; удаление", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "export-store-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var ivanov = new ExportStore(dir, "Иванов");
                    var petrov = new ExportStore(dir, "Петров");
                    Check(ivanov.LoadProjects().Count == 0 && ivanov.LoadCommon().BlockPrefix == "S", "пусто — по умолчанию");

                    ivanov.SaveCommon(new ExportSettings { BlockPrefix = "C" });
                    Check(petrov.LoadCommon().BlockPrefix == "C", "общие настройки — у всех");

                    var mine = ExportTask.NewTop("Жк Нурлы");
                    mine.StoreOrder = 2;
                    mine.Settings.Models.AddRange(Models("S1", "KJ", "AR"));
                    mine.Add(new ExportTask { Name = "Высокая критичность" });
                    Check(ivanov.SaveProject(mine, false, out _, out _) == SaveResult.Saved && mine.StoreId != null, "новая задача записана");
                    var other = ExportTask.NewTop("ArmanQala");
                    other.StoreOrder = 1;
                    ivanov.SaveProject(other, false, out _, out _);
                    var seen = petrov.LoadProjects();
                    Check(string.Join(",", seen.Select(x => x.Name + x.StoreOrder)) == "ArmanQala1,Жк Нурлы2",
                        "у коллеги — обе, по порядковому номеру: " + string.Join(",", seen.Select(x => x.Name)));
                    var his = seen.Single(x => x.Name == "Жк Нурлы");
                    Check(his.StoreId == mine.StoreId && his.Settings.Models.Count == 2 && his.Children.Single().Name == "Высокая критичность",
                        "та же задача с моделями и подзадачами");

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
                    Check(!File.Exists(path) && File.Exists(path + ".prev") && petrov.LoadProjects().Count == 1, "удалена, копия осталась");
                    Check(ivanov.Reload(mine) == null, "удалённую — заново не открыть");
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
            yield return Case("Revit Server: кэш содержимого папок — по версии и пути, переживает перезапуск; версии моделей", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "RASv2.Tests", "server-cache-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var c = new ServerContents { Folders = { new ServerFolder { Name = "2025_Ариал", HasContents = true } },
                        Models = { new ServerModel { Name = "a.rvt", ModelSize = 10 } } };
                    var cache = ServerCache.Open(dir, "10.10.1.88");
                    Check(!cache.TryGet(2026, new List<string>(), out _, out _), "пусто");
                    var when = new DateTime(2026, 11, 3, 14, 20, 0);
                    cache.Put(2026, new List<string> { "2025_Ариал", "01_AR" }, c, when);
                    var again = ServerCache.Open(dir, "10.10.1.88");
                    Check(again.TryGet(2026, new List<string> { "2025_Ариал", "01_AR" }, out var back, out var at) && at == when &&
                          back.Folders.Single().Name == "2025_Ариал" && back.Models.Single().ModelSize == 10, "с диска после перезапуска");
                    Check(!again.TryGet(2023, new List<string> { "2025_Ариал", "01_AR" }, out _, out _), "у другой версии — своё");
                    Check(RevitServers.Same(c, back), "тот же ответ");
                    back.Models.Add(new ServerModel { Name = "b.rvt" });
                    Check(!RevitServers.Same(c, back), "на сервере поменялось");
                    File.WriteAllText(Path.Combine(dir, "сломанный.json"), "не json");
                    Check(!ServerCache.Open(dir, "сломанный").TryGet(2026, new List<string>(), out _, out _), "испорченный файл — как пустой");
                    Check(RevitServers.Years.SequenceEqual(new[] { 2023, 2024, 2025, 2026 }), "версии Revit Server");
                    var list = new List<ExportModel>();
                    ExportNodes.AddModels(list, new[] { new ExportModel { Path = "RSN://srv/a.rvt", Year = 2023 }, new ExportModel { Path = "rsn://SRV/a.rvt", Year = 2026 } });
                    Check(list.Single().Year == 2023, "версия модели — с выбора, повтор — мимо");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
            });
            yield return Case("удаление: подзадачи", () =>
            {
                var roots = Sample(out var high, out _);
                Check(roots[0].Descendants == 4, "у задачи: " + roots[0].Descendants);
                roots[0].Remove(high);
                Check(roots[0].Descendants == 1 && high.Parent == null, "после удаления: " + roots[0].Descendants);
            });
        }
    }
}
