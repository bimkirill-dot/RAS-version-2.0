using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RASv2.ModelHistory.Storage
{
    public static class SpyJson
    {
        /// <summary>Компактный формат строк журнала (JSONL).</summary>
        public static readonly JsonSerializerOptions Line = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = false,
            Converters = { new JsonStringEnumConverter() }
        };

        public static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
