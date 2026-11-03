using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Чем кончилась запись проекта.</summary>
    public enum SaveResult { Saved, Conflict }

    /// <summary>
    /// Общее хранилище «Задач выгрузки» на сервере (разработчик, 02.11.2026: «чтобы все специалисты видели все задачи»):
    /// «Общие настройки.json» и «Проекты\&lt;ключ&gt;.json» — по файлу на проект с задачами, в формате экспорта (ExportFiles).
    /// «Сохранить» пишет сразу; правку проекта, изменённого коллегой после открытия, не затирает молча (SaveResult.Conflict);
    /// перед записью — копия прежнего файла (.prev). Без Revit API — проверяется тестами на временной папке.
    /// </summary>
    public sealed class ExportStore
    {
        private readonly string _dir;
        private readonly string _user;

        /// <param name="dir">Папка хранилища: …\Data\RAS-Clash Analysis\Задачи выгрузки.</param>
        /// <param name="user">Кто правит — пишется в файл проекта («изменил Иванов в 14:20»).</param>
        public ExportStore(string dir, string user)
        {
            _dir = dir;
            _user = user;
        }

        public string CommonPath => Path.Combine(_dir, "Общие настройки.json");
        public string ProjectsDir => Path.Combine(_dir, "Проекты");

        private string ProjectPath(ExportTask project) => Path.Combine(ProjectsDir, project.StoreId + ".json");

        /// <summary>Общие настройки; файла ещё нет — по умолчанию.</summary>
        public ExportSettings LoadCommon() =>
            File.Exists(CommonPath) ? ExportFiles.Load(CommonPath).Settings ?? new ExportSettings() : new ExportSettings();

        public void SaveCommon(ExportSettings settings)
        {
            Directory.CreateDirectory(_dir);
            Write(CommonPath, ExportFiles.FromCommon(settings));
        }

        /// <summary>Все проекты — по порядковому номеру (без номера — в конце, по названию); файл, который не читается, пропускается (в broken — его имя).</summary>
        public List<ExportTask> LoadProjects(List<string> broken = null)
        {
            var list = new List<ExportTask>();
            // «папки нет» на недоступном сервере выглядело бы как «проектов нет»: создание папки там падает — окно скажет
            // «сервер недоступен», а не покажет пустой список
            Directory.CreateDirectory(ProjectsDir);
            foreach (var path in Directory.GetFiles(ProjectsDir, "*.json"))
            {
                try
                {
                    list.Add(LoadProject(path));
                }
                catch (InvalidDataException)
                {
                    broken?.Add(Path.GetFileName(path));
                }
            }
            return list.OrderBy(p => p.StoreOrder > 0 ? p.StoreOrder : int.MaxValue)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private ExportTask LoadProject(string path)
        {
            var file = ExportFiles.Load(path);
            if (file.Kind != ExportFile.Project) throw new InvalidDataException("Не проект.");
            var project = ExportFiles.ToProject(file.ProjectData, new string[0]);
            project.StoreId = Path.GetFileNameWithoutExtension(path);
            project.StoreStamp = File.GetLastWriteTimeUtc(path);
            project.StoreOrder = file.ProjectData.Order;
            return project;
        }

        /// <summary>Проект заново с сервера (после «открыть заново»); файла уже нет — null.</summary>
        public ExportTask Reload(ExportTask project) =>
            project.StoreId != null && File.Exists(ProjectPath(project)) ? LoadProject(ProjectPath(project)) : null;

        /// <summary>
        /// Записать проект. Файл изменился после того, как проект прочитан (коллега сохранил своё), и force нет — не пишет:
        /// Conflict, кто и когда — в changedBy, changedAt.
        /// </summary>
        public SaveResult SaveProject(ExportTask project, bool force, out string changedBy, out DateTime changedAt)
        {
            changedBy = null;
            changedAt = default(DateTime);
            Directory.CreateDirectory(ProjectsDir);
            if (project.StoreId == null) project.StoreId = Guid.NewGuid().ToString("N");
            var path = ProjectPath(project);
            if (!force && File.Exists(path) && File.GetLastWriteTimeUtc(path) != project.StoreStamp)
            {
                try
                {
                    var data = ExportFiles.Load(path).ProjectData;
                    changedBy = data?.ChangedBy;
                    changedAt = data?.Changed ?? File.GetLastWriteTime(path);
                }
                catch (InvalidDataException)
                {
                    changedAt = File.GetLastWriteTime(path);
                }
                return SaveResult.Conflict;
            }
            var file = ExportFiles.FromProject(project.Name, project.Project, project.ModelFiles, project.Children);
            file.ProjectData.Order = project.StoreOrder;
            file.ProjectData.ChangedBy = _user;
            file.ProjectData.Changed = DateTime.Now;
            Write(path, file);
            project.StoreStamp = File.GetLastWriteTimeUtc(path);
            return SaveResult.Saved;
        }

        /// <summary>Удалить проект с сервера; прежний файл остаётся копией (.prev) — вернуть руками.</summary>
        public void DeleteProject(ExportTask project)
        {
            if (project.StoreId == null) return;
            var path = ProjectPath(project);
            if (!File.Exists(path)) return;
            File.Copy(path, path + ".prev", true);
            File.Delete(path);
        }

        /// <summary>Запись через временный файл: обрыв сети на середине не оставит половину файла; прежний — в .prev.</summary>
        private static void Write(string path, ExportFile file)
        {
            var tmp = path + ".tmp";
            ExportFiles.Save(tmp, file);
            if (File.Exists(path)) File.Replace(tmp, path, path + ".prev");
            else File.Move(tmp, path);
        }
    }
}
