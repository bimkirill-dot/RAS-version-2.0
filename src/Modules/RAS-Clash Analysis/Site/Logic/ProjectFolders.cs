using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Папка нового (или изменённого) проекта против папок других проектов (DECISIONS.md модуля, «Удаление проекта»):
    /// папка внутри папки другого проекта или папка, внутри которой уже есть проект, — отчёты считались бы дважды.
    /// Без базы — проверяется тестами.
    /// </summary>
    public static class ProjectFolders
    {
        /// <summary>Путь без кавычек по краям и без косой черты в конце; «/» — как «\».</summary>
        public static string Clean(string folder) =>
            (folder ?? "").Trim().Trim('"').Trim().Replace('/', '\\').TrimEnd('\\');

        /// <summary>
        /// Почему папку нельзя взять для проекта (из-за других проектов): текст для человека; можно — null.
        /// others — другие проекты (сам изменяемый проект — не включать).
        /// </summary>
        public static string Conflict(string folder, IEnumerable<(string Name, string Folder)> others)
        {
            var mine = Clean(folder);
            if (mine.Length == 0) return null;
            foreach (var p in others)
            {
                var their = Clean(p.Folder);
                if (their.Length == 0) continue;
                if (string.Equals(mine, their, StringComparison.OrdinalIgnoreCase))
                    return $"Эта папка уже добавлена — проект «{p.Name}».";
                if (Inside(mine, their))
                    return $"Эта папка — часть проекта «{p.Name}» ({their}). Его отчёты уже загружаются.";
                if (Inside(their, mine))
                    return $"Внутри этой папки уже есть проект «{p.Name}» ({their}). Его отчёты считались бы дважды.";
            }
            return null;
        }

        /// <summary>Папка inner лежит внутри outer (на любой глубине); обе — после Clean.</summary>
        private static bool Inside(string inner, string outer) =>
            inner.Length > outer.Length + 1
            && inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
