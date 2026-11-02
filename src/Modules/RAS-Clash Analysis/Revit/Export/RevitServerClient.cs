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

        public static async Task<ServerContents> ContentsAsync(string host, int year, IList<string> folder)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, RevitServers.ContentsUrl(host, year, folder)))
            {
                request.Headers.Add("User-Name", Environment.UserName);
                request.Headers.Add("User-Machine-Name", Environment.MachineName);
                request.Headers.Add("Operation-GUID", Guid.NewGuid().ToString());
                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return RevitServers.ParseContents(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
    }
}
