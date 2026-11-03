using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// Файл настроек «Задач выгрузки» — поделиться с коллегой (разработчик, 02.11.2026: «импорт и экспорт настроек любого
    /// уровня, чтобы специалисты могли делиться»): общие настройки, проект с моделями и задачами или задача. JSON, «Kind» —
    /// что внутри. Версия 2 (03.11.2026): модели — у проекта, у задачи — её настройки; файлы версии 1 читаются (модели прежних
    /// задач переходят в проект).
    /// </summary>
    [DataContract]
    public sealed class ExportFile
    {
        public const string Common = "Общие настройки";
        public const string Project = "Проект";
        public const string Task = "Задача";

        [DataMember(Order = 1)] public string Kind { get; set; }
        [DataMember(Order = 2)] public int Version { get; set; } = 2;
        [DataMember(Order = 3, EmitDefaultValue = false)] public ExportSettings Settings { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public ProjectFile ProjectData { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public TaskFile TaskData { get; set; }
    }

    [DataContract]
    public sealed class ProjectFile
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public ProjectSettings Settings { get; set; }
        [DataMember(Order = 3)] public List<TaskFile> Tasks { get; set; } = new List<TaskFile>();
        /// <summary>Кто и когда записал проект в общее хранилище — для «изменил Иванов в 14:20» (ExportStore).</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public string ChangedBy { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public DateTime? Changed { get; set; }
        /// <summary>Порядковый номер проекта в общем списке (ExportStore).</summary>
        [DataMember(Order = 6, EmitDefaultValue = false)] public int Order { get; set; }
        /// <summary>Модели проекта (версия 2).</summary>
        [DataMember(Order = 7, EmitDefaultValue = false)] public List<ExportModel> Models { get; set; } = new List<ExportModel>();
    }

    [DataContract]
    public sealed class TaskFile
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        /// <summary>Только версия 1: модели задачи-блока — при чтении переходят в проект.</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public List<ExportModel> Models { get; set; }
        /// <summary>Только версия 1: свои файлы проверок — при чтении переходят в настройки задачи.</summary>
        [DataMember(Order = 3, EmitDefaultValue = false)] public List<string> TestsFiles { get; set; }
        /// <summary>Настройки задачи (версия 2).</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public TaskSettings Options { get; set; }
    }

    /// <summary>Записать и прочитать файл настроек; из строк дерева — в файл и обратно.</summary>
    public static class ExportFiles
    {
        public const string Filter = "Настройки «Задач выгрузки» (*.json)|*.json";

        public static void Save(string path, ExportFile file)
        {
            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                new DataContractJsonSerializer(typeof(ExportFile)).WriteObject(writer, file);
        }

        /// <summary>Прочитать; не тот файл — InvalidDataException с причиной по-русски.</summary>
        public static ExportFile Load(string path)
        {
            ExportFile file;
            try
            {
                using (var stream = File.OpenRead(path))
                    file = (ExportFile)new DataContractJsonSerializer(typeof(ExportFile)).ReadObject(stream);
            }
            catch (SerializationException ex)
            {
                throw new InvalidDataException("Это не файл настроек «Задач выгрузки».", ex);
            }
            if (file == null || (file.Kind != ExportFile.Common && file.Kind != ExportFile.Project && file.Kind != ExportFile.Task) ||
                file.Kind == ExportFile.Common && file.Settings == null ||
                file.Kind == ExportFile.Project && file.ProjectData == null ||
                file.Kind == ExportFile.Task && file.TaskData == null)
                throw new InvalidDataException("Это не файл настроек «Задач выгрузки».");
            Normalize(file);
            return file;
        }

        /// <summary>
        /// Прочитанное без значений — по умолчанию: JSON-чтение не запускает конструкторы, а в файле от прежней версии новых полей
        /// нет.
        /// </summary>
        private static void Normalize(ExportFile file)
        {
            if (file.Settings != null)
            {
                var d = new ExportSettings();
                var s = file.Settings;
                if (s.Nwc == null) s.Nwc = d.Nwc;
                if (string.IsNullOrEmpty(s.Nwc.ViewName)) s.Nwc.ViewName = d.Nwc.ViewName;
                if (s.Nwc.FacetingFactor <= 0) s.Nwc.FacetingFactor = d.Nwc.FacetingFactor;
                if (s.NameParts <= 0) s.NameParts = d.NameParts;
                if (string.IsNullOrEmpty(s.BlockPrefix)) s.BlockPrefix = d.BlockPrefix;
                if (string.IsNullOrEmpty(s.DateFormat)) s.DateFormat = d.DateFormat;
                if (s.Departments == null) s.Departments = new List<DepartmentTemplate>();
                if (s.HideNames == null) s.HideNames = new List<string>();
            }
            if (file.ProjectData != null)
            {
                var pd = file.ProjectData;
                pd.Settings = Normalize(pd.Settings);
                if (pd.Tasks == null) pd.Tasks = new List<TaskFile>();
                pd.Models = Clean(pd.Models);
                // версия 1: задачи были блоками со своими моделями — модели переходят в проект
                foreach (var t in pd.Tasks)
                    foreach (var m in Clean(t.Models))
                        if (!pd.Models.Any(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase))) pd.Models.Add(m);
                foreach (var t in pd.Tasks) Normalize(t);
            }
            if (file.TaskData != null) Normalize(file.TaskData);
        }

        private static List<ExportModel> Clean(List<ExportModel> models) =>
            (models ?? new List<ExportModel>()).Where(m => m != null && !string.IsNullOrWhiteSpace(m.Path)).ToList();

        private static ProjectSettings Normalize(ProjectSettings p)
        {
            var d = new ProjectSettings();
            if (p == null) return d;
            p.Folder = p.Folder ?? "";
            p.ServerFolder = p.ServerFolder ?? "";
            p.NwcFolder = p.NwcFolder ?? d.NwcFolder;
            return p;
        }

        private static void Normalize(TaskFile t)
        {
            var d = new TaskSettings();
            t.Name = t.Name ?? "";
            var o = t.Options ?? new TaskSettings { TestsFiles = t.TestsFiles ?? new List<string>() };
            o.TestsFiles = o.TestsFiles ?? new List<string>();
            o.NwfName = string.IsNullOrWhiteSpace(o.NwfName) ? d.NwfName : o.NwfName;
            o.NwfFolder = o.NwfFolder ?? d.NwfFolder;
            o.ReportFolder = o.ReportFolder ?? d.ReportFolder;
            o.SkippedBlocks = o.SkippedBlocks ?? new List<string>();
            if (t.Options == null) { o.Xml = true; o.Html = true; }
            t.Options = o;
            t.Models = null;
            t.TestsFiles = null;
        }

        public static ExportFile FromCommon(ExportSettings settings) =>
            new ExportFile { Kind = ExportFile.Common, Settings = settings.Clone() };

        /// <summary>Проект с моделями и задачами; settings и models — как сейчас в окне.</summary>
        public static ExportFile FromProject(string name, ProjectSettings settings, IEnumerable<ExportModel> models, IEnumerable<ExportTask> tasks) =>
            new ExportFile
            {
                Kind = ExportFile.Project,
                ProjectData = new ProjectFile
                {
                    Name = name, Settings = Copy(settings), Models = models.Select(CopyModel).ToList(), Tasks = tasks.Select(ToFile).ToList(),
                },
            };

        public static ExportFile FromTask(ExportTask task) => new ExportFile { Kind = ExportFile.Task, TaskData = ToFile(task) };

        public static TaskFile ToFile(ExportTask task) => new TaskFile { Name = task.Name, Options = task.Options.Clone() };

        /// <summary>Новый проект из файла; занятое название — «ArmanQala (2)».</summary>
        public static ExportTask ToProject(ProjectFile file, IEnumerable<string> taken)
        {
            var project = new ExportTask { Name = UniqueName(file.Name, taken), ModelFiles = file.Models.Select(CopyModel).ToList() };
            var s = Copy(file.Settings);
            var p = project.Project;
            p.Folder = s.Folder;
            p.ServerFolder = s.ServerFolder;
            p.NwcFolder = s.NwcFolder;
            p.ViewName = s.ViewName;
            foreach (var t in file.Tasks) project.Add(ToTask(t, project.Children.Select(c => c.Name)));
            return project;
        }

        /// <summary>Новая задача из файла (настройки, без моделей — они у проекта); занятое в проекте название — «… (2)».</summary>
        public static ExportTask ToTask(TaskFile file, IEnumerable<string> taken) =>
            new ExportTask { Name = UniqueName(file.Name, taken), Options = file.Options.Clone() };

        /// <summary>Название, которого ещё нет: «X», «X (2)», «X (3)»…</summary>
        public static string UniqueName(string name, IEnumerable<string> taken)
        {
            var n = string.IsNullOrWhiteSpace(name) ? "Без названия" : name.Trim();
            var set = new HashSet<string>(taken.Select(t => (t ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(n)) return n;
            for (var i = 2; ; i++)
                if (!set.Contains(n + " (" + i + ")")) return n + " (" + i + ")";
        }

        private static ExportModel CopyModel(ExportModel m) => new ExportModel { Path = m.Path, NwcName = m.NwcName, Block = m.Block };

        private static ProjectSettings Copy(ProjectSettings s)
        {
            s = s ?? new ProjectSettings();
            return new ProjectSettings { Folder = s.Folder, ServerFolder = s.ServerFolder, NwcFolder = s.NwcFolder, ViewName = s.ViewName };
        }
    }
}
