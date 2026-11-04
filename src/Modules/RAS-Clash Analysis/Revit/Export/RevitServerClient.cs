using System;
using System.Collections.Generic;
using System.Net.Http;
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
        // 10.10.1.88 (2026) отвечает 8–13 с (04.11.2026) — 15 с было впритык. Соединений к серверу — до 16: в .NET Framework
        // (Revit 2023) по умолчанию два, и запрос к службе 2026 стоял в очереди за зависшим запросом к службе 2023 того же
        // сервера — истекал вместе с ним, и сервер числился «не отвечает» для обеих версий
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { MaxConnectionsPerServer = 16 }) { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task<ServerContents> ContentsAsync(string host, int year, IList<string> folder) =>
            RevitServers.ParseContents(await GetAsync(RevitServers.ContentsUrl(host, year, folder), "\"Folders\"").ConfigureAwait(false));

        /// <summary>Сведения о модели — дата изменения.</summary>
        public static async Task<ServerModelInfo> ModelInfoAsync(string host, int year, IList<string> folder, string model) =>
            RevitServers.ParseModelInfo(await GetAsync(RevitServers.ModelInfoUrl(host, year, folder, model), "\"DateModified\"").ConfigureAwait(false));

        /// <summary>
        /// Ответ службы. Код ошибки, а в ответе — то, что просили (есть поле expected), — ответ принимается: служба 2026 на
        /// 10.10.1.88 (04.11.2026) отдаёт полный список папок с кодом «500 System», и сервер числился «не отвечает».
        /// </summary>
        private static async Task<string> GetAsync(string url, string expected)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Add("User-Name", Environment.UserName);
                request.Headers.Add("User-Machine-Name", Environment.MachineName);
                request.Headers.Add("Operation-GUID", Guid.NewGuid().ToString());
                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
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
