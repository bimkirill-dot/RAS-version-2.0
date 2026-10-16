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
    /// <summary>«Тип проверки» в проекте: категории и подкатегории из «Настроек» видны всегда, пустые — серым с нулём.</summary>
    public bool ShowEmptyCategories { get; set; }
    public int NightlyBackups { get; set; } = 60;

    /// <summary>Папка сайта на этом компьютере: программа («Программа»), данные, settings.json.</summary>
    public static string DefaultFolder => Path.Combine(RasPaths.Local, ModuleFolder, "Site");

    /// <summary>
    /// Свои настройки сайта (тот же вид, что appsettings.json; страница «Настройки») — рядом с данными этого сайта:
    /// обычно %LocalAppData%\RAS_v_2.0\RAS-Clash Analysis\Site\settings.json; обновление сайта их не трогает.
    /// Папку данных сам settings.json не задаёт (её задают appsettings.json или переменная Site__DataFolder).
    /// </summary>
    public static string SettingsFileFor(string dataFolder) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataFolder) ? DefaultFolder : Environment.ExpandEnvironmentVariables(dataFolder), "settings.json");

    public string SettingsFile => SettingsFileFor(DataFolder);

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
