using System;

namespace RASv2.ClashAnalysis.Logic
{
    /// <summary>
    /// С какого адреса сайта мост принимает просьбы (заголовок Origin браузера): адрес сайта коллизий (от ядра — на сервере или,
    /// пока сайт на компьютере разработчика, — его имя), его же полное имя в домене («…ras.kz») и этот компьютер (localhost) с
    /// тем же портом — разработчик открывает сайт у себя. Чужие страницы Revit не командуют. Без Revit API — тесты.
    /// </summary>
    public static class SiteOrigins
    {
        public static bool Allowed(string origin, string site)
        {
            if (!Uri.TryCreate(origin ?? "", UriKind.Absolute, out var o) || !Uri.TryCreate(site ?? "", UriKind.Absolute, out var s)) return false;
            if (!string.Equals(o.Scheme, s.Scheme, StringComparison.OrdinalIgnoreCase) || o.Port != s.Port) return false;
            var host = o.Host;
            return string.Equals(host, s.Host, StringComparison.OrdinalIgnoreCase)
                   || host.StartsWith(s.Host + ".", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || host == "127.0.0.1";
        }
    }
}
