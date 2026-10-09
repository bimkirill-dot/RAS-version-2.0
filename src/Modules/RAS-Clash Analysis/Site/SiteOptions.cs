namespace RASv2.ClashAnalysis.Site;

/// <summary>
/// Настройки сайта — раздел «Site» в appsettings.json. Пустые пути — значения по умолчанию
/// (PRINCIPLES.md, «Сайт модуля»: база — на локальном диске машины сайта, копии — на сервере).
/// </summary>
public sealed class SiteOptions
{
    public const string ModuleFolder = "RAS-Clash Analysis";

    /// <summary>База, снимки коллизий, журнал сайта. По умолчанию — %LocalAppData%\RAS_v_2.0\RAS-Clash Analysis\Site.</summary>
    public string DataFolder { get; set; }

    /// <summary>Копии базы на сервере. По умолчанию — Data\RAS-Clash Analysis\Копии базы в папке плагина на сервере.</summary>
    public string BackupFolder { get; set; }

    /// <summary>Как часто проверять папки проектов на новые отчёты, минут; 0 — только по кнопке.</summary>
    public int ScanMinutes { get; set; } = 10;

    /// <summary>Сколько хранить копий: часовых и ночных.</summary>
    public int HourlyBackups { get; set; } = 48;
    public int NightlyBackups { get; set; } = 60;

    /// <summary>Папка сайта на этом компьютере: программа («Программа»), данные, settings.json.</summary>
    public static string DefaultFolder => Path.Combine(RasPaths.Local, ModuleFolder, "Site");

    /// <summary>Свои настройки сайта (тот же вид, что appsettings.json); обновление сайта их не трогает.</summary>
    public static string LocalSettingsFile => Path.Combine(DefaultFolder, "settings.json");

    public string Data => string.IsNullOrWhiteSpace(DataFolder)
        ? DefaultFolder
        : Environment.ExpandEnvironmentVariables(DataFolder);

    public string Backups => string.IsNullOrWhiteSpace(BackupFolder)
        ? Path.Combine(RasPaths.SharedData, ModuleFolder, "Копии базы")
        : Environment.ExpandEnvironmentVariables(BackupFolder);

    public string DatabaseFile => Path.Combine(Data, "clashes.db");
    public string ImagesFolder => Path.Combine(Data, "Снимки");
    public string LogsFolder => Path.Combine(Data, "Журнал");
    /// <summary>Копии базы на этом компьютере — на случай, если сервер недоступен.</summary>
    public string LocalBackups => Path.Combine(Data, "Копии базы");
}
