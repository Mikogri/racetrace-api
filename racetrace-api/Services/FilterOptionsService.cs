using System.Text.Json;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class FilterOptionsService(ISupabaseRestClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    public async Task<FilterOptionsDto> GetAsync(CancellationToken cancellationToken)
    {
        var payload = await client.RpcAsync("get_filter_options", new(), SupabaseRole.Anon, cancellationToken);
        return payload?.Deserialize<FilterOptionsDto>(JsonOptions)
            ?? throw new JsonException("The filter options RPC returned no dictionaries.");
    }
}
