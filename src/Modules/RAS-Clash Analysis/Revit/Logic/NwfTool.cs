using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>Задание программе сборки NWF (NwfTool\): какой Navisworks, какие NWF и из каких NWC, куда копии NWF до обновления.</summary>
    [DataContract]
    public sealed class NwfToolJob
    {
        /// <summary>Папка Navisworks (Manage / Simulate): Autodesk.Navisworks.Automation.dll — оттуда.</summary>
        [DataMember(Order = 1)] public string NavisworksDir { get; set; }
        /// <summary>Появился файл — «Отмена»: следующие NWF не собираются.</summary>
        [DataMember(Order = 2)] public string CancelFile { get; set; }
        /// <summary>Копии NWF перед обновлением — в ней результаты проверок и статусы коллизий.</summary>
        [DataMember(Order = 3)] public string BackupDir { get; set; }
        [DataMember(Order = 4)] public List<NwfToolItem> Items { get; set; } = new List<NwfToolItem>();
    }

    [DataContract]
    public sealed class NwfToolItem
    {
        [DataMember(Order = 1)] public string Nwf { get; set; }
        [DataMember(Order = 2)] public List<string> Nwcs { get; set; } = new List<string>();
        /// <summary>«Скрыть»: поисковые наборы Navisworks (&lt;selectionset&gt; как в файле экспорта) — найденное скрывается до проверок.</summary>
        [DataMember(Order = 3)] public List<string> HideSets { get; set; } = new List<string>();
        /// <summary>Файлы проверок Clash Detective (.xml) — загружаются, если в NWF проверок ещё нет; затем — прогон всех проверок.</summary>
        [DataMember(Order = 4)] public List<string> Tests { get; set; } = new List<string>();
    }

    /// <summary>
    /// Программа сборки NWF (DECISIONS.md модуля, «Этап 2 — сборка NWF»): Navisworks без окна — только в отдельном процессе
    /// .NET Framework 4.8 x64 (из Revit 2026 напрямую нельзя). Плагин пишет задание файлом, программа отвечает строками:
    /// «@ 2 шаг» — шаг NWF №2, «= 2 ok 12.3 заметка» — NWF №2 готова (ok / fail / skip, секунды), остальное — в журнал.
    /// Общее у плагина и программы — этот файл (исходником); без Revit и Navisworks — проверяется тестами.
    /// </summary>
    public static class NwfTool
    {
        public const string ExeName = "RASv2.ClashAnalysis.NwfTool.exe";
        /// <summary>Модуль программы внутри Navisworks: какие модели в открытой NWF (имя + «.» + разработчик).</summary>
        public const string ModelsPlugin = "RasNwfModels.RASV";
        /// <summary>Модуль программы: скрыть найденное наборами (имена — после файла итога), сами наборы — убрать.</summary>
        public const string HidePlugin = "RasNwfHide.RASV";
        /// <summary>Модуль программы: проверки — «count» (сколько в NWF) или «run» (прогнать все, итог по каждой).</summary>
        public const string TestsPlugin = "RasNwfTests.RASV";
        /// <summary>Встроенный импорт Navisworks: «Импортировать наборы поисковых запросов» (XML &lt;exchange&gt;&lt;selectionsets&gt;).</summary>
        public const string ImportSets = "NativeExportPluginAdaptor_XmlSelectionSetsImportPlugin_Import.Navisworks";
        /// <summary>Встроенный импорт Clash Detective: «Тест конфликтов в формате XML».</summary>
        public const string ImportTests = "NativeExportPluginAdaptor_LcOdpClashTestsImportPlugin_Import.Navisworks";
        public const string Ok = "ok", Fail = "fail", Skip = "skip";

        public static string StepLine(int index, string step) => "@ " + index + " " + OneLine(step);

        public static string DoneLine(int index, string result, double seconds, string note) =>
            "= " + index + " " + result + " " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " " + OneLine(note);

        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        /// <summary>Строка программы: '@' — шаг, '=' — итог NWF, ' ' — просто в журнал.</summary>
        public static char Parse(string line, out int index, out string result, out double seconds, out string text)
        {
            index = -1;
            result = null;
            seconds = 0;
            text = line ?? "";
            if (text.Length < 4 || text[1] != ' ' || text[0] != '@' && text[0] != '=') return ' ';
            var parts = text.Split(new[] { ' ' }, text[0] == '@' ? 3 : 5);
            if (parts.Length < 2 || !int.TryParse(parts[1], out index)) return ' ';
            if (text[0] == '@')
            {
                text = parts.Length > 2 ? parts[2] : "";
                return '@';
            }
            if (parts.Length < 4 || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                index = -1;
                return ' ';
            }
            result = parts[2];
            text = parts.Length > 4 ? parts[4] : "";
            return '=';
        }

        /// <summary>
        /// Какой Navisworks: сначала 2023 (NWC компании — из экспортёра 2023), потом новее; Manage, затем Simulate. Нет — null.
        /// </summary>
        public static string FindNavisworks(string programFiles, Func<string, bool> fileExists)
        {
            foreach (var year in new[] { 2023, 2024, 2025, 2026, 2027 })
                foreach (var edition in new[] { "Manage", "Simulate" })
                {
                    var dir = Path.Combine(programFiles, "Autodesk", "Navisworks " + edition + " " + year);
                    if (fileExists(Path.Combine(dir, "Autodesk.Navisworks.Automation.dll")) && fileExists(Path.Combine(dir, "Roamer.exe"))) return dir;
                }
            return null;
        }

        /// <summary>
        /// Постоянная NWF: что добавить. inside — NWC, которые уже в NWF; wanted — NWC строки (есть на диске). append — нет в NWF;
        /// elsewhere — в NWF NWC с тем же именем из другой папки (не добавляется: была бы вторая такая же модель); extra — в NWF есть,
        /// а у строки нет (не убирается: в ней — результаты проверок; руками).
        /// </summary>
        public static void Diff(IEnumerable<string> inside, IEnumerable<string> wanted, out List<string> append, out List<string> elsewhere,
            out List<string> extra)
        {
            var have = inside.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var want = wanted.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            append = new List<string>();
            elsewhere = new List<string>();
            foreach (var w in want)
            {
                if (have.Any(h => Same(h, w))) continue;
                if (have.Any(h => string.Equals(Path.GetFileName(h), Path.GetFileName(w), StringComparison.OrdinalIgnoreCase))) elsewhere.Add(w);
                else append.Add(w);
            }
            extra = have.Where(h => !want.Any(w => Same(h, w) || string.Equals(Path.GetFileName(h), Path.GetFileName(w), StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static bool Same(string a, string b) =>
            string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        private static string Norm(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var s = File.Create(path))
                new DataContractJsonSerializer(typeof(T)).WriteObject(s, value);
        }

        public static T Read<T>(string path) where T : class
        {
            using (var s = File.OpenRead(path))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s);
        }
    }
}
