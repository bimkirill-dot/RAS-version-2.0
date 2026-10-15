using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Блоки проекта, заданные вручную (DECISIONS.md модуля, «Блоки — вручную»): название и папка внутри папки проекта.
    /// Отчёт относится к блоку, если лежит в его папке; вне всех блоков — не загружается, а его папка — в предупреждении.
    /// Распознавание по названиям (S1, «Блок 2») — только подсказка при заполнении списка. Без базы — проверяется тестами.
    /// </summary>
    public static class ProjectBlocks
    {
        /// <summary>
        /// Папка блока относительно папки проекта: «S1», «Корпус А\Секция 1» (без косых черт по краям, «/» — как «\»).
        /// Полный путь внутри папки проекта — тоже; вне её, пустая или сама папка проекта — null.
        /// </summary>
        public static string Relative(string projectFolder, string folder)
        {
            var root = ProjectFolders.Clean(projectFolder);
            var f = ProjectFolders.Clean(folder);
            if (f.Length == 0) return null;
            // полный путь — с буквой диска или сетевой (\\сервер\…); «\Корпус А» — относительный, с лишней чертой
            if ((f.Length >= 2 && f[1] == ':') || f.StartsWith("\\\\", StringComparison.Ordinal))
            {
                if (!f.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                f = f.Substring(root.Length + 1);
            }
            f = f.Trim('\\');
            if (f.Length == 0 || f.Split('\\').Any(s => s.Trim().Length == 0 || s == "." || s == "..")) return null;
            return f;
        }

        /// <summary>Блок отчёта: чья папка содержит файл (самая глубокая); ни чья — null.</summary>
        public static string BlockOf(IEnumerable<(string Name, string Folder)> blocks, string projectFolder, string file)
        {
            var dir = RelativeDir(projectFolder, file);
            if (dir == null) return null;
            string best = null;
            var depth = -1;
            foreach (var b in blocks)
            {
                var f = ProjectFolders.Clean(b.Folder);
                if (dir.Equals(f, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    var d = f.Split('\\').Length;
                    if (d > depth) { best = b.Name; depth = d; }
                }
            }
            return best;
        }

        /// <summary>
        /// Папка, которую надо сделать блоком, для отчёта вне всех блоков: самая короткая часть его пути, с которой не
        /// начинается ни одна папка блока («S10»; при блоках «Корпус А\Секция 1» — «Корпус А\Секция 3»). Отчёт прямо в
        /// папке проекта — «» (папка проекта).
        /// </summary>
        public static string Unassigned(IEnumerable<string> blockFolders, string projectFolder, string file)
        {
            var dir = RelativeDir(projectFolder, file);
            if (dir == null || dir.Length == 0) return "";
            var folders = blockFolders.Select(ProjectFolders.Clean).ToList();
            var parts = dir.Split('\\');
            for (var k = 1; k <= parts.Length; k++)
            {
                var prefix = string.Join("\\", parts.Take(k));
                if (!folders.Any(f => f.Equals(prefix, StringComparison.OrdinalIgnoreCase) || f.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)))
                    return prefix;
            }
            return dir;
        }

        /// <summary>
        /// Подсказка: вложенные папки папки проекта, похожие на блоки («S1», «S08», «Блок 2», «Секция 3»), — название (как
        /// распознано: «S8», «Блок 2») и папка; по порядку блоков.
        /// </summary>
        public static List<(string Name, string Folder)> Suggest(IEnumerable<string> subfolders) =>
            subfolders
                .Select(f => (Name: ReportPath.Block(f), Folder: f))
                .Where(x => x.Name != null)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(x => ReportPath.BlockOrder(x.Name)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>
        /// Что не так со списком блоков: пустое название или папка, папка вне проекта, повтор названия или папки, папка
        /// внутри папки другого блока; всё в порядке — null. Папки — относительно папки проекта (Relative).
        /// </summary>
        public static string Validate(IReadOnlyList<(string Name, string Folder)> blocks)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                var (name, folder) = blocks[i];
                if (string.IsNullOrWhiteSpace(name)) return "У блока нет названия.";
                if (string.IsNullOrWhiteSpace(folder)) return $"У блока «{name.Trim()}» не указана папка внутри папки проекта.";
                for (var j = 0; j < i; j++)
                {
                    var (otherName, otherFolder) = blocks[j];
                    if (string.Equals(name.Trim(), otherName.Trim(), StringComparison.CurrentCultureIgnoreCase))
                        return $"Блок «{name.Trim()}» указан дважды.";
                    var a = ProjectFolders.Clean(folder);
                    var b = ProjectFolders.Clean(otherFolder);
                    if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
                        return $"У блоков «{otherName.Trim()}» и «{name.Trim()}» одна папка.";
                    if (a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
                        return $"Папка одного блока внутри папки другого: «{otherName.Trim()}» и «{name.Trim()}».";
                }
            }
            return null;
        }

        /// <summary>Папка файла относительно папки проекта («S1\14.10.2026\XML»); прямо в папке проекта — «»; вне её — null.</summary>
        private static string RelativeDir(string projectFolder, string file)
        {
            var root = ProjectFolders.Clean(projectFolder);
            var dir = ProjectFolders.Clean(Path.GetDirectoryName(ProjectFolders.Clean(file)) ?? "");
            if (dir.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
            return dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? dir.Substring(root.Length + 1) : null;
        }
    }
}
