using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public static class JsonSerializerProxy
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResponseOptions = CreateResponseOptions();

    // Keep DTOs typed until the HTTP writer runs, including dynamic nested field normalization.
    public static JsonValue? ToJsonValue<T>(T value) =>
        JsonValue.Create(value, (JsonTypeInfo<T>)ResponseOptions.GetTypeInfo(typeof(T)));

    private static JsonSerializerOptions CreateResponseOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ApiJsonNodeConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static JsonObject ToJsonObject<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options)?.AsObject() ?? [];
    }
}
