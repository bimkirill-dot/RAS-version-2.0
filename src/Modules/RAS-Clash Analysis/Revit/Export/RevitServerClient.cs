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
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public static async Task<ServerContents> ContentsAsync(string host, int year, IList<string> folder) =>
            RevitServers.ParseContents(await GetAsync(RevitServers.ContentsUrl(host, year, folder)).ConfigureAwait(false));

        /// <summary>Сведения о модели — дата изменения.</summary>
        public static async Task<ServerModelInfo> ModelInfoAsync(string host, int year, IList<string> folder, string model) =>
            RevitServers.ParseModelInfo(await GetAsync(RevitServers.ModelInfoUrl(host, year, folder, model)).ConfigureAwait(false));

        private static async Task<string> GetAsync(string url)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Add("User-Name", Environment.UserName);
                request.Headers.Add("User-Machine-Name", Environment.MachineName);
                request.Headers.Add("Operation-GUID", Guid.NewGuid().ToString());
                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
