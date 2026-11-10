using System.Text.Json;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Infrastructure;

internal static class AdminJsonFields
{
    internal static JsonObject? ToDatabasePatch(JsonObject? patch)
    {
        if (patch is null)
            return null;
        var result = new JsonObject();
        foreach (var property in patch)
        {
            var field = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Key);
            if (result.ContainsKey(field))
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "DUPLICATE_ADMIN_FIELD",
                    $"Field '{field}' was provided more than once."
                );
            result[field] = property.Value?.DeepClone();
        }
        return result;
    }

    internal static JsonObject EditableResources(JsonObject resources)
    {
        foreach (var fields in resources.Select(property => property.Value).OfType<JsonArray>())
            for (var index = 0; index < fields.Count; index++)
                fields[index] = ApiJsonNodeConverterFactory.CamelCaseField(
                    fields[index]!.GetValue<string>()
                );
        return resources;
    }
}
