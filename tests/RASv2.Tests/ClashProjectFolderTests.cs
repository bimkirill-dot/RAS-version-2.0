using System;
using System.Collections.Generic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: папка нового проекта против папок других проектов (ProjectFolders).</summary>
    internal static class ClashProjectFolderTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private const string Zamon = @"C:\Users\user\Desktop\2025_Zamon (Яшнабад) г.Ташкент";

        private static readonly (string Name, string Folder)[] Others =
        {
            ("2025_Zamon (Яшнабад) г.Ташкент", Zamon),
            ("Арман Кала", @"\\10.10.1.77\Shared\Коллизии\Арман Кала\"),
        };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("папка даты внутри проекта — нельзя (случай «08.10.2026»)", () =>
            {
                var why = ProjectFolders.Conflict(Zamon + @"\08.10.2026", Others);
                Check(why != null && why.Contains("часть проекта «2025_Zamon"), "внутри: " + why);
            });
            yield return Case("папка, внутри которой уже есть проект, — нельзя", () =>
            {
                var why = ProjectFolders.Conflict(@"C:\Users\user\Desktop", Others);
                Check(why != null && why.Contains("Внутри этой папки"), "снаружи: " + why);
                why = ProjectFolders.Conflict(@"\\10.10.1.77\Shared\Коллизии", Others);
                Check(why != null && why.Contains("«Арман Кала»"), "снаружи, сетевая: " + why);
            });
            yield return Case("та же папка — с кавычками, другим регистром, косой чертой в конце", () =>
            {
                var why = ProjectFolders.Conflict("\"" + Zamon.ToUpperInvariant() + "\\\"", Others);
                Check(why != null && why.Contains("уже добавлена"), "та же: " + why);
                why = ProjectFolders.Conflict(@"//10.10.1.77/Shared/Коллизии/Арман Кала", Others);
                Check(why != null && why.Contains("уже добавлена"), "та же, «/»: " + why);
            });
            yield return Case("соседняя папка с похожим началом имени — можно", () =>
            {
                Check(ProjectFolders.Conflict(Zamon + " (2)", Others) == null, "«… (2)» — не внутри");
                Check(ProjectFolders.Conflict(@"\\10.10.1.77\Shared\Коллизии\Арман Кала 2", Others) == null, "«Арман Кала 2»");
                Check(ProjectFolders.Conflict(@"D:\Другой проект", Others) == null, "другой диск");
            });
            yield return Case("без других проектов и с пустой папкой — нет возражений", () =>
            {
                Check(ProjectFolders.Conflict(Zamon, Array.Empty<(string, string)>()) == null, "нет других");
                Check(ProjectFolders.Conflict("  ", Others) == null, "пустая папка — проверяется отдельно");
            });
        }
    }
}
