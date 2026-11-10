using System.Text.Json.Nodes;

namespace RaceTrace.Api.Infrastructure;

public interface ISupabaseRestClient
{
    Task<SupabaseQueryResult> GetAsync(
        SupabaseQuery query,
        bool count = false,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default
    );

    Task<JsonObject?> GetSingleAsync(
        SupabaseQuery query,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<JsonObject>> PostAsync(
        string table,
        JsonNode payload,
        SupabaseRole role,
        string? onConflict = null,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<JsonObject>> PatchAsync(
        string table,
        JsonObject payload,
        SupabaseQuery query,
        SupabaseRole role,
        CancellationToken cancellationToken = default
    );

    Task DeleteAsync(
        string table,
        SupabaseQuery query,
        SupabaseRole role,
        CancellationToken cancellationToken = default
    );

    Task<JsonNode?> RpcAsync(
        string functionName,
        JsonObject payload,
        SupabaseRole role,
        CancellationToken cancellationToken = default
    );
}
