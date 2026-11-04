using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Запрос к службе администрирования Revit Server — только чтение содержимого папки (адрес и разбор ответа —
    /// Logic\RevitServers.cs). Заголовки User-Name, User-Machine-Name, Operation-GUID служба требует у каждого запроса.
    /// </summary>
    internal static class RevitServerClient
    {
        // Соединений к серверу — до 16: в .NET Framework (Revit 2023) по умолчанию два, и запрос к службе 2026 стоял в очереди
        // за зависшим запросом к службе 2023 того же сервера — истекал вместе с ним. Ожидание — своё у каждого запроса.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { MaxConnectionsPerServer = 16 }) { Timeout = Timeout.InfiniteTimeSpan };

        // содержимое папки: служба считает размер каждой папки — корень хранилища 10.10.1.88 (Revit 2023) — 67 с, (2026) — 8–13 с
        // (04.11.2026); сведения о сервере и модели — быстро
        private static readonly TimeSpan ContentsWait = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan QuickWait = TimeSpan.FromSeconds(30);

        public static async Task<ServerContents> ContentsAsync(string host, int year, IList<string> folder) =>
            RevitServers.ParseContents(await GetAsync(RevitServers.ContentsUrl(host, year, folder), "\"Folders\"", ContentsWait).ConfigureAwait(false));

        /// <summary>Отвечает ли служба этой версии на сервере — по сведениям о сервере (мгновенно).</summary>
        public static async Task PingAsync(string host, int year) =>
            await GetAsync(RevitServers.PropertiesUrl(host, year), "\"Servers\"", QuickWait).ConfigureAwait(false);

        /// <summary>Сведения о модели — дата изменения.</summary>
        public static async Task<ServerModelInfo> ModelInfoAsync(string host, int year, IList<string> folder, string model) =>
            RevitServers.ParseModelInfo(await GetAsync(RevitServers.ModelInfoUrl(host, year, folder, model), "\"DateModified\"", QuickWait).ConfigureAwait(false));

        /// <summary>
        /// Ответ службы. Код ошибки, а в ответе — то, что просили (есть поле expected), — ответ принимается: служба 2026 на
        /// 10.10.1.88 (04.11.2026) отдаёт полный список папок с кодом «500 System», и сервер числился «не отвечает».
        /// </summary>
        private static async Task<string> GetAsync(string url, string expected, TimeSpan wait)
        {
            using (var timeout = new CancellationTokenSource(wait))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Add("User-Name", Environment.UserName);
                request.Headers.Add("User-Machine-Name", Environment.MachineName);
                request.Headers.Add("Operation-GUID", Guid.NewGuid().ToString());
                using (var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode && (body == null || body.IndexOf(expected, StringComparison.Ordinal) < 0))
                        response.EnsureSuccessStatusCode();
                    return body;
                }
            }
        }
    }
}
