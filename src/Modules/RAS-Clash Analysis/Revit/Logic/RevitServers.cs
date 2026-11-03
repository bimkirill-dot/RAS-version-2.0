using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Содержимое папки Revit Server: вложенные папки и модели (ответ службы администрирования, …/contents).</summary>
    [DataContract]
    public sealed class ServerContents
    {
        [DataMember] public List<ServerFolder> Folders { get; set; } = new List<ServerFolder>();
        [DataMember] public List<ServerModel> Models { get; set; } = new List<ServerModel>();
    }

    [DataContract]
    public sealed class ServerFolder
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public bool HasContents { get; set; }
    }

    [DataContract]
    public sealed class ServerModel
    {
        /// <summary>Имя с «.rvt».</summary>
        [DataMember] public string Name { get; set; }
        [DataMember] public long ModelSize { get; set; }
        /// <summary>Когда изменена — не из ответа о папке, а из сведений о модели (…/modelInfo; кэш хранит).</summary>
        [DataMember(EmitDefaultValue = false)] public DateTime? Modified { get; set; }
    }

    /// <summary>Сведения о модели Revit Server (…/{папка|модель}/modelInfo): когда изменена и кем.</summary>
    [DataContract]
    public sealed class ServerModelInfo
    {
        [DataMember] public DateTime DateModified { get; set; }
        [DataMember] public string LastModifiedBy { get; set; }
    }

    /// <summary>
    /// Revit Server для «Добавить модели…» (разработчик, 02.11.2026: окно Revit «Открыть» из плагина Revit Server не показывает):
    /// серверы — из настроек Revit (RSN.ini своей версии), папки и модели — через службу администрирования Revit Server
    /// (только чтение: GET …/AdminRESTService.svc/{папка}/contents). Без Revit API.
    /// </summary>
    public static class RevitServers
    {
        /// <summary>Версии Revit Server, из которых добавляются модели (разработчик, 03.11.2026: «все версии — из любого Revit»).</summary>
        public static readonly int[] Years = { 2023, 2024, 2025, 2026 };

        /// <summary>Серверы, настроенные у Revit этой версии: %ProgramData%\Autodesk\Revit Server {год}\Config\RSN.ini.</summary>
        public static List<string> Hosts(int year)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "Revit Server " + year, "Config", "RSN.ini");
            return File.Exists(path) ? ParseRsn(File.ReadAllText(path)) : new List<string>();
        }

        /// <summary>
        /// Версии Revit, установленные на компьютере (разработчик, 03.11.2026: «предлагает серверы тех Revit, которых даже нет»):
        /// %ProgramFiles%\Autodesk\Revit {год}\Revit.exe; первой — та, из которой открыто окно. installed — для проверок.
        /// </summary>
        public static List<int> InstalledYears(int thisYear, Func<int, bool> installed = null)
        {
            installed = installed ?? (y => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk", "Revit " + y, "Revit.exe")));
            var list = new List<int> { thisYear };
            list.AddRange(Years.Where(y => y != thisYear && installed(y)));
            return list;
        }

        /// <summary>Откуда брать модели: пары «сервер · версия» — из настроек (RSN.ini) каждой установленной версии, по порядку версий.</summary>
        public static List<(string Host, int Year)> Sources(IEnumerable<int> years, Func<int, List<string>> hosts = null)
        {
            hosts = hosts ?? Hosts;
            return years.SelectMany(y => hosts(y).Select(h => (h, y))).ToList();
        }

        /// <summary>RSN.ini — по серверу в строке; пустые и комментарии («;», «#») — мимо, повторы — один раз.</summary>
        public static List<string> ParseRsn(string text) =>
            (text ?? "").Split('\r', '\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith(";") && !l.StartsWith("#"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Адрес содержимого папки: «http://сервер/RevitServerAdminRESTService2026/AdminRESTService.svc/a|b/contents»; корень —
        /// «|»; части — в кодировке адреса (кириллица, пробелы).
        /// </summary>
        public static string ContentsUrl(string host, int year, IList<string> folder) =>
            "http://" + host + "/RevitServerAdminRESTService" + year + "/AdminRESTService.svc/" +
            (folder.Count == 0 ? "%7C" : string.Join("%7C", folder.Select(Uri.EscapeDataString))) + "/contents";

        /// <summary>Адрес сведений о модели (дата изменения): «…/AdminRESTService.svc/a|b|модель.rvt/modelInfo».</summary>
        public static string ModelInfoUrl(string host, int year, IList<string> folder, string model) =>
            "http://" + host + "/RevitServerAdminRESTService" + year + "/AdminRESTService.svc/" +
            string.Join("%7C", folder.Concat(new[] { model }).Select(Uri.EscapeDataString)) + "/modelInfo";

        /// <summary>Сведения о модели; дата — местным временем (служба отдаёт «/Date(мс)/»).</summary>
        public static ServerModelInfo ParseModelInfo(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
            {
                var info = (ServerModelInfo)new DataContractJsonSerializer(typeof(ServerModelInfo)).ReadObject(ms);
                if (info.DateModified.Kind == DateTimeKind.Utc) info.DateModified = info.DateModified.ToLocalTime();
                return info;
            }
        }

        /// <summary>Даты моделей из прежнего ответа — в новый, у тех же моделей (имя и размер те же — не менялись).</summary>
        public static void KeepDates(ServerContents from, ServerContents to)
        {
            foreach (var m in to.Models)
            {
                var old = from.Models.FirstOrDefault(x => x.Name == m.Name && x.ModelSize == m.ModelSize);
                if (old?.Modified != null && m.Modified == null) m.Modified = old.Modified;
            }
        }

        /// <summary>Путь модели, как его пишет Revit: «RSN://10.10.1.88/2025_Ариал/01_AR/S03/модель.rvt».</summary>
        public static string RsnPath(string host, IList<string> folder, string model) =>
            "RSN://" + host + "/" + string.Join("/", folder.Concat(new[] { model }));

        public static ServerContents ParseContents(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
            {
                var c = (ServerContents)new DataContractJsonSerializer(typeof(ServerContents)).ReadObject(ms);
                c.Folders = c.Folders ?? new List<ServerFolder>();
                c.Models = c.Models ?? new List<ServerModel>();
                return c;
            }
        }

        /// <summary>Ответы одинаковые — то же содержимое папки (кэш обновлять незачем).</summary>
        public static bool Same(ServerContents a, ServerContents b) =>
            a.Folders.Select(f => f.Name + "|" + f.HasContents).SequenceEqual(b.Folders.Select(f => f.Name + "|" + f.HasContents)) &&
            a.Models.Select(m => m.Name + "|" + m.ModelSize).SequenceEqual(b.Models.Select(m => m.Name + "|" + m.ModelSize));

        /// <summary>Размер модели: «734 МБ», «1,2 ГБ».</summary>
        public static string SizeText(long bytes)
        {
            const double mb = 1024d * 1024d;
            return bytes >= 1024 * mb
                ? (bytes / (1024 * mb)).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " ГБ"
                : Math.Max(1, (long)Math.Round(bytes / mb)) + " МБ";
        }
    }
}
