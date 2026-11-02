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

        private static List<ExportModel> Models(int n) =>
            Enumerable.Range(1, n).Select(i => new ExportModel { Path = "0000_01_01_S6_KJ0" + i + "_01_M3.rvt" }).ToList();

        // Арман Кала → Блок 1 (6 моделей: Высокая, Средняя), Блок 2 (5); ЖК Нурлы (12)
        private static List<ExportTask> Sample(out ExportTask block1, out ExportTask high)
        {
            var project = new ExportTask { Name = "Арман Кала" };
            block1 = project.Add(new ExportTask { Name = "Блок 1", ModelFiles = Models(6) });
            high = block1.Add(new ExportTask { Name = "Высокая критичность" });
            block1.Add(new ExportTask { Name = "Средняя критичность" });
            project.Add(new ExportTask { Name = "Блок 2", ModelFiles = Models(5) });
            return new List<ExportTask> { project, new ExportTask { Name = "ЖК Нурлы", ModelFiles = Models(12) } };
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
            yield return Case("раздел и отдел из имени модели", () =>
            {
                Check(ExportNames.Section("0000_01_01_S6_KJ01_01_M3 еще текст.rvt", 5) == "KJ01", "раздел");
                Check(ExportNames.Section("0000_01.rvt", 5) == "", "нет такой части");
                Check(ExportNames.Department("KJ01") == "KJ" && ExportNames.Department("AR") == "AR" && ExportNames.Department("ОВ2") == "ОВ",
                    "отдел — буквы до цифр");
                Check(ExportNames.Department("") == "", "пусто");
            });
            yield return Case("папка — строка с метками: подставить известные, неизвестные — как есть", () =>
            {
                var values = new Dictionary<string, string> { ["Проект"] = "Арман Кала", ["Блок"] = "Блок 1", ["Отдел"] = "" };
                var folder = ExportNames.Fill(@"{Шифр}_{Проект}\{ Блок }\{Отдел}\NWC", values);
                Check(folder == @"{Шифр}_Арман Кала\Блок 1\{Отдел}\NWC", folder);
                var labels = string.Join(",", ExportNames.LabelsIn(@"{Шифр}_{Проект}\{Проект}\{Дата}"));
                Check(labels == "Шифр,Проект,Дата", labels);
            });
            yield return Case("общие настройки: по умолчанию — как на снимке окна экспортёра; коэффициент фасетизации", () =>
            {
                var s = new ExportSettings();
                Check(s.ConvertElementIds && s.Parameters == NwcParameters.All && s.SharedCoordinates && s.FacetingFactor == 1 &&
                      s.ConvertElementProperties && s.RoomAsAttribute && s.ConvertParts && s.DivideIntoLevels &&
                      s.Scope == NwcScope.View && s.ViewName == "3D_Navisworks", "включённое");
                Check(!s.ConvertUrls && !s.ConvertLights && !s.ConvertLinkedCad && !s.ConvertLinkedFiles && !s.FindMissingMaterials &&
                      !s.RoomGeometry, "выключенное");
                Check(s.RemoveRevitLinks && s.RemoveCad && s.RemovePointClouds && s.RemoveIfc, "перед выгрузкой удаляется всё внешнее");
                Check(ExportSettings.TryFaceting("0,5", out var f) && f == 0.5, "запятая");
                Check(ExportSettings.TryFaceting(" 2.5 ", out f) && f == 2.5, "точка");
                Check(!ExportSettings.TryFaceting("0", out _) && !ExportSettings.TryFaceting("-1", out _) &&
                      !ExportSettings.TryFaceting("абв", out _) && !ExportSettings.TryFaceting("", out _), "не число или не больше нуля");
            });
            yield return Case("настройки узла: своё — у узла, не заданное — от ближайшего выше; нигде — null", () =>
            {
                var roots = Sample(out var block1, out var high);
                var project = roots[0];
                project.Own.Folders = new FolderSettings { NwcFolder = @"\\srv\ArmanQala\NWC\{Блок}" };
                block1.Own.Names = new NameSettings { Parts = 6 };
                var (folders, from) = ExportNodes.Inherited(high, s => s.Folders);
                Check(from == project && folders.NwcFolder.EndsWith("{Блок}"), "папки — от проекта");
                Check(ExportNodes.Effective(high, s => s.Names).Parts == 6, "имя — от блока");
                Check(ExportNodes.Effective(project, s => s.Names) == null && ExportNodes.Inherited(project, s => s.Names).From == null,
                    "у проекта своего нет, выше никого");
                high.Own.Folders = new FolderSettings { NwcFolder = "своя" };
                Check(ExportNodes.Effective(high, s => s.Folders).NwcFolder == "своя", "своё сильнее");
                Check(ExportNodes.Inherited(high, s => s.Folders).From == project, "унаследованное — по-прежнему от проекта");
            });
            yield return Case("{Блок}: у блока — он сам, у критичности — её блок, у задачи — первый блок", () =>
            {
                var roots = Sample(out var block1, out var high);
                Check(ExportNodes.BlockName(block1) == "Блок 1" && ExportNodes.BlockName(high) == "Блок 1", "блок и критичность");
                Check(ExportNodes.BlockName(roots[0]) == "Блок 1", "задача — пример по первому блоку");
                Check(ExportNodes.BlockName(roots[1]) == null, "блоков нет");
                Check(ExportNodes.Level(high) == 2 && ExportNodes.Level(roots[0]) == 0, "уровни");
            });
            yield return Case("модели узла: имя NWC руками или из имени; Revit Server; проверка имён и папок", () =>
            {
                var names = new NameSettings();
                var m = new ExportModel { Path = @"\\srv\Проект\0000_01_01_S6_KJ01_01_M3 еще текст.rvt" };
                Check(ExportNodes.NwcNameOf(m, names) == "0000_01_01_S6_KJ01_01_M3", "из имени");
                m.NwcName = " КЖ_свой ";
                Check(ExportNodes.NwcNameOf(m, names) == "КЖ_свой", "руками");
                var rsn = new ExportModel { Path = "RSN://10.10.1.5/Проекты/0000_01_01_S6_AR01_01_M3.rvt" };
                Check(ExportNodes.NwcNameOf(rsn, names) == "0000_01_01_S6_AR01_01_M3", "Revit Server: " + ExportNodes.NwcNameOf(rsn, names));
                Check(ExportNodes.IsServerPath(rsn.Path) && !ExportNodes.IsServerPath(@"\\srv\m.rvt") && !ExportNodes.IsServerPath("RSN://srv/папка"),
                    "путь Revit Server");
                Check(ExportNodes.CheckNwcNames(new[] { "a", "b" }) == null, "верные имена");
                Check(ExportNodes.CheckNwcNames(new[] { "a", "A" }) != null, "повтор без учёта регистра");
                Check(ExportNodes.CheckNwcNames(new[] { "a:b" }) != null && ExportNodes.CheckNwcNames(new[] { " " }) != null, "знаки и пусто");
                Check(ExportNodes.CheckFolder(@"\\srv\{Дата}\Высокая критичность\XML\{ блок }", "Папка") == null, "метки известны");
                var bad = ExportNodes.CheckFolder(@"\\srv\{Проект}", "Папка отчётов");
                Check(bad != null && bad.Contains("{Проект}"), "неизвестная метка: " + bad);
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
