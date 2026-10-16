using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>Настройки окружения сайта — то, что у компании своё (DECISIONS.md модуля, «Настройки сайта»).</summary>
public sealed class SiteSettingsValues
{
    /// <summary>Папка копий базы на сервере; пусто — по умолчанию (папка плагина на сервере).</summary>
    public string BackupFolder { get; set; } = "";
    public int HourlyBackups { get; set; }
    public int NightlyBackups { get; set; }
    /// <summary>Как часто проверять папки проектов, минут; 0 — только по кнопке.</summary>
    public int ScanMinutes { get; set; }
    public int Port { get; set; }
    /// <summary>Сайт доступен коллегам по сети (иначе — только на этом компьютере).</summary>
    public bool Network { get; set; }
}

/// <summary>
/// Страница «Настройки»: читает и пишет свой settings.json сайта (обновление сайта его не трогает), не теряя в нём
/// ничего, чего страница не знает. Папка копий, сколько хранить и как часто проверять папки — действуют сразу;
/// порт и доступ по сети — после перезапуска сайта.
/// </summary>
public sealed class SiteSettings
{
    private static readonly Regex UrlPort = new Regex(@"^\s*https?://(?<host>[^:/]+):(?<port>\d+)", RegexOptions.IgnoreCase);

    private readonly SiteOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<SiteSettings> _log;
    private readonly Data.Database _db;

    public SiteSettings(SiteOptions options, IConfiguration config, ILogger<SiteSettings> log, Data.Database db)
    {
        _db = db;
        _options = options;
        _config = config;
        _log = log;
        var (port, network) = ParseUrls(config["Urls"]);
        RunningPort = port;
        RunningNetwork = network;
    }

    /// <summary>Порт и доступ по сети, с которыми сайт запущен сейчас.</summary>
    public int RunningPort { get; }
    public bool RunningNetwork { get; }

    /// <summary>Папка копий по умолчанию — если своя не задана.</summary>
    public string DefaultBackupFolder => Path.Combine(RasPaths.SharedData, SiteOptions.ModuleFolder, "Копии базы");

    public SiteSettingsValues Current()
    {
        var (port, network) = ParseUrls(ReadFile()?["Urls"]?.GetValue<string>() ?? _config["Urls"]);
        return new SiteSettingsValues
        {
            // в поле — фактическая папка: своя или по умолчанию (папка плагина на сервере)
            BackupFolder = string.IsNullOrWhiteSpace(_options.BackupFolder) ? DefaultBackupFolder : _options.BackupFolder,
            HourlyBackups = _options.HourlyBackups,
            NightlyBackups = _options.NightlyBackups,
            ScanMinutes = _options.ScanMinutes,
            Port = port,
            Network = network,
        };
    }

