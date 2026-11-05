using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Одна NWC задания для фонового Revit другой версии (NwcJob — файлом).</summary>
    [DataContract]
    public sealed class HandoffJob
    {
        [DataMember(Order = 1)] public string ModelPath { get; set; }
        [DataMember(Order = 2)] public int ModelYear { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string NwcName { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Block { get; set; }
        [DataMember(Order = 5)] public string NwcPath { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public string View { get; set; }
        [DataMember(Order = 7)] public ExportSettings Export { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public ModelDateFilter Edited { get; set; }
        [DataMember(Order = 9)] public List<string> Rows { get; set; } = new List<string>();
    }

    /// <summary>
    /// Задание фоновому Revit другой версии (разработчик, 05.11.2026: «если модель в Revit 2023 — пусть плагин запускает Revit 2023
    /// для обработки, и всё; фоново, чтобы открывался максимально быстро»): модели этой версии, журнал, Shift, копий сразу.
    /// </summary>
    [DataContract]
    public sealed class NwcHandoff
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public int Year { get; set; }
        [DataMember(Order = 3)] public string Title { get; set; }
        [DataMember(Order = 4)] public string Journal { get; set; }
        [DataMember(Order = 5)] public bool Force { get; set; }
        [DataMember(Order = 6)] public int Parallel { get; set; }
        [DataMember(Order = 7)] public List<HandoffJob> Jobs { get; set; } = new List<HandoffJob>();
        [DataMember(Order = 8)] public DateTime Created { get; set; } = DateTime.Now;
    }

    /// <summary>Строка хода у фонового Revit: состояние NWC (как NwcState — словом), шаг, итог, время.</summary>
    [DataContract]
    public sealed class HandoffState
    {
        [DataMember(Order = 1)] public int Index { get; set; }
        [DataMember(Order = 2)] public string State { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string Step { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
        [DataMember(Order = 5)] public double Seconds { get; set; }
    }

    /// <summary>Ход задания: взято ли, закончено ли, строки; пишет фоновый Revit, читает окно хода.</summary>
    [DataContract]
    public sealed class HandoffProgress
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public bool Finished { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Order = 4)] public List<HandoffState> Items { get; set; } = new List<HandoffState>();
        [DataMember(Order = 5)] public DateTime Updated { get; set; } = DateTime.Now;
    }

    /// <summary>Фоновый Revit жив: номер процесса и когда отметился.</summary>
    [DataContract]
    public sealed class WorkerAlive
    {
        [DataMember(Order = 1)] public int ProcessId { get; set; }
        [DataMember(Order = 2)] public DateTime At { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Фоновые Revit других версий: папка заданий, файлы, кто какую модель выгружает. Связь — файлами в папке заданий на этом
    /// компьютере: «.job» — задание (фоновый Revit берёт, переименовывая в «.taken»), «.progress» — ход, «.cancel» — «Отмена»,
    /// «worker-{год}.alive» — фоновый Revit жив (отмечается каждые 2 с). Записи — через временный файл: читатель не видит половину.
    /// </summary>
    public static class NwcWorkers
    {
        /// <summary>Переменная окружения фонового Revit — его версия: свой Revit пользователя задания не берёт.</summary>
        public const string EnvVar = "RAS_NWC_WORKER";

        /// <summary>Сколько фоновый Revit ждёт новое задание, прежде чем закрыться (разработчик: «максимально быстро» — второй раз не запускать).</summary>
        public static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(15);

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAS_v_2.0", "RAS-Clash Analysis", "Выгрузка NWC", "Задания");

        public static string JobPath(string dir, string id) => Path.Combine(dir, id + ".job");
        public static string TakenPath(string dir, string id) => Path.Combine(dir, id + ".taken");
        public static string ProgressPath(string dir, string id) => Path.Combine(dir, id + ".progress");
        public static string CancelPath(string dir, string id) => Path.Combine(dir, id + ".cancel");
        public static string AlivePath(string dir, int year) => Path.Combine(dir, "worker-" + year + ".alive");

        /// <summary>
        /// В каком Revit выгружать модель: Revit Server — в Revit её версии (у каждой версии своё хранилище, а Revit новее открыл бы
        /// модель только обновляя — разработчик: «давай без обновления»); файл — по его версии (fileYear; 0 — не известна — в этом).
        /// </summary>
        public static int RunYear(ExportModel model, int fileYear, int thisYear)
        {
            var year = ExportNodes.IsServerPath(model.Path) ? model.Year : fileYear;
            return year > 0 ? year : thisYear;
        }

        public static HandoffJob ToHandoff(NwcJob job) => new HandoffJob
        {
            ModelPath = job.Model.Path, ModelYear = job.Model.Year, NwcName = job.Model.NwcName, Block = job.Model.Block,
            NwcPath = job.NwcPath, View = job.View, Export = job.Export, Edited = job.Edited, Rows = job.Rows.ToList(),
        };

        public static NwcJob FromHandoff(HandoffJob h)
        {
            var job = new NwcJob
            {
                Model = new ExportModel { Path = h.ModelPath, Year = h.ModelYear, NwcName = h.NwcName, Block = h.Block },
                NwcPath = h.NwcPath, View = h.View, Export = h.Export ?? new ExportSettings(), Edited = h.Edited,
            };
            job.Rows.AddRange(h.Rows ?? new List<string>());
            return job;
        }

        /// <summary>Жив ли фоновый Revit: отметился недавно и процесс есть (isRunning — для проверок).</summary>
        public static bool IsAlive(WorkerAlive alive, DateTime now, Func<int, bool> isRunning) =>
            alive != null && now - alive.At < TimeSpan.FromSeconds(20) && isRunning(alive.ProcessId);

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
            using (var s = File.Create(tmp))
                new DataContractJsonSerializer(typeof(T)).WriteObject(s, value);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Прочитать; нет файла или он испорчен (пишется прямо сейчас) — null.</summary>
        public static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
