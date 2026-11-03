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
    /// уровня, чтобы специалисты могли делиться»): общие настройки, задача верхнего уровня (так же хранится на сервере) или
    /// подзадача с вложенными. JSON, «Kind» — что внутри. Версия 3 (03.11.2026): дерево задач с настройками строк (NodeSettings);
    /// файлы версий 1 и 2 (проект → задачи) читаются: проект становится задачей, его задачи — подзадачами.
    /// </summary>
    [DataContract]
    public sealed class ExportFile
    {
        public const string Common = "Общие настройки";
        /// <summary>Задача верхнего уровня (до версии 3 — «проект»).</summary>
        public const string Project = "Проект";
        /// <summary>Подзадача с вложенными (до версии 3 — задача проекта).</summary>
        public const string Task = "Задача";

        [DataMember(Order = 1)] public string Kind { get; set; }
        [DataMember(Order = 2)] public int Version { get; set; } = 3;
        [DataMember(Order = 3, EmitDefaultValue = false)] public ExportSettings Settings { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public ProjectFile ProjectData { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public TaskFile TaskData { get; set; }
    }

    /// <summary>Задача верхнего уровня — с данными общего хранилища.</summary>
    [DataContract]
    public sealed class ProjectFile
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        /// <summary>Только версии 1–2: настройки проекта.</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public LegacyProject Settings { get; set; }
        [DataMember(Order = 3)] public List<TaskFile> Tasks { get; set; } = new List<TaskFile>();
        /// <summary>Кто и когда записал задачу в общее хранилище — для «изменил Иванов в 14:20» (ExportStore).</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public string ChangedBy { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public DateTime? Changed { get; set; }
        /// <summary>Порядковый номер задачи в общем списке (ExportStore).</summary>
        [DataMember(Order = 6, EmitDefaultValue = false)] public int Order { get; set; }
        /// <summary>Только версия 2: модели проекта.</summary>
        [DataMember(Order = 7, EmitDefaultValue = false)] public List<ExportModel> Models { get; set; }
        /// <summary>Настройки строки (версия 3).</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)] public NodeSettings Node { get; set; }
    }

    /// <summary>Подзадача с вложенными.</summary>
    [DataContract]
    public sealed class TaskFile
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        /// <summary>Только версия 1: модели задачи-блока — при чтении переходят в проект.</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public List<ExportModel> Models { get; set; }
        /// <summary>Только версия 1: свои файлы проверок.</summary>
        [DataMember(Order = 3, EmitDefaultValue = false)] public List<string> TestsFiles { get; set; }
        /// <summary>Только версия 2: настройки задачи.</summary>
        [DataMember(Order = 4, EmitDefaultValue = false)] public LegacyTask Options { get; set; }
        /// <summary>Настройки строки (версия 3).</summary>
        [DataMember(Order = 5, EmitDefaultValue = false)] public NodeSettings Node { get; set; }
        /// <summary>Вложенные подзадачи (версия 3).</summary>
        [DataMember(Order = 6, EmitDefaultValue = false)] public List<TaskFile> Tasks { get; set; }
    }

    /// <summary>Настройки проекта версий 1–2 — только для чтения прежних файлов.</summary>
    [DataContract(Name = "ProjectSettings")]
    public sealed class LegacyProject
    {
        [DataMember(EmitDefaultValue = false)] public string Folder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ServerFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string NwcFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ViewName { get; set; }
        [DataMember(EmitDefaultValue = false)] public string NwfFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ReportFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<string> TestsFiles { get; set; }
    }

    /// <summary>Настройки задачи версии 2 — только для чтения прежних файлов.</summary>
    [DataContract(Name = "TaskSettings")]
    public sealed class LegacyTask
    {
        [DataMember(EmitDefaultValue = false)] public List<string> TestsFiles { get; set; }
        [DataMember(EmitDefaultValue = false)] public string NwfName { get; set; }
        [DataMember(EmitDefaultValue = false)] public string NwfFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ReportFolder { get; set; }
        [DataMember] public bool Xml { get; set; }
        [DataMember] public bool Html { get; set; }
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
            if (file.Settings != null) Normalize(file.Settings);
            return file;
        }

        /// <summary>
        /// Общие настройки без значений — по умолчанию: JSON-чтение не запускает конструкторы, а в файле от прежней версии новых
        /// полей нет.
        /// </summary>
        private static void Normalize(ExportSettings s)
        {
            var d = new ExportSettings();
            if (s.Nwc == null) s.Nwc = d.Nwc;
            if (string.IsNullOrEmpty(s.Nwc.ViewName)) s.Nwc.ViewName = d.Nwc.ViewName;
            if (s.Nwc.FacetingFactor <= 0) s.Nwc.FacetingFactor = d.Nwc.FacetingFactor;
            if (string.IsNullOrEmpty(s.BlockPrefix)) s.BlockPrefix = d.BlockPrefix;
            if (string.IsNullOrEmpty(s.DateFormat)) s.DateFormat = d.DateFormat;
            if (s.Departments == null) s.Departments = new List<DepartmentTemplate>();
        }

        public static ExportFile FromCommon(ExportSettings settings) =>
            new ExportFile { Kind = ExportFile.Common, Settings = settings.Clone() };

        /// <summary>Задача верхнего уровня с подзадачами — для хранилища и «Экспорт…» у неё.</summary>
        public static ExportFile FromTop(ExportTask top) =>
            new ExportFile
            {
                Kind = ExportFile.Project,
                ProjectData = new ProjectFile { Name = top.Name, Node = top.Settings.Clone(), Tasks = top.Children.Select(ToFile).ToList() },
            };

        /// <summary>Подзадача с вложенными — для «Экспорт…» у неё.</summary>
        public static ExportFile FromTask(ExportTask task) => new ExportFile { Kind = ExportFile.Task, TaskData = ToFile(task) };

        public static TaskFile ToFile(ExportTask task) =>
            new TaskFile { Name = task.Name, Node = task.Settings.Clone(), Tasks = task.Children.Select(ToFile).ToList() };

        /// <summary>Задача верхнего уровня из файла; занятое название — «ArmanQala (2)». Версии 1–2: проект → задача, его задачи → подзадачи.</summary>
        public static ExportTask ToTop(ProjectFile file, IEnumerable<string> taken)
        {
            var top = new ExportTask { Name = UniqueName(file.Name, taken), Settings = file.Node?.Clone() ?? FromLegacy(file) };
            Fill(top.Settings, NodeSettings.Top());
            foreach (var t in file.Tasks ?? new List<TaskFile>()) top.Add(ToTask(t, top.Children.Select(c => c.Name)));
            return top;
        }

        /// <summary>Подзадача с вложенными из файла; занятое рядом название — «… (2)».</summary>
        public static ExportTask ToTask(TaskFile file, IEnumerable<string> taken)
        {
            var task = new ExportTask { Name = UniqueName(file.Name, taken), Settings = file.Node?.Clone() ?? FromLegacy(file) };
            Clean(task.Settings);
            foreach (var t in file.Tasks ?? new List<TaskFile>()) task.Add(ToTask(t, task.Children.Select(c => c.Name)));
            return task;
        }

        // версии 1–2: настройки проекта и его модели (версия 1 — модели задач-блоков, без повторов)
        private static NodeSettings FromLegacy(ProjectFile file)
        {
            var p = file.Settings ?? new LegacyProject();
            var models = (file.Models ?? new List<ExportModel>()).ToList();
            foreach (var t in file.Tasks ?? new List<TaskFile>())
                foreach (var m in t.Models ?? new List<ExportModel>())
                    if (m != null && !models.Any(x => string.Equals(x?.Path, m.Path, StringComparison.OrdinalIgnoreCase))) models.Add(m);
            return new NodeSettings
            {
                Models = models, Folder = p.Folder, ServerFolder = p.ServerFolder, NwcFolder = p.NwcFolder, ViewName = p.ViewName,
                NwfFolder = p.NwfFolder, ReportFolder = p.ReportFolder, TestsFiles = p.TestsFiles,
            };
        }

        // версия 2 — свои настройки задачи; версия 1 — свои файлы проверок (модели ушли в проект)
        private static NodeSettings FromLegacy(TaskFile file)
        {
            var o = file.Options;
            if (o == null) return new NodeSettings { TestsFiles = file.TestsFiles };
            return new NodeSettings
            {
                TestsFiles = o.TestsFiles ?? new List<string>(), NwfName = o.NwfName, NwfFolder = o.NwfFolder, ReportFolder = o.ReportFolder,
                Xml = o.Xml, Html = o.Html,
            };
        }

        /// <summary>Верхняя задача: чего нет — по умолчанию (у неё всё своё).</summary>
        private static void Fill(NodeSettings s, NodeSettings d)
        {
            Clean(s);
            s.Models = s.Models ?? d.Models;
            s.TestsFiles = s.TestsFiles ?? d.TestsFiles;
            s.HideSets = s.HideSets ?? d.HideSets;
            s.Folder = s.Folder ?? d.Folder;
            s.NwcFolder = s.NwcFolder ?? d.NwcFolder;
            s.NwfFolder = s.NwfFolder ?? d.NwfFolder;
            s.ReportFolder = s.ReportFolder ?? d.ReportFolder;
            s.ReportFormats = s.ReportFormats ?? d.ReportFormats;
        }

        // пустые записи моделей — мимо; прежние галочки XML / HTML — в список форматов
        private static void Clean(NodeSettings s)
        {
            if (s.Models != null) s.Models = s.Models.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Path)).ToList();
            if (s.ReportFormats == null && s.Xml != null && s.Html != null)
                s.ReportFormats = new[] { s.Xml == true ? "XML" : null, s.Html == true ? "HTML" : null }.Where(f => f != null).ToList();
            s.Xml = s.Html = null;
            // прежние строки «имя элемента содержит …» — тем же поиском, что набор Navisworks
            if (s.HideSets == null && s.HideNames != null) s.HideSets = s.HideNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(SearchSets.FromName).ToList();
            s.HideNames = null;
            if (s.Export != null && s.Export.Nwc == null) s.Export.Nwc = new NwcOptions();
            // {Блок} в NWF и отчётах больше нет (03.11.2026): прежние значения по умолчанию — новые, свои — без метки
            s.NwfFolder = s.NwfFolder == "{Блок}" ? "NWF" : WithoutBlock(s.NwfFolder);
            s.ReportFolder = s.ReportFolder == @"{Блок}\{Дата}" ? @"Отчеты\{Дата}" : WithoutBlock(s.ReportFolder);
            s.NwfName = WithoutBlock(s.NwfName);
            if (s.NwfName != null && s.NwfName.Trim().Trim('_', '-', ' ').Length == 0) s.NwfName = null;
        }

        private static string WithoutBlock(string s)
        {
            if (s == null || s.IndexOf("{Блок}", StringComparison.OrdinalIgnoreCase) < 0) return s;
            var r = System.Text.RegularExpressions.Regex.Replace(s, @"\{\s*Блок\s*\}[\\_\- ]?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return r.Trim().TrimEnd('\\');
        }

        /// <summary>Название, которого ещё нет: «X», «X (2)», «X (3)»…</summary>
        public static string UniqueName(string name, IEnumerable<string> taken)
        {
            var n = string.IsNullOrWhiteSpace(name) ? "Без названия" : name.Trim();
            var set = new HashSet<string>(taken.Select(t => (t ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(n)) return n;
            for (var i = 2; ; i++)
                if (!set.Contains(n + " (" + i + ")")) return n + " (" + i + ")";
        }
    }
}
