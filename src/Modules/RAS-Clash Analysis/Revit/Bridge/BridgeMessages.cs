using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RASv2.ClashAnalysis.Bridge
{
    /// <summary>
    /// Договорённость сайта и моста (JSON). Version растёт, когда добавляется новое; старое не меняет смысла — сайт один для
    /// всех, а сборки модуля у людей разные (PRINCIPLES.md, раздел 10, «Сайт»).
    /// </summary>
    internal static class BridgeProtocol
    {
        public const int Version = 1;

        public static T Read<T>(byte[] json) where T : class
        {
            using (var ms = new MemoryStream(json))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        public static byte[] Write<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        public static string Text(byte[] utf8) => Encoding.UTF8.GetString(utf8);
    }

    /// <summary>GET /status — мост жив, какой Revit, какие модели и связи открыты (индикатор на сайте).</summary>
    [DataContract]
    internal sealed class StatusReply
    {
        [DataMember(Name = "version")] public int Version { get; set; } = BridgeProtocol.Version;
        [DataMember(Name = "revit")] public int Revit { get; set; }
        [DataMember(Name = "documents")] public List<OpenDocument> Documents { get; set; } = new List<OpenDocument>();
    }

    [DataContract]
    internal sealed class OpenDocument
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "active")] public bool Active { get; set; }
        [DataMember(Name = "links")] public List<string> Links { get; set; } = new List<string>();
    }

    /// <summary>POST /show — показать коллизию: её элементы (файл модели из отчёта и номер элемента).</summary>
    [DataContract]
    internal sealed class ShowRequest
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        /// <summary>«Конфликт14» — для сообщений.</summary>
        [DataMember(Name = "clash")] public string Clash { get; set; }
        [DataMember(Name = "elements")] public List<ShowElement> Elements { get; set; } = new List<ShowElement>();
    }

    [DataContract]
    internal sealed class ShowElement
    {
        /// <summary>Файл модели из отчёта: «…_AR01_01_M3.nwc».</summary>
        [DataMember(Name = "model")] public string Model { get; set; }
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
    }

    /// <summary>
    /// Ответ на «показать»: Ok — вид открыт (Warnings — что не так, но показать удалось); иначе Error — почему нельзя.
    /// Pending — Revit занят: коллизия откроется, когда он освободится.
    /// </summary>
    [DataContract]
    internal sealed class ShowReply
    {
        [DataMember(Name = "version")] public int Version { get; set; } = BridgeProtocol.Version;
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "pending")] public bool Pending { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "warnings")] public List<string> Warnings { get; set; } = new List<string>();

        public static ShowReply Fail(string error) => new ShowReply { Error = error };
    }
}