    /// <summary>Что не так со значениями; всё в порядке — null.</summary>
    public static string Validate(SiteSettingsValues v)
    {
        if (v.ScanMinutes < 0 || v.ScanMinutes > 1440) return "Проверять папки: от 0 до 1440 минут.";
        if (v.HourlyBackups < 1 || v.HourlyBackups > 1000) return "Часовых копий: от 1 до 1000.";
        if (v.NightlyBackups < 1 || v.NightlyBackups > 1000) return "Ночных копий: от 1 до 1000.";
        if (v.Port < 1024 || v.Port > 65535) return "Порт: от 1024 до 65535.";
        if (string.IsNullOrWhiteSpace(v.BackupFolder)) return "Укажите папку копий базы.";
        if (v.BackupFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "В пути папки копий недопустимые знаки.";
        return null;
    }

    /// <summary>Сохранить в settings.json и применить, что можно, сразу. Возвращает: нужен ли перезапуск сайта.</summary>
    public bool Save(SiteSettingsValues v)
    {
        var before = Current();
        var root = ReadFile() ?? new JsonObject();
        if (root["Site"] is not JsonObject site) root["Site"] = site = new JsonObject();
        // папка по умолчанию запоминается как «по умолчанию» (пусто): тогда она следует за папкой плагина на сервере
        var folder = (v.BackupFolder ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        site["BackupFolder"] = string.Equals(folder, DefaultBackupFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? "" : folder;
        site["HourlyBackups"] = v.HourlyBackups;
        site["NightlyBackups"] = v.NightlyBackups;
        site["ScanMinutes"] = v.ScanMinutes;
        root["Urls"] = $"http://{(v.Network ? "*" : "localhost")}:{v.Port}";

        Directory.CreateDirectory(Path.GetDirectoryName(_options.SettingsFile)!);
        var temp = _options.SettingsFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, _options.SettingsFile, overwrite: true);

        // сразу: копии и проверка папок читают эти значения при каждом обращении
        _options.BackupFolder = (string)site["BackupFolder"];
        _options.HourlyBackups = v.HourlyBackups;
        _options.NightlyBackups = v.NightlyBackups;
        _options.ScanMinutes = v.ScanMinutes;
        _log.LogInformation("Настройки сайта сохранены: копии — {Backups}, часовых {Hourly}, ночных {Nightly}, проверка папок — {Scan} мин, адрес — {Urls}",
            _options.Backups, v.HourlyBackups, v.NightlyBackups, v.ScanMinutes, (string)root["Urls"]);
        // в историю сайта — только то, что поменялось
        var changes = new List<string>();
        var folderNow = (v.BackupFolder ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        if (!string.Equals(before.BackupFolder.TrimEnd('\\', '/'), folderNow, StringComparison.OrdinalIgnoreCase)) changes.Add($"папка копий базы: {before.BackupFolder} → {folderNow}");
        if (before.HourlyBackups != v.HourlyBackups) changes.Add($"часовых копий: {before.HourlyBackups} → {v.HourlyBackups}");
        if (before.NightlyBackups != v.NightlyBackups) changes.Add($"ночных копий: {before.NightlyBackups} → {v.NightlyBackups}");
        if (before.ScanMinutes != v.ScanMinutes) changes.Add($"проверять папки: каждые {before.ScanMinutes} → {v.ScanMinutes} мин");
        if (before.Port != v.Port) changes.Add($"порт: {before.Port} → {v.Port} (после перезапуска)");
        if (before.Network != v.Network) changes.Add($"доступ по сети: {(before.Network ? "да" : "нет")} → {(v.Network ? "да" : "нет")} (после перезапуска)");
        if (changes.Count > 0)
        {
            var text = string.Join("; ", changes);
            Data.SiteHistory.Write(_db, null, Data.SiteHistory.Settings, char.ToUpper(text[0]) + text[1..]);
        }
        return v.Port != RunningPort || v.Network != RunningNetwork;
    }

    /// <summary>
    /// Пустые категории и подкатегории в «Типе проверки» — показывать или нет. Сохраняется сразу (флажок у «Категорий проверок»,
    /// отдельно от «Сохранить» внизу страницы), действует на все проекты.
    /// </summary>
    public void SetShowEmptyCategories(bool show)
    {
        if (_options.ShowEmptyCategories == show) return;
        var root = ReadFile() ?? new JsonObject();
        if (root["Site"] is not JsonObject site) root["Site"] = site = new JsonObject();
        site["ShowEmptyCategories"] = show;
        Directory.CreateDirectory(Path.GetDirectoryName(_options.SettingsFile)!);
        var temp = _options.SettingsFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, _options.SettingsFile, overwrite: true);
        _options.ShowEmptyCategories = show;
        Data.SiteHistory.Write(_db, null, Data.SiteHistory.Settings,
            show ? "Пустые категории и подкатегории в «Типе проверки» — показывать" : "Пустые категории и подкатегории в «Типе проверки» — не показывать");
    }

    /// <summary>Можно ли писать в папку копий: пробный файл создаётся и сразу удаляется (ничего чужого не трогает).</summary>
    public static string CheckFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".проверка-записи-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "проверка");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return ex.Message;
        }
    }

    private JsonObject ReadFile()
    {
        try
        {
            return File.Exists(_options.SettingsFile) ? JsonNode.Parse(File.ReadAllText(_options.SettingsFile)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>«http://localhost:5080» → (5080, нет); «http://*:5080» → (5080, да).</summary>
    private static (int Port, bool Network) ParseUrls(string urls)
    {
        var first = (urls ?? "").Split(';')[0];
        var m = UrlPort.Match(first);
        if (!m.Success) return (5080, false);
        var host = m.Groups["host"].Value;
        var network = host == "*" || host == "+" || host == "0.0.0.0";
        return (int.Parse(m.Groups["port"].Value), network);
    }
}
