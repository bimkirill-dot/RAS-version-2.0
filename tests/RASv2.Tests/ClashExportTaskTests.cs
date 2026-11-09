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
            // пути по регламенту — у новой задачи они пустые (04.11.2026), здесь заданы
            top.Settings.NwcFolder = @"{Блок}\NWC";
            top.Settings.NwfFolder = "NWF";
            top.Settings.ReportFolder = @"Отчеты\{Дата}";
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
                Check(ExportNodes.MakesReports(s1), "отчёты — и без NWF (по готовой NWF, 08.11.2026)");
                Check(!ExportNodes.MakesReports(high), "у строки с подзадачами — нет");
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
                Check(S(2) == "3 модели · КЖ-АР.xml · S1_ВК.nwf", "свои — без пометки: " + S(2));
                Check(S(4) == "2 модели · МЕР.xml · S1_СК.nwf", S(4));
                Check(S(5) == "2 модели", "задача без подзадач, без NWF: " + S(5));
            });
            yield return Case("«модель / подзадача / блок» по числу", () =>
            {
                var words = string.Join(" ", new[] { 1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 111 }.Select(n => n + " " + ExportTask.ModelsWord(n)));
                Check(words == "1 модель 2 модели 4 модели 5 моделей 11 моделей 12 моделей 14 моделей 21 модель 22 модели 25 моделей 111 моделей", words);
                Check(ExportTask.SubtasksWord(1) == "подзадача" && ExportTask.SubtasksWord(3) == "подзадачи" && ExportTask.BlocksWord(9) == "блоков",
                    "подзадачи, блоки");
            });
            yield return Case("имя NWC: обрезать всё после части («М3» — как прежний шаблон); части нет — как есть", () =>
            {
                string Nwc(string file, bool trim = true) => ExportNames.NwcName(file, trim ? "М3" : "", out _);
                Check(Nwc("0000_01_01_S01_01_М3_BIM_Alma") == "0000_01_01_S01_01_М3", "образец разработчика (русская М): " + Nwc("0000_01_01_S01_01_М3_BIM_Alma"));
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt"));
                Check(Nwc("0000_01_01_S6_KJ01_01_M3_еще текст.rvt") == "0000_01_01_S6_KJ01_01_M3", "с отделом и хвостом через «_»");
                Check(Nwc(@"\server\Проект\0000_01_01_S6_KJ01_01_m3.rvt") == "0000_01_01_S6_KJ01_01_m3", "путь и регистр");
                Check(Nwc(@"\\srv\Генплан.rvt") == "Генплан", "«\\\\сервер\\файл» — не пусто (.NET 8)");
                Check(Nwc("0000_01_01_S6_KJ01_01_M3 еще текст.rvt", trim: false) == "0000_01_01_S6_KJ01_01_M3 еще текст", "без обрезки");
                Check(Nwc("0000_01_01_S6_KJ01_01_M31_x.rvt") == "0000_01_01_S6_KJ01_01_M31_x", "«M31» — не «М3»");
                var short_ = ExportNames.NwcName("Модель АР.rvt", "М3", out var matches);
                Check(short_ == "Модель АР" && !matches, "части нет: " + short_);
                ExportNames.NwcName("0000_01_01_S6_KJ01_01_M3.rvt", "м3", out matches);
                Check(matches, "найдена, регистр и русская / латинская «М» — не важны");
                Check(ExportNames.NwcName("0000_01_01_S6_KJ01_01_M3_BIM_Alma_рабочая.rvt", "BIM_Alma", out _) == "0000_01_01_S6_KJ01_01_M3_BIM_Alma",
                      "своя часть — из нескольких слов");
                Check(ExportNames.NwcName("KM3_01_M3_x.rvt", "M3", out _) == "KM3_01_M3", "«KM3» — не часть «M3»");
                // файл до 07.11.2026 — без TrimAfter: галочка «по шаблону» → «М3», снята → пусто
                var tmp = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    foreach (var was in new[] { true, false })
                    {
                        ExportFiles.Save(tmp, ExportFiles.FromCommon(new ExportSettings { TrimAfter = null, TrimName = was }));
                        var back = ExportFiles.Load(tmp).Settings.TrimAfter;
                        Check(back == (was ? "М3" : ""), "прежний файл, галочка " + was + ": «" + back + "»");
                    }
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            });
            yield return Case("настройки отчётов (08.11.2026): по умолчанию — как в Navisworks и для сайта; файл туда и обратно; прежний файл", () =>
            {
                var d = ReportOptions.Defaults();
                Check(d.Fields.All(f => ReportOptions.AllFields.Contains(f)) && d.Statuses.SequenceEqual(ReportOptions.AllStatuses), "названия — из списков Navisworks");
                Check(new[] { "Точка конфликта", "Путь к элементу", "Дата обнаружения", "Быстрый показ свойств", "Расположение сетки", "Статус" }.All(d.Fields.Contains),
                      "поля, нужные сайту, — по умолчанию отмечены");
                Check(d.GroupMode == "Только заголовки групп" && !d.FilteredOnly, "группы, фильтр — как в Navisworks");
                var s = new ExportSettings();
                s.Report.Fields.Remove("Изображение");
                s.Report.Statuses = new List<string> { "Новая", "Активн." };
                s.Report.GroupMode = "Все";
                s.Report.FilteredOnly = true;
                var copy = new ExportSettings();
                copy.CopyFrom(s);
                s.Report.Fields.Add("Описание");
                Check(!copy.Report.Fields.Contains("Описание"), "CopyFrom — своя копия");
                var tmp = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    ExportFiles.Save(tmp, ExportFiles.FromCommon(copy));
                    var back = ExportFiles.Load(tmp).Settings.Report;
                    Check(!back.Fields.Contains("Изображение") && back.Statuses.Count == 2 && back.GroupMode == "Все" && back.FilteredOnly, "файл туда и обратно");
                    // файл до 08.11.2026 — без раздела отчётов: по умолчанию
                    ExportFiles.Save(tmp, ExportFiles.FromCommon(new ExportSettings { Report = null }));
                    back = ExportFiles.Load(tmp).Settings.Report;
                    Check(back != null && back.Fields.SequenceEqual(d.Fields) && back.GroupMode == d.GroupMode, "прежний файл — по умолчанию");
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
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
                // {Дата} — со временем записи (08.11.2026): два прогона в один день — разные папки
                Check(ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2, 17, 5, 0), "XML") == root + @"\Отчеты\02.11.2026 17-05", "отчёты по умолчанию");
                Check(ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2, 9, 30, 0), "XML") != ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2, 17, 5, 0), "XML"),
                      "в один день — разные папки");
                top.Settings.ReportFolder = @"Отчеты\{Дата}\{Формат}";
                s1.Settings.ReportFolder = @"Отчеты\{Дата}\S1\Высокая критичность\{Формат}";
                Check(ExportNodes.ReportPath(s1, c, new DateTime(2026, 11, 2), "HTML") == root + @"\Отчеты\02.11.2026 00-00\S1\Высокая критичность\HTML" &&
                      ExportNodes.ReportPath(mid, c, new DateTime(2026, 11, 2), "XML") == root + @"\Отчеты\02.11.2026 00-00\XML", "своя и от задачи");
                top.Settings.NwcFolder = @"{Блок}\NWC\{Отдел}";
                Check(ExportNodes.NwcPath(s1, m, c).EndsWith(@"\S1\NWC\KJ\0000_01_01_S1_KJ01_01_M3.nwc"), "отдел — по модели");
                high.Settings.NwfFolder = @"D:\NWF\S1";
                Check(ExportNodes.NwfPath(s1, c) == @"D:\NWF\S1\S1_ВК.nwf", "полный путь — как есть");
                Check(ExportNodes.InProject("", @"{Блок}") == "{Блок}" && ExportNodes.InProject(@"\\srv\P", "") == @"\\srv\P", "пустые части");
            });
            yield return Case("проверки строк: NWC без даты, NWF постоянная, отчёты без отдела, неизвестная метка", () =>
            {
                Check(ExportNodes.CheckNwcFolder(@"{Блок}\NWC\{Отдел}") == null && ExportNodes.CheckNwcFolder(@"{Блок}\{Дата}") != null, "NWC — без даты");
                Check(ExportNodes.CheckNwfFolder(@"NWF\S1") == null && ExportNodes.CheckNwfFolder("{Блок}") == null &&
                      ExportNodes.CheckNwfFolder(@"NWF\{Дата}") != null, "папка NWF — только {Блок} (09.11.2026)");
                Check(ExportNodes.CheckNwfName("S1_ВК") == null && ExportNodes.CheckNwfName("{Блок}_ВК") == null && ExportNodes.CheckNwfName("{Дата}_ВК") != null,
                      "имя NWF — только {Блок}");
                Check(ExportNodes.CheckNwfName(" ") != null && ExportNodes.CheckNwfName("S1:ВК") != null, "пусто и недопустимые знаки");
                Check(ExportNodes.CheckReportFolder(@"Отчеты\{Дата}\S1\ВК\{Формат}") == null && ExportNodes.CheckReportFolder(@"{Отдел}") != null &&
                      ExportNodes.CheckReportFolder(@"{Блок}\{Дата}") == null,
                    "отчёты — с форматом и блоком (09.11.2026), без отдела");
                // {Блок} строки — общий блок её моделей; разные — NWF не делается, с причиной
                var rb = Sample(out var rbHigh, out var rbMid);
                rbMid.Settings.NwfFolder = @"NWF\{Блок}";
                rbMid.Settings.MakeReports = true;
                rbMid.Settings.ReportFolder = @"Отчеты\{Блок}\{Дата}";
                Check(ExportNodes.RowBlock(rbMid, common) == "S1" && ExportNodes.NwfPath(rbMid, common).EndsWith(@"\NWF\S1\S1_СК.nwf"), "блок строки — S1");
                var rbPlan = NwfRunPlan.Build(rbMid, common, new DateTime(2026, 11, 9, 10, 0, 0)).Single();
                Check(rbPlan.Skip == null && rbPlan.Reports[0].Folder.Contains(@"\Отчеты\S1\09.11.2026 10-00"), "отчёты — в папку блока: " + rbPlan.Reports[0].Folder);
                rbMid.Settings.Models.Add(new ExportModel { Path = "0000_01_01_S2_KJ01_01_M3.rvt" });
                Check(ExportNodes.RowBlock(rbMid, common) == "" && NwfRunPlan.Build(rbMid, common).Single().Skip.StartsWith("{Блок}"), "разные блоки — NWF с причиной");
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
                // экспорт поиска «Найти элементы» — без набора (08.11.2026): правило с названием по файлу
                var find = "<exchange units=\"ft\"><findspec mode=\"all\" disjoint=\"0\"><conditions><condition test=\"contains\" flags=\"10\">" +
                           "<category><name internal=\"LcRevitData_Element\">Объект</name></category><property><name internal=\"LcRevitPropertyElementName\">Имя</name>" +
                           "</property><value><data type=\"wstring\">еменн</data></value></condition></conditions><locator>/</locator></findspec></exchange>";
                var found = SearchSets.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(find)), out _, "Скрыть_Временные-Конструкции");
                Check(found.Count == 1 && found[0].Name == "Скрыть_Временные-Конструкции" && found[0].Text == "Объект › Имя содержит «еменн»" &&
                      found[0].Xml.StartsWith("<selectionset name=\"Скрыть_Временные-Конструкции\"><findspec"), "экспорт поиска: " + found[0].Xml);
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
                var c = new ExportSettings { BlockPrefix = "C", Departments = { new DepartmentTemplate { Department = "KJ" } } };
                c.Nwc.ViewName = "3D_NW";
                var path = Path.Combine(dir, "Общие настройки.json");
                ExportFiles.Save(path, ExportFiles.FromCommon(c));
                var back = ExportFiles.Load(path);
                Check(back.Kind == ExportFile.Common && back.Settings.BlockPrefix == "C" && back.Settings.Nwc.ViewName == "3D_NW" &&
                      back.Settings.Departments.Single().Department == "KJ", "общие");

                var roots = Sample(out var high, out var mid);
                var top = roots[0];
                top.Settings.Models.Last().Block = "S1";
                top.Settings.HideSets.Add(SearchSets.FromName("временн"));
                top.Settings.Edited = new ModelDateFilter { On = true, Mode = ModelDateFilter.Between, From = new DateTime(2026, 11, 1), To = new DateTime(2026, 11, 3) };
                mid.Settings.ReportFormats = new List<string> { "XML" };
                high.Settings.Export = ExportOverride.From(new ExportSettings { RemoveIfc = false });
                path = Path.Combine(dir, "Задача.json");
                ExportFiles.Save(path, ExportFiles.FromTop(top));
                back = ExportFiles.Load(path);
                var copy = ExportFiles.ToTop(back.ProjectData, new[] { "ArmanQala" });
                Check(copy.Name == "ArmanQala (2)" && copy.Settings.Folder == top.Settings.Folder && copy.Settings.Models.Count == 7 &&
                      copy.Settings.Models.Last().Block == "S1" && copy.Settings.NwfName == null &&
                      copy.Settings.HideSets.Single().Text == "Элемент › Имя содержит «временн»" &&
                      copy.Settings.Edited.Mode == ModelDateFilter.Between && copy.Settings.Edited.To.Value.Day == 3, "задача: " + copy.Name);
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
            yield return Case("модели подзадачи — и в задачу; дата изменения модели Revit Server", () =>
            {
                var roots = Sample(out var high, out _);
                var top = roots[0];
                var s1 = high.Children[0];
                s1.Settings.Models.Add(new ExportModel { Path = "RSN://srv/AQ/0000_01_01_S1_VK01_01_M3.rvt", Year = 2026 });
                Check(ExportNodes.PushModelsUp(s1) == 1 && top.Settings.Models.Count == 8 && top.Settings.Models.Last().Year == 2026,
                      "новая модель подзадачи — в задачу (у «Высокой» своих нет — мимо)");
                Check(ExportNodes.PushModelsUp(s1) == 0, "повтор — мимо");
                high.Settings.Models = new List<ExportModel>();
                Check(ExportNodes.PushModelsUp(s1) == 4 && high.Settings.Models.Count == 4, "у строки выше со своим списком — тоже");
                var info = RevitServers.ParseModelInfo(@"{""Path"":""a\\b.rvt"",""DateCreated"":""\/Date(1788420371177)\/"",""DateModified"":""\/Date(1788766343000)\/"",""LastModifiedBy"":""Dulat-KJ-Shymkent"",""ModelSize"":1}");
                Check(info.DateModified.ToUniversalTime() == new DateTime(2026, 9, 7, 7, 32, 23, DateTimeKind.Utc) && info.LastModifiedBy == "Dulat-KJ-Shymkent",
                      "дата из ответа службы: " + info.DateModified.ToUniversalTime().ToString("u"));
                Check(RevitServers.ModelInfoUrl("10.10.1.88", 2026, new List<string> { "2025_Ариал" }, "a b.rvt") ==
                      "http://10.10.1.88/RevitServerAdminRESTService2026/AdminRESTService.svc/2025_%D0%90%D1%80%D0%B8%D0%B0%D0%BB%7Ca%20b.rvt/modelInfo", "адрес");
                var old = new ServerContents { Models = { new ServerModel { Name = "a.rvt", ModelSize = 1, Modified = new DateTime(2026, 11, 1) },
                    new ServerModel { Name = "b.rvt", ModelSize = 2, Modified = new DateTime(2026, 11, 1) } } };
                var fresh = new ServerContents { Models = { new ServerModel { Name = "a.rvt", ModelSize = 1 }, new ServerModel { Name = "b.rvt", ModelSize = 3 } } };
                RevitServers.KeepDates(old, fresh);
                Check(fresh.Models[0].Modified != null && fresh.Models[1].Modified == null, "дата — у той же модели; поменялась — заново");
            });
            yield return Case("выгружать отредактированные после / до / за период; наследуется; откуда брать модели — установленные Revit", () =>
            {
                var f = new ModelDateFilter { On = true, Mode = ModelDateFilter.After, From = new DateTime(2026, 11, 1) };
                Check(f.Passes(new DateTime(2026, 11, 1, 0, 5, 0)) && !f.Passes(new DateTime(2026, 10, 31, 23, 59, 0)) && f.Passes(null),
                      "после — с начала дня; дата не известна — выгружать");
                f.Mode = ModelDateFilter.Before;
                Check(f.Passes(new DateTime(2026, 11, 1, 23, 0, 0)) && !f.Passes(new DateTime(2026, 11, 2)), "до — включая этот день");
                f.Mode = ModelDateFilter.Between;
                f.To = new DateTime(2026, 11, 3);
                Check(f.Passes(new DateTime(2026, 11, 3, 18, 0, 0)) && !f.Passes(new DateTime(2026, 11, 4)) && !f.Passes(new DateTime(2026, 10, 30)),
                      "за период — включительно");
                f.On = false;
                Check(f.Passes(new DateTime(2020, 1, 1)), "выключен — все");

                var roots = Sample(out var high, out _);
                Check(ExportNodes.EditedOf(high.Children[0]) == null, "нигде не задан — нет отбора");
                roots[0].Settings.Edited = new ModelDateFilter { On = true, From = new DateTime(2026, 11, 1) };
                Check(ExportNodes.EditedOf(high.Children[0]).On, "от задачи — подзадачам");

                var years = RevitServers.InstalledYears(2026, y => y == 2023);
                Check(string.Join(",", years) == "2026,2023", "сначала этот Revit, затем установленные: " + string.Join(",", years));
                var hosts = new Dictionary<int, List<string>> { [2026] = new List<string> { "10.10.1.88" }, [2023] = new List<string> { "192.168.1.250", "10.10.1.88" } };
                var sources = RevitServers.Sources(years, y => hosts[y]);
                Check(string.Join(";", sources.Select(s => s.Host + "/" + s.Year)) == "10.10.1.88/2026;192.168.1.250/2023;10.10.1.88/2023",
                      "пары «сервер · версия» — из настроек каждой версии");
            });
            yield return Case("выгрузка NWC: план — модели строк без подзадач, одна NWC один раз, модели задачи без подзадач, несостыковки", () =>
            {
                var roots = Sample(out var high, out var mid);
                var notes = new List<string>();
                var jobs = NwcRunPlan.Build(roots[0], common, 2026, notes);
                // S1 (3) + S2 (2) из подзадач; S1 КЖ и АР «Средней» — те же NWC; S10 и Генплан есть только у задачи
                Check(jobs.Count == 7, "NWC: " + jobs.Count);
                Check(jobs.Count(j => j.Rows.Count == 2) == 2 && jobs.Where(j => j.Rows.Count == 2).All(j => j.Rows.Contains("Средняя критичность")),
                      "одна NWC — для двух строк");
                Check(notes.Count == 2 && notes.All(n => n.Contains("ни у одной подзадачи")), "модели только у задачи: " + string.Join(" | ", notes));
                var s1kj = jobs.First(j => j.ModelName == "0000_01_01_S1_KJ01_01_M3");
                Check(s1kj.NwcPath == @"\\srv\2025_Арман Кала 7 очередь\S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc", s1kj.NwcPath);
                Check(s1kj.View == "3D_Navisworks" && s1kj.Skip == null, "вид из общих");

                // у «Средней» свой вид — у той же NWC разные настройки: несостыковка, выгружается по первой строке
                mid.Settings.ViewName = "3D_Другой";
                notes.Clear();
                jobs = NwcRunPlan.Build(roots[0], common, 2026, notes);
                Check(notes.Count(n => n.Contains("разные вид")) == 2 && jobs.First(j => j.ModelName == "0000_01_01_S1_KJ01_01_M3").View == "3D_Navisworks",
                      "разные настройки: " + string.Join(" | ", notes));

                // запуск у подзадачи — только её модели; Revit Server другой версии — фоновому Revit её версии, такого Revit нет — пропуск
                var s2 = high.Children[1];
                s2.Settings.Models = new List<ExportModel>
                {
                    new ExportModel { Path = "RSN://192.168.1.250/Проект/0000_01_01_S2_KJ01_01_M3.rvt", Year = 2023 },
                    new ExportModel { Path = "RSN://10.10.1.88/Проект/0000_01_01_S2_AR01_01_M3.rvt", Year = 2026 },
                };
                notes.Clear();
                jobs = NwcRunPlan.Build(s2, common, 2026, notes, y => true);
                Check(jobs.Count == 2 && notes.Count == 0, "подзадача: " + jobs.Count);
                Check(jobs[0].Skip == null && jobs[1].Skip == null, "2023 в Revit 2026 — не пропуск: " + jobs[0].Skip);
                Check(NwcWorkers.RunYear(jobs[0].Model, 0, 2026) == 2023 && NwcWorkers.RunYear(jobs[1].Model, 0, 2026) == 2026, "2023 — фоновому Revit 2023");
                Check(NwcWorkers.RunYear(new ExportModel { Path = @"\\srv\a.rvt" }, 2023, 2026) == 2023 &&
                      NwcWorkers.RunYear(new ExportModel { Path = @"\\srv\a.rvt" }, 0, 2026) == 2026, "файл — по своей версии, не прочитана — этот");
                jobs = NwcRunPlan.Build(s2, common, 2026, notes, y => y == 2026);
                Check(jobs[0].Skip == "модель Revit Server 2023 — на этом компьютере нет Revit 2023", "нет Revit 2023 — пропуск: " + jobs[0].Skip);

                // одна модель — в две разные папки: несостыковка
                mid.Settings.ViewName = null;
                mid.Settings.NwcFolder = @"Средняя\NWC";
                notes.Clear();
                NwcRunPlan.Build(roots[0], common, 2026, notes);
                Check(notes.Count(n => n.Contains("выгружается 2 раза")) == 2, "две папки: " + string.Join(" | ", notes));

                // отбор «отредактированные»: нет NWC — всегда; модель не менялась в срок — пропуск
                var job = new NwcJob { Edited = new ModelDateFilter { On = true, Mode = ModelDateFilter.After, From = new DateTime(2026, 11, 1) } };
                Check(NwcRunPlan.Due(job, new DateTime(2026, 10, 1), false), "нет NWC — выгружать");
                Check(!NwcRunPlan.Due(job, new DateTime(2026, 10, 1), true), "не менялась — не выгружать");
                Check(NwcRunPlan.Due(job, new DateTime(2026, 11, 2), true) && NwcRunPlan.Due(job, null, true), "менялась или дата не известна — выгружать");
            });
            yield return Case("выгрузка NWC: сравнение с прошлой выгрузкой, история в файле, журнал со временем шагов", () =>
            {
                var before = new NwcHistory.Entry { Nwc = @"\\srv\a.nwc", Size = 300 << 20, Elements = 10000, Seconds = 120, At = new DateTime(2026, 11, 1) };
                Check(NwcHistory.Compare(before, new NwcHistory.Entry { Size = 290 << 20, Elements = 9900, Seconds = 130 }).Count == 0, "как прежде — тихо");
                var notes = NwcHistory.Compare(before, new NwcHistory.Entry { Size = 100 << 20, Elements = 5000, Seconds = 400 });
                Check(notes.Count == 3, "меньше, без элементов, дольше: " + string.Join(" | ", notes));
                Check(NwcHistory.Compare(null, before).Count == 0, "первый раз — сравнивать не с чем");

                var dir = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var history = new NwcHistory();
                    history.Put(before);
                    history.Put(new NwcHistory.Entry { Nwc = @"\\SRV\A.nwc", Size = 1 });
                    history.Save(Path.Combine(dir, "история.json"));
                    var back = NwcHistory.Load(Path.Combine(dir, "история.json"));
                    Check(back.Entries.Count == 1 && back.Last(@"\\srv\a.nwc").Size == 1, "одна запись на NWC, последняя");
                    Check(NwcHistory.Load(Path.Combine(dir, "нет.json")).Entries.Count == 0, "нет файла — пусто");

                    var path = Path.Combine(dir, "журнал.log");
                    using (var j = new ExportJournal(path))
                    {
                        j.StepStart("Открытие");
                        j.StepDone("Открытие", 12.5, true, "память Revit 2 ГБ");
                        j.StepStart("Выгрузка NWC");
                        j.StepDone("Выгрузка NWC", 3, false);
                        j.Warn("в виде было включено — рамка 3D-вида");
                        j.Error("нет 3D-вида");
                        Check(j.StepTotals().Contains("Открытие") && j.Errors.Count == 1 && j.Warnings.Count == 1, "итог шагов");
                        var text = j.ReadAll();   // пишется сразу — читается и до закрытия
                        Check(text.Contains("✓ Открытие — 0:12") && text.Contains("✗ Выгрузка NWC") && text.Contains("НЕСОСТЫКОВКА") && text.Contains("ОШИБКА"),
                              "строки журнала");
                    }
                    Check(NwcHistory.Duration(3725) == "1:02:05" && NwcHistory.SizeText(3L << 30) == "3,0 ГБ", "время и размер");
                }
                finally
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            });
            yield return Case("выбор моделей: сервер и версия отдельно — серверы всех версий без повторов; при открытии — отвечавший для версии", () =>
            {
                var hosts = new Dictionary<int, List<string>>
                {
                    [2023] = new List<string> { "10.10.1.88", "192.168.100.72", "192.168.1.250" },
                    [2026] = new List<string> { "10.10.1.88", "26.219.177.116" },
                };
                var all = RevitServers.AllHosts(new[] { 2023, 2026 }, y => hosts[y]);
                Check(string.Join(",", all) == "10.10.1.88,192.168.100.72,192.168.1.250,26.219.177.116", string.Join(",", all));
                // Revit 2023: 10.10.1.88 первый, но службы 2023 на нём нет — берётся тот, что уже отвечал
                Func<string, int, bool> answered = (h, y) => h == "192.168.1.250" || (h == "10.10.1.88" && y == 2026);
                Check(RevitServers.StartHost(all, null, 2023, answered) == "192.168.1.250", "2023 — отвечавший");
                Check(RevitServers.StartHost(all, null, 2026, answered) == "10.10.1.88", "2026 — первый отвечавший");
                Check(RevitServers.StartHost(all, "192.168.100.72", 2023, answered) == "192.168.100.72", "последний выбранный — он");
                Check(RevitServers.StartHost(all, null, 2024, (h, y) => false) == "10.10.1.88", "никто не отвечал — первый");
            });
            yield return Case("выгрузка NWC: NWC актуальна — модель не менялась после NWC, вид и параметры те же; иначе — выгружать", () =>
            {
                var roots = Sample(out _, out _);
                var job = NwcRunPlan.Build(roots[0], common, 2026, new List<string>())[0];
                var model = new DateTime(2026, 11, 4, 10, 41, 0);
                var nwc = new DateTime(2026, 11, 4, 10, 54, 0);
                var same = new NwcHistory.Entry { Signature = NwcRunPlan.Signature(job) };
                Check(NwcRunPlan.Fresh(job, model, nwc, same) != null, "не менялась, настройки те же — актуальна");
                Check(NwcRunPlan.Fresh(job, model, nwc, null) != null, "записи нет (выгружал другой) — по дате");
                Check(NwcRunPlan.Fresh(job, nwc.AddMinutes(3), nwc, same) == null, "модель новее NWC — выгружать");
                Check(NwcRunPlan.Fresh(job, null, nwc, same) == null, "дата модели не известна — выгружать");
                Check(NwcRunPlan.Fresh(job, model, null, same) == null, "NWC нет — выгружать");
                Check(NwcRunPlan.Fresh(job, model, nwc, new NwcHistory.Entry { Signature = "другой вид" }) == null, "вид или параметры поменялись — выгружать");
            });
            yield return Case("новая задача — пути пустые: NWC, NWF, отчёты — в папке задачи; без папки — модель пропускается с причиной", () =>
            {
                var task = ExportTask.NewTop("Новая");
                Check(task.Settings.Folder == "" && task.Settings.NwcFolder == "" && task.Settings.NwfFolder == "" && task.Settings.ReportFolder == "",
                      "пути пустые");
                task.Settings.Models.Add(new ExportModel { Path = "0000_01_01_S1_KJ01_01_M3.rvt" });
                var notes = new List<string>();
                var job = NwcRunPlan.Build(task, common, 2026, notes).Single();
                Check(job.Skip != null && job.Skip.StartsWith("не задана папка"), "без папки: " + job.Skip);
                task.Settings.Folder = @"\\srv\Новая";
                job = NwcRunPlan.Build(task, common, 2026, notes).Single();
                Check(job.Skip == null && job.NwcPath == @"\\srv\Новая\0000_01_01_S1_KJ01_01_M3.nwc", job.NwcPath);
                Check(ExportNodes.NwfPath(task, common) == @"\\srv\Новая\Новая.nwf", ExportNodes.NwfPath(task, common));
                Check(NwcRunPlan.IsRooted(@"C:\a") && NwcRunPlan.IsRooted(@"\\srv\a") && !NwcRunPlan.IsRooted(@"\a") && !NwcRunPlan.IsRooted("NWC"), "полный путь");
            });
            yield return Case("копий с Revit Server одновременно: по умолчанию 4; в прежнем файле поля нет — 4; заданное — сохраняется", () =>
            {
                Check(new ExportSettings().ParallelCopies == 4, "по умолчанию");
                var dir = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = new ExportStore(dir, "Poletayev_BIM");
                    store.SaveCommon(new ExportSettings { ParallelCopies = 0 });   // как прежний файл: читается 0
                    Check(store.LoadCommon().ParallelCopies == 4, "прежний файл — 4");
                    store.SaveCommon(new ExportSettings { ParallelCopies = 6 });
                    Check(store.LoadCommon().ParallelCopies == 6, "заданное — 6");
                    var copy = new ExportSettings();
                    copy.CopyFrom(new ExportSettings { ParallelCopies = 2 });
                    Check(copy.ParallelCopies == 2, "CopyFrom");
                }
                finally
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            });
            yield return Case("таблица задач: столбцы — подзадачи, модели, проверки, NWF, «↑» — от строки выше; «Выгружено» — по самой старой NWC", () =>
            {
                var roots = Sample(out var high, out var mid);
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>());
                RowColumns C(int i) => ExportNodes.Columns(rows[i]);
                Check(C(0).Subtasks == "2" && C(0).Models == "7" && C(0).ModelsFrom == null && C(0).Tests == "—" && C(0).Nwf == "—", "задача");
                Check(C(1).Subtasks == "2" && C(1).ModelsFrom == "ArmanQala" && C(1).Tests == "КЖ-АР.xml" && C(1).TestsFrom == null, "критичность: модели от задачи");
                Check(C(2).Subtasks == "—" && C(2).Models == "3" && C(2).ModelsFrom == null && C(2).TestsFrom == "Высокая критичность" && C(2).Nwf == "S1_ВК.nwf",
                      "подзадача: свои модели, проверки от «Высокой»: " + C(2).TestsFrom);

                // выключатели списка (07.11.2026): у строки с подзадачами — сразу у всех ниже, по-разному — null
                var s1 = high.Children[0];
                var s2 = high.Children[1];
                Check(C(2).NwfOn && C(2).NwfState == true && C(1).NwfState == true && C(0).NwfState == true, "NWF: у всех строк без подзадач — да");
                Check(C(0).NwcState == true && C(0).ReportsState == false && C(0).ReportsEnabled, "NWC — да, отчёты — нет");
                Check(ExportNodes.SetFlag(s1, RowFlag.Nwf, false, common) == null && !s1.Settings.MakeNwf && C(2).Nwf == "—" && C(1).NwfState == null && C(0).NwfState == null,
                      "снята у S1 — у «Высокой» и задачи по-разному");
                Check(ExportNodes.SetFlag(s1, RowFlag.Reports, true, common) == null && s1.Settings.MakeReports && C(2).ReportsState == true,
                      "отчёты без NWF — можно (по готовой NWF)");
                Check(ExportNodes.SetFlag(high, RowFlag.Nwf, true, common) == null && s1.Settings.MakeNwf && s2.Settings.MakeNwf && !high.Settings.MakeNwf &&
                      C(1).NwfState == true, "у строки с подзадачами — у всех ниже, у неё самой — нет");
                s2.Settings.NwfName = "S1_ВК";
                ExportNodes.SetFlag(s1, RowFlag.Nwf, false, common);
                var clash = ExportNodes.SetFlag(high, RowFlag.Nwf, true, common);
                Check(clash != null && clash.Contains("и у") && !s1.Settings.MakeNwf && s2.Settings.MakeNwf, "та же NWF у двух строк — нельзя, всё как было: " + clash);
                s2.Settings.NwfName = null;
                Check(ExportNodes.SetFlag(roots[0], RowFlag.Nwf, true, common) == null && C(2).Nwf == "S1_ВК.nwf", "поставлена");
                Check(ExportNodes.SetFlag(roots[0], RowFlag.Reports, true, common) == null && s1.Settings.MakeReports && mid.Settings.MakeReports &&
                      C(0).ReportsState == true, "отчёты — у всех, кто собирает NWF");

                Check(ExportNodes.SetFlag(high, RowFlag.Nwc, false, common) == null && high.Settings.NoNwc && s1.Settings.NoNwc && s2.Settings.NoNwc &&
                      !roots[0].Settings.NoNwc && C(0).NwcState == null && C(1).NwcState == false, "NWC выключен у ветки — у задачи по-разному");
                var plan = NwcRunPlan.Build(roots[0], common, 2026, new List<string>());
                var kj1 = plan.Single(j => j.NwcPath.EndsWith(@"S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc"));
                Check(!kj1.Off && kj1.Skip == null, "модель S1 нужна и «Средней» (NWC включён) — выгружается: " + kj1.Skip);
                mid.Settings.NoNwc = true;
                plan = NwcRunPlan.Build(roots[0], common, 2026, new List<string>());
                Check(plan.Where(j => j.Rows.Any(r => r != "ArmanQala")).All(j => j.Off && j.Skip == "выгрузка NWC выключена (галочка «NWC»)"),
                      "выключен у всех строк этой NWC — не выгружается");
                Check(NwfRunPlan.Build(roots[0], common).Count(n => n.Skip == null) == 3, "NWF собираются и без выгрузки NWC");
                var copy = s1.Settings.Clone();
                Check(copy.NoNwc, "копия строки — с выключателем");


                var t1 = new DateTime(2026, 11, 4, 10, 54, 0);
                var t2 = new DateTime(2026, 11, 3, 9, 0, 0);
                Check(NwcRunPlan.ExportedText(new List<(string, DateTime?)> { (@"\\s\a.nwc", t1), (@"\\s\b.nwc", t2) }, out var tip) == "03.11.26 09:00" &&
                      tip.Contains("a.nwc — 04.11.2026 10:54"), "все есть — самая старая");
                Check(NwcRunPlan.ExportedText(new List<(string, DateTime?)> { (@"\\s\a.nwc", t1), (@"\\s\b.nwc", null) }, out tip) == "1 из 2" &&
                      tip.Contains("b.nwc — нет"), "есть не все");
                Check(NwcRunPlan.ExportedText(new List<(string, DateTime?)> { (@"\\s\a.nwc", null) }, out _) == "—", "нет ни одной");
                Check(NwcRunPlan.ExportedText(new List<(string, DateTime?)>(), out tip) == "—" && tip == null, "NWC у строки нет");
            });
            yield return Case("RVT (08.11.2026): тем же открытием, что NWC; папка с {Дата}; только RVT; имя; выключатель; итог функции", () =>
            {
                var roots = Sample(out var high, out var mid);
                var when = new DateTime(2026, 11, 8, 18, 5, 0);
                var notes = new List<string>();
                Check(NwcRunPlan.Build(roots[0], common, 2026, notes, null, when).All(j => j.RvtPath == null), "без галочки — RVT нет");
                mid.Settings.MakeRvt = true;
                mid.Settings.RvtFolder = @"RVT\{Блок}\{Дата}";
                var jobs = NwcRunPlan.Build(roots[0], common, 2026, notes, null, when);
                var kj = jobs.Single(j => j.NwcPath.EndsWith(@"S1\NWC\0000_01_01_S1_KJ01_01_M3.nwc"));
                Check(kj.RvtPath == @"\\srv\2025_Арман Кала 7 очередь\RVT\S1\08.11.2026 18-05\0000_01_01_S1_KJ01_01_M3.rvt" && !kj.Off && kj.Skip == null,
                      "модель «Средней» — NWC и RVT одной строкой плана: " + kj.RvtPath);
                Check(jobs.Single(j => j.NwcPath.EndsWith("S1_OV01_01_M3.nwc")).RvtPath == null, "модель без RVT у своих строк — без RVT");
                common.Rvt.TrimAfter = "M3";
                Check(NwcRunPlan.Build(mid, common, 2026, notes, null, when).All(j => j.RvtPath.EndsWith(@"_M3.rvt")) &&
                      ExportNodes.RvtNameOf(new ExportModel { Path = "0000_01_01_S1_KJ01_01_M3_BIM_Alma.rvt" }, common) == "0000_01_01_S1_KJ01_01_M3", "имя — своё правило «обрезать после»");
                common.Rvt.TrimAfter = "";
                // только RVT: «NWC» снята — модель всё равно открывается, NWC не выгружается
                foreach (var t in roots[0].Self()) t.Settings.NoNwc = true;
                jobs = NwcRunPlan.Build(roots[0], common, 2026, notes, null, when);
                Check(jobs.Where(j => j.RvtPath != null).All(j => j.Off && j.Skip == null) &&
                      jobs.Where(j => j.RvtPath == null).All(j => j.Skip == "выгрузка NWC выключена (галочка «NWC»)"), "только RVT — не пропуск; ни NWC, ни RVT — пропуск");
                foreach (var t in roots[0].Self()) t.Settings.NoNwc = false;
                // папки нет — пропуск с причиной
                roots[1].Settings.MakeRvt = true;
                roots[1].Settings.Folder = "";
                roots[1].Settings.NoNwc = true;
                Check(NwcRunPlan.Build(roots[1], common, 2026, notes, null, when).All(j => j.Skip.Contains("«Папке для RVT»")), "без папки RVT — пропуск");
                // выключатель в списке — как у NWC: у строки с подзадачами — у всех ниже
                var rows = ExportNodes.Rows(roots, new HashSet<ExportTask>());
                Check(ExportNodes.Columns(rows[0]).RvtState == null && ExportNodes.Columns(rows[4]).RvtState == true, "у задачи — по-разному, у «Средней» — да");
                Check(ExportNodes.SetFlag(roots[0], RowFlag.Rvt, true, common) == null && roots[0].Self().All(t => t.Settings.MakeRvt) &&
                      ExportNodes.Columns(rows[0]).RvtState == true, "включён у всей ветки");
                Check(mid.Settings.Clone().MakeRvt && mid.Settings.Clone().RvtFolder == @"RVT\{Блок}\{Дата}", "копия строки — с RVT");
                Check(ExportNodes.CheckRvtFolder(@"RVT\{Блок}\{Отдел}\{Дата}") == null && ExportNodes.CheckRvtFolder(@"RVT\{Формат}") != null, "метки папки RVT");
                // фоновый Revit другой версии — RVT и «только RVT» доезжают
                var only = jobs.First(j => j.RvtPath != null);
                only.Off = true;
                var back = NwcWorkers.FromHandoff(NwcWorkers.ToHandoff(only));
                Check(back.RvtPath == only.RvtPath && back.Off, "задание фоновому Revit — с RVT");
                // итог функции RVT — своим значком
                Check(RunStates.KindOf(@"\\s\a.rvt") == "RVT" && RunStates.KindOf(@"\\s\a.nwc") == "NWC" && RunStates.KindOf(@"\\s\a.nwf") == "NWF", "функция по файлу");
                var run = RunStates.Record(mid, new[] { @"\\s\a.nwc", @"\\s\a.rvt" },
                    new[] { (@"\\s\a.nwc", "a", ItemResult.Done, "1 МБ"), (@"\\s\a.rvt", "a", ItemResult.Failed, "RVT занят") }, when, 60, null, null);
                Check(RunStates.MarkOf(run, "NWC") == StepMark.Ok && RunStates.MarkOf(run, "RVT") == StepMark.Errors, "NWC — зелёный, RVT — красный");
                // общие настройки «Экспорт RVT»: по умолчанию, копия, файл без раздела — по умолчанию
                var d = new RvtOptions();
                Check(!d.Audit && d.KeepWorksets && d.Overwrite && d.TrimAfter == "", "по умолчанию: без проверки, рабочие наборы оставить, перезаписывать, имя модели");
                var s = new ExportSettings();
                s.Rvt.KeepWorksets = false;
                var c = new ExportSettings();
                c.CopyFrom(s);
                s.Rvt.Audit = true;
                Check(!c.Rvt.KeepWorksets && !c.Rvt.Audit && !s.Clone().Rvt.KeepWorksets && !ReferenceEquals(s.Clone().Rvt, s.Rvt), "CopyFrom и Clone — своя копия");
                // пересохранение на место (08.11.2026): тем же открытием; «только пересохранение» — не пропуск; свои «Настройки RVT» строки
                var roots2 = Sample(out _, out var mid2);
                mid2.Settings.Resave = true;
                mid2.Settings.NoNwc = true;
                var j2 = NwcRunPlan.Build(roots2[0], common, 2026, notes, null, when);
                Check(j2.Where(j => j.Rows.Contains(mid2.Name)).All(j => j.Resave && j.Skip == null) && j2.Count(j => j.Resave) == 2,
                      "пересохранение — у моделей «Средней», и без NWC не пропуск");
                mid2.Settings.ResaveOptions = new ResaveOptions { Compact = false, BackupFolder = @"\\srv\Копии" };
                var own = NwcRunPlan.Build(mid2, common, 2026, notes, null, when);
                Check(own.All(j => j.BackupDir == @"\\srv\Копии\08.11.2026 18-05"), "резервные копии — в папку запуска с датой и временем: " + own[0].BackupDir);
                Check(own.All(j => !j.Export.Resave.Compact && j.Export.Resave.BackupDir == @"\\srv\Копии") && common.Resave.Compact,
                      "свои «Настройки пересохранения» строки — в параметры модели, общие не меняются");
                Check(ExportNodes.ResaveOptionsOf(mid2, common).BackupFolder == @"\\srv\Копии" && ExportNodes.ResaveOptionsOf(roots2[0], common) == common.Resave, "чьи настройки");
                Check(mid2.Settings.Clone().Resave && mid2.Settings.Clone().ResaveOptions.BackupFolder == @"\\srv\Копии", "копия строки — с пересохранением");
                Check(new ResaveOptions().Compact && new ResaveOptions().Audit && new ResaveOptions().BackupDir == ResaveOptions.DefaultBackupDir,
                      "по умолчанию: сжать, проверить, копии — на этом компьютере");
                var key = RunStates.ResaveKey(@"RSN://srv/Проект/a.rvt");
                Check(RunStates.KindOf(key) == "RESAVE" && RunStates.KindOf(@"RSN://srv/Проект/a.rvt") == "RVT", "итог пересохранения — своя функция");
                var h2 = NwcWorkers.FromHandoff(NwcWorkers.ToHandoff(own[0]));
                Check(h2.Resave && h2.Export.Resave.BackupFolder == @"\\srv\Копии" && h2.BackupDir == own[0].BackupDir, "задание фоновому Revit — с пересохранением и своими настройками");
                Check(ExportNodes.SetFlag(roots2[0], RowFlag.Resave, true, common) == null && roots2[0].Self().All(t => t.Settings.Resave), "выключатель — у всей ветки");
                var tmp = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    ExportFiles.Save(tmp, ExportFiles.FromCommon(s));
                    var r = ExportFiles.Load(tmp).Settings.Rvt;
                    Check(r.Audit && !r.KeepWorksets, "файл туда и обратно");
                    ExportFiles.Save(tmp, ExportFiles.FromCommon(new ExportSettings { Rvt = null }));
                    Check(ExportFiles.Load(tmp).Settings.Rvt?.KeepWorksets == true, "прежний файл — по умолчанию");
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            });
            yield return Case("NWF: какие строки собирают, из каких NWC; строки хода программы; что добавить в постоянную NWF; какой Navisworks", () =>
            {
                var roots = Sample(out var high, out var mid);
                var plans = NwfRunPlan.Build(roots[0], common);
                Check(string.Join(" ", plans.Select(p => p.Row.Name)) == "S1_ВК S2_ВК Средняя критичность", "строки с галочкой: " + string.Join(" ", plans.Select(p => p.Row.Name)));
                Check(plans[0].NwfPath == @"\\srv\2025_Арман Кала 7 очередь\NWF\S1_ВК.nwf" && plans[0].Skip == null, "путь NWF: " + plans[0].NwfPath);
                var nwcs = NwcRunPlan.Build(high.Children[0], common, 2026, new List<string>()).Select(j => j.NwcPath).ToList();
                Check(plans[0].Nwcs.Count == 3 && plans[0].Nwcs.SequenceEqual(nwcs), "NWC — те же пути, что у выгрузки: " + string.Join(", ", plans[0].Nwcs));
                Check(NwfRunPlan.Build(roots[1], common).Count == 0, "без галочки — NWF нет");
                // «Выполнять проверки» (08.11.2026): снята — проверок в задании нет
                Check(plans[0].RunTests && plans.Last().Tests.Count == 1, "по умолчанию — выполнять");
                // отчёты (08.11.2026): у строки «Формировать отчёты» — по формату папка на время запуска
                Check(plans.All(p => p.Reports.Count == 0), "без «Формировать отчёты» — отчётов нет");
                mid.Settings.MakeReports = true;
                var withReports = NwfRunPlan.Build(roots[0], common, new DateTime(2026, 11, 8, 18, 5, 0)).Single(p => p.Row == mid);
                Check(withReports.Reports.Select(r => r.Format).SequenceEqual(new[] { "XML", "HTML" }) &&
                      withReports.Reports[0].Folder.EndsWith(@"\Отчеты\08.11.2026 18-05"), "XML и HTML — в папку с датой и временем: " + withReports.Reports[0].Folder);
                Check(withReports.ReportOptions != null && withReports.ReportOptions.GroupMode == common.Report.GroupMode &&
                      !ReferenceEquals(withReports.ReportOptions, common.Report), "настройки отчётов — копией общих");
                mid.Settings.NoTestsRun = true;
                Check(NwfRunPlan.Build(roots[0], common).Single(p => p.Row == mid) is NwfPlan np && np.Reports.Count == 0 &&
                      np.ReportsSkip.Contains("«Выполнять проверки» снята"), "проверки не выполняются — отчётов нет, с причиной (09.11.2026)");
                mid.Settings.NoTestsRun = false;
                mid.Settings.MakeReports = false;
                mid.Settings.NoTestsRun = true;
                var noTests = NwfRunPlan.Build(roots[0], common).Single(p => p.Row == mid);
                Check(!noTests.RunTests && noTests.Tests.Count == 0 && mid.Settings.Clone().NoTestsRun, "снята — без проверок");
                mid.Settings.NoTestsRun = false;
                // галочки по отдельности (08.11.2026): «Отчёты» без «NWF» — по готовой NWF: прогон и отчёты, без скрытия
                mid.Settings.MakeNwf = false;
                mid.Settings.MakeReports = true;
                mid.Settings.HideSets = new List<HideSet> { SearchSets.FromName("временн") };
                var only = NwfRunPlan.Build(roots[0], common, null, f => true).Single(p => p.Row == mid);
                Check(only.ReportsOnly && only.Skip == null && only.Reports.Count == 2 && only.HideSets.Count == 0 && only.RunTests,
                      "только отчёты: NWF есть — прогон и отчёты, без скрытия: " + only.Skip);
                Check(NwfRunPlan.Build(roots[0], common, null, f => false).Single(p => p.Row == mid).Skip.StartsWith("NWF ещё нет"), "NWF нет — пропуск");
                mid.Settings.NoTestsRun = true;
                Check(NwfRunPlan.Build(roots[0], common, null, f => true).Single(p => p.Row == mid).Skip.Contains("«Выполнять проверки» снята"),
                      "проверки не выполняются — отчётов нет, пропуск");
                mid.Settings.NoTestsRun = false;
                Check(!NwfRunPlan.Build(roots[0], common, null, f => true).Any(p => p.Row == high.Children[0] && p.ReportsOnly), "с «NWF» — не «только отчёты»");
                mid.Settings.MakeReports = false;
                Check(NwfRunPlan.Build(roots[0], common).All(p => p.Row != mid), "ни NWF, ни отчётов — строки нет");
                mid.Settings.MakeNwf = true;
                mid.Settings.HideSets = null;
                roots[1].Settings.MakeNwf = true;
                roots[1].Settings.Folder = "";
                Check(NwfRunPlan.Build(roots[1], common)[0].Skip.StartsWith("не задана папка"), "без папки — пропуск");

                Check(NwfTool.Parse(NwfTool.StepLine(2, "Открытие NWF"), out var i, out _, out _, out var text) == '@' && i == 2 && text == "Открытие NWF", "шаг");
                Check(NwfTool.Parse(NwfTool.DoneLine(1, NwfTool.Ok, 12.34, "обновлена: NWC 4\nстрока"), out i, out var result, out var sec, out text) == '=' &&
                      i == 1 && result == "ok" && Math.Abs(sec - 12.3) < 0.01 && text == "обновлена: NWC 4 строка", "итог: " + text);
                Check(NwfTool.Parse("  Navisworks запущен за 3,6 с", out i, out _, out _, out _) == ' ' && i == -1, "просто строка");
                Check(NwfTool.Parse("@ x шаг", out i, out _, out _, out _) == ' ' && NwfTool.Parse("= 1 ok", out _, out _, out _, out _) == ' ', "испорченные — просто строки");

                NwfTool.Diff(new[] { @"\\s\NWC\KJ.nwc", @"\\s\NWC\AR.nwc", @"\\s\Старое\OV.nwc", @"\\s\NWC\VK.nwc" },
                    new[] { @"\\s\NWC\kj.nwc", @"\\s\NWC\AR.nwc", @"\\s\NWC\OV.nwc", @"\\s\NWC\EL.nwc" }, out var append, out var elsewhere, out var extra);
                Check(append.SequenceEqual(new[] { @"\\s\NWC\EL.nwc" }), "добавить — новую: " + string.Join(", ", append));
                Check(elsewhere.SequenceEqual(new[] { @"\\s\NWC\OV.nwc" }), "та же NWC из другой папки — не добавлять");
                Check(extra.SequenceEqual(new[] { @"\\s\NWC\VK.nwc" }), "лишняя в NWF — сказать: " + string.Join(", ", extra));

                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    @"P:\Autodesk\Navisworks Simulate 2023\Autodesk.Navisworks.Automation.dll", @"P:\Autodesk\Navisworks Simulate 2023\Roamer.exe",
                    @"P:\Autodesk\Navisworks Manage 2024\Autodesk.Navisworks.Automation.dll", @"P:\Autodesk\Navisworks Manage 2024\Roamer.exe",
                };
                Check(NwfTool.FindNavisworks(@"P:\", files.Contains) == @"P:\Autodesk\Navisworks Simulate 2023", "сначала 2023");
                files.Add(@"P:\Autodesk\Navisworks Manage 2023\Roamer.exe");
                Check(NwfTool.FindNavisworks(@"P:\", files.Contains) == @"P:\Autodesk\Navisworks Simulate 2023", "Manage без Automation — не он");
                Check(NwfTool.FindNavisworks(@"P:\", f => false) == null, "нет — null");

                var dir = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var file = Path.Combine(dir, "job.json");
                    NwfTool.Write(file, new NwfToolJob { NavisworksDir = @"C:\N", Items = { new NwfToolItem { Nwf = @"\\s\S1_ВК.nwf", Nwcs = { @"\\s\a.nwc" } } } });
                    var back = NwfTool.Read<NwfToolJob>(file);
                    Check(back.NavisworksDir == @"C:\N" && back.Items.Single().Nwf == @"\\s\S1_ВК.nwf" && back.Items[0].Nwcs.Single() == @"\\s\a.nwc", "задание туда и обратно");
                }
                finally
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            });
            yield return Case("галочка подзадачи: снята — при запуске строки выше не делается (NWC и NWF), её модели не выгружает и строка выше", () =>
            {
                var roots = Sample(out var high, out var mid);
                var s1 = high.Children[0];
                var all = NwcRunPlan.Build(roots[0], common, 2026, new List<string>()).Count;
                s1.Settings.Skipped = true;
                Check(s1.IsSkipped && !high.IsSkipped && roots[0].Active().Count() == 4 && !roots[0].Active().Contains(s1), "снята — не в запуске");
                var notes = new List<string>();
                var jobs = NwcRunPlan.Build(roots[0], common, 2026, notes);
                Check(jobs.All(j => !j.Rows.Contains("S1_ВК")) && !notes.Any(n => n.Contains("_S1_")) && !jobs.Any(j => j.ModelName.Contains("_S1_OV")),
                      "модели S1 — ни у неё, ни у задачи «ни у одной подзадачи»: " + string.Join(" | ", notes));
                Check(jobs.Count < all, "NWC меньше: " + jobs.Count + " из " + all);
                Check(!NwfRunPlan.Build(roots[0], common).Any(n => n.Row == s1), "NWF S1 — нет");
                Check(NwcRunPlan.Build(s1, common, 2026, new List<string>()).Count == 3 && NwfRunPlan.Build(s1, common).Count == 1, "▷ у неё самой — делается");
                high.Settings.Skipped = true;
                Check(high.Children[1].IsSkipped && roots[0].Active().Count() == 2, "снята строка выше — серые и все под ней");
                Check(s1.Settings.Clone().Skipped, "копия строки — с галочкой");
            });
            yield return Case("пакетно: выбранные без подзадач выбранных; копии — с подзадачами, «(2)», подзадача — под своей, задача — в конец", () =>
            {
                var roots = Sample(out var high, out var mid);
                var s1 = high.Children[0];
                var outer = ExportTask.Outermost(roots, new[] { s1, roots[1], high, mid });
                Check(outer.SequenceEqual(new[] { high, mid, roots[1] }), "S1 — внутри выбранной «Высокой»; порядок — как в дереве: " +
                      string.Join(", ", outer.Select(t => t.Name)));
                var order = 5;
                var copies = ExportTask.CopyRows(roots, new[] { s1, roots[1] }, () => ++order);
                Check(copies.Count == 2 && high.Children[1].Name == "S1_ВК (2)" && high.Children[1].Settings.MakeNwf && high.Children[1] != s1,
                      "подзадача — сразу под своей, «(2)»: " + string.Join(", ", high.Children.Select(c => c.Name)));
                Check(roots.Count == 3 && roots[2].Name == "ЖК Нурлы (2)" && roots[2].StoreOrder == 6 && roots[2].StoreId == null,
                      "задача — в конец, новый номер, новый файл");
                var deep = ExportTask.CopyRows(roots, new[] { high }, () => ++order);
                Check(deep.Single().Children.Count == 3 && roots[0].Children[1].Name == "Высокая критичность (2)", "копия — с подзадачами");
                Check(ExportTask.RowsWord(1) == "строка" && ExportTask.RowsWord(3) == "строки" && ExportTask.RowsWord(5) == "строк", "слово");
            });
            yield return Case("статус строки: итог запуска в папке задачи, «Повторить ошибки», время, модель изменилась после NWC", () =>
            {
                var roots = Sample(out var high, out var mid);
                var s1 = high.Children[0];
                Check(RunStates.Key(s1) == "ArmanQala › Высокая критичность › S1_ВК", "ключ — по названиям: " + RunStates.Key(s1));
                Check(RunStates.PathOf(s1) == @"\\srv\2025_Арман Кала 7 очередь\Журналы выгрузки\состояние.json", "файл — в папке задачи");
                roots[1].Settings.Folder = "";
                Check(RunStates.PathOf(roots[1]) == Path.Combine(RunStates.LocalDir, "ЖК Нурлы.json"), "без папки задачи — на этом компьютере (09.11.2026)");
                // задача без папки — только пересохранение: папки не нужны; NWC — пропуск с причиной
                Check(NwcRunPlan.Build(roots[1], common, 2026, new List<string>()).All(j => j.Skip.StartsWith("не задана папка")), "NWC без папки — пропуск");
                roots[1].Settings.NoNwc = true;
                roots[1].Settings.Resave = true;
                Check(NwcRunPlan.Build(roots[1], common, 2026, new List<string>()).All(j => j.Skip == null && j.Resave), "только пересохранение — без папок");
                // модель отключена галочкой (09.11.2026) — не делается совсем; у другой строки включена — делается
                var r2 = Sample(out var high3, out var mid3);
                mid3.Settings.Models[0].Off = true;
                var withOff = NwcRunPlan.Build(mid3, common, 2026, new List<string>());
                Check(withOff[0].Skip == "модель отключена (снята галочка у модели в «Моделях»)" && withOff[1].Skip == null, "отключённая — пропуск");
                Check(NwcRunPlan.Build(r2[0], common, 2026, new List<string>()).Single(j => j.NwcPath == withOff[0].NwcPath).Skip == null,
                      "та же модель у «S1_ВК» включена — делается");
                Check(mid3.Settings.Clone().Models[0].Off, "копия строки — с отключённой моделью");
                Check(NwfRunPlan.Build(mid3, common).Single().Nwcs.Contains(withOff[0].NwcPath), "NWF — с NWC отключённой модели (та, что лежит)");
                // NWC включён (по умолчанию), папки нет, а пересохранение есть — модель не пропускается, NWC — с причиной (09.11.2026)
                roots[1].Settings.NoNwc = false;
                var plain = new List<string>();
                var mixed = NwcRunPlan.Build(roots[1], common, 2026, plain);
                Check(mixed.All(j => j.Skip == null && j.Off && j.NwcSkip.StartsWith("не задана папка") && j.Resave) &&
                      plain.Any(n => n.StartsWith("NWC не выгружаются")), "без папки — только NWC пропускается, пересохранение делается");
                roots[1].Settings.Resave = false;

                var at = new DateTime(2026, 11, 7, 15, 20, 0);
                var items = new List<(string, string, ItemResult, string)>
                {
                    (@"\\s\a.nwc", "A", ItemResult.Done, null), (@"\\s\b.nwc", "B", ItemResult.Failed, "NWC занята"),
                    (@"\\s\c.nwc", "C", ItemResult.Skipped, "не изменялась в срок"), (@"\\s\S1.nwf", "S1.nwf", ItemResult.Done, "собрана"),
                    (@"\\s\other.nwc", "D", ItemResult.Failed, "чужая"),
                };
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\s\a.nwc", @"\\s\b.nwc", @"\\s\c.nwc", @"\\s\S1.nwf" };
                var run = RunStates.Record(s1, paths, items, at, 218, @"\\s\журнал.log", new[] { "Одна NWC — у двух моделей" });
                Check(run.Done == 2 && run.Skipped == 1 && run.Errors.Count == 1 && run.Errors[0].Name == "B" && run.Errors[0].Note == "NWC занята",
                      "только NWC и NWF строки: " + run.Errors.Count);
                Check(run.Notes.SequenceEqual(new[] { "C — не изменялась в срок", "Одна NWC — у двух моделей" }), "пропуски и несостыковки: " + string.Join(" | ", run.Notes));

                var retry = RunStates.Record(s1, paths, new List<(string, string, ItemResult, string)> { (@"\\s\b.nwc", "B", ItemResult.Done, null) },
                    at.AddHours(1), 40, null, null, new HashSet<string> { @"\\s\b.nwc" }, run);
                Check(retry.Errors.Count == 0 && retry.Done == 3 && retry.Seconds == 218 && retry.At == at.AddHours(1) && retry.Notes.Count == 2,
                      "повтор ошибок: ошибка ушла, время — прежнего запуска");
                var still = RunStates.Record(s1, paths, new List<(string, string, ItemResult, string)>(), at, 0, null, null, new HashSet<string> { @"\\s\x.nwc" },
                    run);
                Check(still.Errors.Count == 1 && still.Errors[0].Path == @"\\s\b.nwc", "не повторяли — ошибка остаётся");

                // по функциям: значок у галочки NWC и у галочки NWF — каждый о своём (07.11.2026)
                Check(RunStates.MarkOf(run, "NWC") == StepMark.Errors && RunStates.MarkOf(run, "NWF") == StepMark.Ok, "NWC — ошибка (B), NWF — собрана");
                Check(RunStates.StepOf(run, "NWC").Done == 1 && RunStates.StepOf(run, "NWC").Skipped == 1 && RunStates.StepOf(run, "NWF").Done == 1, "счёт по функциям");
                Check(RunStates.MarkOf(null, "NWC") == StepMark.None && RunStates.MarkOf(new RowRun(), "NWF") == StepMark.None, "не запускали — без значка");
                var nwcTip = RunStates.StepTip(run, "NWC", new List<string> { "KJ" }, false);
                var nwfTip = RunStates.StepTip(run, "NWF", new List<string>(), false);
                Check(nwcTip.Contains("B — NWC занята") && nwcTip.Contains("↻") && nwcTip.Contains("Модели изменились") && !nwfTip.Contains("B —") &&
                      nwfTip.Contains("собрано 1"), "подсказки — каждая о своей функции");
                var legacy = new RowRun { Done = 3, Errors = { new RunIssue { Name = "S1.nwf", Path = @"\\s\S1.nwf", Note = "занята" } }, Steps = new List<StepRun>() };
                Check(RunStates.MarkOf(legacy, "NWC") == StepMark.Ok && RunStates.MarkOf(legacy, "NWF") == StepMark.Errors, "запуск до 07.11 — по ошибкам");
                var nwfRetry = RunStates.Record(s1, paths, new List<(string, string, ItemResult, string)> { (@"\\s\S1.nwf", "S1.nwf", ItemResult.Done, null) },
                    at.AddHours(2), 5, null, null, new HashSet<string> { @"\\s\S1.nwf" }, run);
                Check(RunStates.MarkOf(nwfRetry, "NWC") == StepMark.Errors && RunStates.StepOf(nwfRetry, "NWF").Done == 2, "повтор NWF — ошибка NWC остаётся");

                var tip = RunStates.Tip(run, new List<string> { "KJ" }, true);
                Check(tip.Contains("с ошибкой 1") && tip.Contains("B — NWC занята") && tip.Contains("Изменились после выгрузки") && tip.Contains("Revit Server") &&
                      tip.Contains("Щелчок — журнал"), tip);
                Check(RunStates.Tip(null, new List<string>(), false) == null, "ничего — без подсказки");

                var dir = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var file = Path.Combine(dir, "Журналы выгрузки", RunStates.FileName);
                    RunStates.Save(file, new[] { run }, new[] { new ItemTime { Path = @"\\s\a.nwc", Seconds = 120 }, new ItemTime { Path = @"\\s\S1.nwf", Seconds = 20 } });
                    var other = new RowRun { Key = "ArmanQala › Средняя критичность", At = at };
                    RunStates.Save(file, new[] { other }, new[] { new ItemTime { Path = @"\\s\a.nwc", Seconds = 180 } });
                    var loaded = RunStates.Load(file);
                    Check(loaded.Rows.Count == 2 && RunStates.Find(loaded, s1)?.Errors.Count == 1 && RunStates.Find(loaded, mid)?.At == at,
                          "коллега записал другую строку — обе целы");
                    Check(RunStates.Estimate(new[] { @"\\s\a.nwc", @"\\s\S1.nwf", @"\\s\new.nwc" }, loaded, out var unknown) == 200 && unknown == 1,
                          "время — по последним, новая — без времени");
                    Check(RunStates.Estimate(new[] { @"\\s\new.nwc" }, loaded, out _) == null && RunStates.Load(Path.Combine(dir, "нет.json")).Rows.Count == 0,
                          "ни одной — null; нет файла — пусто");

                    // «Время» строки — прошлый запуск целиком; не запускали — сумма подзадач; лист без запуска — сумма NWC и NWF
                    Func<ExportTask, IEnumerable<string>> none = t => new[] { @"\\s\a.nwc" };
                    Check(RunStates.EstimateRow(s1, loaded, none, out var src) == 218 && src.StartsWith("прошлый запуск"), "своя строка — 218 с: " + src);
                    var s2 = high.Children[1];
                    Check(RunStates.EstimateRow(s2, loaded, none, out src) == 180 && src.Contains("без копирования"), "не запускали — по NWC: " + src);
                    Check(RunStates.EstimateRow(high, loaded, none, out src) == 398 && src == "сумма подзадач", "у строки выше — сумма подзадач: " + src);
                    Check(RunStates.EstimateRow(mid, loaded, t => new string[0], out _) == null, "ничего не известно — null");
                }
                finally
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                // файл проверок сменился с прошлой сборки NWF — спросить (08.11.2026)
                var a1 = RunStates.TestsSignature(new[] { @"\\s\a.xml" }, f => new byte[] { 1, 2 });
                var a2 = RunStates.TestsSignature(new[] { @"\\s\a.xml" }, f => new byte[] { 1, 3 });
                Check(a1 != a2 && a1 == RunStates.TestsSignature(new[] { @"\\s\a.xml" }, f => new byte[] { 1, 2 }), "подпись — по содержимому");
                Check(RunStates.TestsSignature(new[] { @"\\s\нет.xml" }, f => null) == "нет:нет.xml", "файла нет — имя");
                var st = new RunStateFile { Tests = { new NwfTests { Nwf = @"\\s\S1.nwf", Signature = a1 } } };
                Check(RunStates.TestsChanged(st, @"\\s\S1.nwf", a2) != null && RunStates.TestsChanged(st, @"\\s\S1.nwf", a1) == null &&
                      RunStates.TestsChanged(st, @"\\s\S2.nwf", a2) == null, "сменился — только у той NWF, где была другая подпись");
                Check(RunStates.EstimateText(null) == "—" && RunStates.EstimateText(20) == "≈ 1 мин" && RunStates.EstimateText(725) == "≈ 12 мин" &&
                      RunStates.EstimateText(4800) == "≈ 1 ч 20 мин" && RunStates.EstimateText(7200) == "≈ 2 ч", "текст времени");
                Check(RunStates.Elapsed(TimeSpan.FromSeconds(245)) == "4:05" && RunStates.Elapsed(TimeSpan.FromSeconds(3730)) == "1:02:10", "идёт");

                var nwc = new DateTime(2026, 11, 6, 9, 30, 0);
                var changed = RunStates.Changed(new[]
                {
                    new ModelCheck { Name = "KJ", Model = nwc.AddHours(2), Answered = true, Nwc = nwc },
                    new ModelCheck { Name = "AR", Model = nwc.AddSeconds(-30), Answered = true, Nwc = nwc },
                    new ModelCheck { Name = "OV", Model = nwc.AddSeconds(30), Answered = true, Nwc = nwc },
                    new ModelCheck { Name = "VK", Model = nwc.AddDays(1), Answered = true, Nwc = null },
                }, out var unk);
                Check(changed.SequenceEqual(new[] { "KJ" }) && !unk, "изменилась после NWC — только KJ (NWC нет — не считается, минута — запас): " + string.Join(",", changed));
                RunStates.Changed(new[] { new ModelCheck { Name = "KJ", Answered = false, Nwc = nwc } }, out unk);
                Check(unk, "сервер не ответил — «?»");
            });
            yield return Case("фоновый Revit другой версии: задание туда и обратно, «жив», история из двух Revit не затирается", () =>
            {
                var roots = Sample(out _, out _);
                var job = NwcRunPlan.Build(roots[0], common, 2026, new List<string>())[0];
                job.Model.Year = 2023;
                job.Model.NwcName = "своё";
                job.Edited = new ModelDateFilter { On = true, Mode = ModelDateFilter.After, From = new DateTime(2026, 11, 1) };
                var dir = Path.Combine(Path.GetTempPath(), "ras-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var path = NwcWorkers.JobPath(dir, "2023-1");
                    NwcWorkers.Write(path, new NwcHandoff { Id = "2023-1", Year = 2023, Title = "A › B", Jobs = { NwcWorkers.ToHandoff(job) } });
                    var back = NwcWorkers.FromHandoff(NwcWorkers.Read<NwcHandoff>(path).Jobs.Single());
                    Check(back.Model.Path == job.Model.Path && back.Model.Year == 2023 && back.Model.NwcName == "своё" && back.NwcPath == job.NwcPath &&
                          back.View == job.View && back.Edited.On && back.Edited.From.Value.Day == 1 && back.Rows.SequenceEqual(job.Rows) &&
                          NwcRunPlan.Signature(back) == NwcRunPlan.Signature(job), "задание — то же");
                    Check(NwcWorkers.Read<NwcHandoff>(Path.Combine(dir, "нет.job")) == null, "нет файла — null");

                    var now = new DateTime(2026, 11, 5, 12, 0, 0);
                    Check(NwcWorkers.IsAlive(new WorkerAlive { ProcessId = 1, At = now.AddSeconds(-5) }, now, id => true), "отметился 5 с назад — жив");
                    Check(!NwcWorkers.IsAlive(new WorkerAlive { ProcessId = 1, At = now.AddMinutes(-2) }, now, id => true), "давно — нет");
                    Check(!NwcWorkers.IsAlive(new WorkerAlive { ProcessId = 1, At = now }, now, id => false), "процесса нет — нет");

                    var history = Path.Combine(dir, "история.json");
                    NwcHistory.Merge(history, new[] { new NwcHistory.Entry { Nwc = @"\\s\a.nwc", Size = 1 } });   // фоновый Revit
                    NwcHistory.Merge(history, new[] { new NwcHistory.Entry { Nwc = @"\\s\b.nwc", Size = 2 } });   // этот
                    var h = NwcHistory.Load(history);
                    Check(h.Entries.Count == 2 && h.Last(@"\\s\a.nwc").Size == 1, "записи обоих Revit — целы");
                }
                finally
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
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
