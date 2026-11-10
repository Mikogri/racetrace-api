using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace RaceTrace.Api.Infrastructure.Calculations;

public sealed class CacheCoherenceOptions
{
    // Enable only after applying the revision migration. Disabled preserves legacy deployments.
    public bool Enabled { get; set; }
}

/// <summary>One lightweight revision read per active instance per five seconds.
/// A failed check never extends the lifetime of the previous revision.</summary>
public sealed class DataRevisionService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<CacheCoherenceOptions> options
)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DataRevisionSnapshot? _snapshot;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    public bool Enabled => options.Value.Enabled;

    public async Task<DataRevisionSnapshot> GetAsync(
        CancellationToken token = default,
        bool forceRefresh = false
    )
    {
        if (!Enabled)
            return DataRevisionSnapshot.Unversioned;
        await _gate.WaitAsync(token);
        try
        {
            if (
                !forceRefresh
                && _snapshot is not null
                && time.GetUtcNow() - _checkedAt < TimeSpan.FromSeconds(5)
            )
                return _snapshot;
            var startedAt = time.GetUtcNow();
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ISupabaseRestClient>();
            var payload = await client.RpcAsync(
                "get_calculation_revisions",
                new JsonObject(),
                SupabaseRole.Service,
                token
            );
            if (payload is not JsonObject obj || obj["all"] is null || obj["global"] is null)
                throw new ApiException(
                    503,
                    "REVISION_UNAVAILABLE",
                    "Cannot verify the current data revision."
                );
            var revisions = obj.ToDictionary(
                pair => pair.Key,
                pair => pair.Value!.GetValue<long>(),
                StringComparer.Ordinal
            );
            _snapshot = new DataRevisionSnapshot(revisions);
            _checkedAt = startedAt;
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
