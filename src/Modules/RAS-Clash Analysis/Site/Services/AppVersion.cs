namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Метка версии сайта для адресов стилей и скриптов («site.css?v=…»): после обновления браузер берёт новые файлы,
/// а не старые из своего кэша.
/// </summary>
public static class AppVersion
{
    public static readonly string Tag = File.GetLastWriteTimeUtc(typeof(AppVersion).Assembly.Location).Ticks.ToString("x");
}
