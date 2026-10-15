using System;
using System.Collections.Generic;
using System.Linq;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: блоки проекта, заданные вручную (ProjectBlocks).</summary>
    internal static class ClashBlockTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Root = @"\\10.10.1.77\Shared\Коллизии\Арман Кала";

        private static readonly (string Name, string Folder)[] Blocks =
        {
            ("S1", "S1"),
            ("Секция 1", @"Корпус А\Секция 1"),
        };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("папка блока — относительно папки проекта; вне проекта — нет", () =>
            {
                Check(ProjectBlocks.Relative(Root, "S1") == "S1", "короткая");
                Check(ProjectBlocks.Relative(Root, Root + @"\Корпус А\Секция 1\") == @"Корпус А\Секция 1", "полный путь внутри");
                Check(ProjectBlocks.Relative(Root, "/Корпус А/Секция 1/") == @"Корпус А\Секция 1", "косые черты");
                Check(ProjectBlocks.Relative(Root, @"\\10.10.1.77\Shared\Другое\S1") == null, "вне проекта");
                Check(ProjectBlocks.Relative(Root, Root) == null, "сама папка проекта");
                Check(ProjectBlocks.Relative(Root, @"..\S1") == null, "«..»");
            });
            yield return Case("отчёт — в блоке, чья папка его содержит (самая глубокая)", () =>
            {
                Check(ProjectBlocks.BlockOf(Blocks, Root, Root + @"\S1\14.10.2026\Высокая критичность\XML\АР.xml") == "S1", "S1");
                Check(ProjectBlocks.BlockOf(Blocks, Root, Root + @"\корпус а\СЕКЦИЯ 1\08.10.2026\XML\КЖ.xml") == "Секция 1", "регистр");
                Check(ProjectBlocks.BlockOf(Blocks, Root, Root + @"\S10\14.10.2026\XML\АР.xml") == null, "S10 — не S1");
                Check(ProjectBlocks.BlockOf(Blocks, Root, Root + @"\АР.xml") == null, "прямо в папке проекта");
                var nested = new[] { ("Корпус А", "Корпус А"), ("Секция 1", @"Корпус А\Секция 1") };
                Check(ProjectBlocks.BlockOf(nested, Root, Root + @"\Корпус А\Секция 1\XML\АР.xml") == "Секция 1", "самая глубокая");
            });
            yield return Case("отчёт вне блоков — какую папку сделать блоком", () =>
            {
                var folders = Blocks.Select(b => b.Folder).ToList();
                Check(ProjectBlocks.Unassigned(folders, Root, Root + @"\S10\14.10.2026\XML\АР.xml") == "S10", "S10");
                Check(ProjectBlocks.Unassigned(folders, Root, Root + @"\Корпус А\Секция 3\XML\АР.xml") == @"Корпус А\Секция 3", "соседняя секция");
                Check(ProjectBlocks.Unassigned(folders, Root, Root + @"\АР.xml") == "", "прямо в папке проекта");
            });
            yield return Case("подсказка — папки, похожие на блоки, по порядку; прочие — нет", () =>
            {
                var s = ProjectBlocks.Suggest(new[] { "S10", "S01", "XML", "Блок 2", "14.10.2026", "S2", "С3" });
                var names = s.Select(x => x.Name).ToList();
                Check(names.Count == 5 && names.Contains("Блок 2") && names.IndexOf("S1") < names.IndexOf("S2") && names.IndexOf("S2") < names.IndexOf("S3")
                      && names.IndexOf("S3") < names.IndexOf("S10"), "по номеру: " + string.Join(",", s.Select(x => x.Name + "=" + x.Folder)));
                Check(s.First().Folder == "S01", "папка — как на диске");
                Check(s.All(x => x.Name != null && x.Folder != "XML" && x.Folder != "14.10.2026"), "не блоки — не подсказываются");
            });
            yield return Case("список блоков: пустое, повторы, вложенные папки — нельзя", () =>
            {
                Check(ProjectBlocks.Validate(Blocks) == null, "правильный");
                Check(ProjectBlocks.Validate(new[] { ("", "S1") })?.Contains("нет названия") == true, "без названия");
                Check(ProjectBlocks.Validate(new[] { ("S1", "") })?.Contains("не указана папка") == true, "без папки");
                Check(ProjectBlocks.Validate(new[] { ("S1", "S1"), ("s1", "S01") })?.Contains("дважды") == true, "название дважды");
                Check(ProjectBlocks.Validate(new[] { ("S1", "S1"), ("Первый", "s1") })?.Contains("одна папка") == true, "папка дважды");
                Check(ProjectBlocks.Validate(new[] { ("А", "Корпус А"), ("Секция", @"Корпус А\Секция 1") })?.Contains("внутри папки другого") == true, "вложенные");
            });
        }
    }
}
