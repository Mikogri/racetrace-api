using System.Text.Json;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Services;

public static class JsonSerializerProxy
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonObject ToJsonObject<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options)?.AsObject() ?? [];
    }
}
