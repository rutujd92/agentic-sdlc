using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Infrastructure.Storage;

internal static class OrchestratorJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
