using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Imports.ChronoRace;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class ChronoRaceBatchStoreTests
{
    [Fact]
    public async Task LookupsRetainDeduplicationBatchSizeAndServiceRole()
    {
        var client = new RecordingClient();
        var store = new ChronoRaceBatchStore(client);
        var ids = Enumerable
            .Range(1, 201)
            .Select(value => value.ToString())
            .Concat(["", "  ", "1"]);

        await store.GetRowsByValuesAsync("riders", "id", "id", ids, default);

        Assert.Equal(3, client.Reads.Count);
        Assert.All(client.Reads, query => Assert.Equal("riders", query.Table));
        Assert.All(
            client.Reads,
            query =>
                Assert.Contains(new KeyValuePair<string, string>("select", "id"), query.Parameters)
        );
        Assert.All(client.Roles, role => Assert.Equal(SupabaseRole.Service, role));
    }

    [Fact]
    public async Task WritesRetainBatchSizeConflictKeyAndInputOwnership()
    {
        var client = new RecordingClient();
        var store = new ChronoRaceBatchStore(client);
        var rows = Enumerable.Range(1, 1001).Select(id => new JsonObject { ["id"] = id }).ToArray();

        var saved = await store.PostRowsAsync("riders", rows, "uci_id", default);

        Assert.Equal(new[] { 500, 500, 1 }, client.WriteSizes);
        Assert.Equal(1001, saved.Count);
        Assert.All(rows, row => Assert.Null(row.Parent));
        Assert.All(client.ConflictKeys, key => Assert.Equal("uci_id", key));
        Assert.All(client.Roles, role => Assert.Equal(SupabaseRole.Service, role));
    }

    [Fact]
    public async Task EmptyInputsDoNotAccessTheDatabase()
    {
        var client = new RecordingClient();
        var store = new ChronoRaceBatchStore(client);
        Assert.Empty(await store.GetRowsByValuesAsync("riders", "id", "id", [], default));
        Assert.Empty(await store.PostRowsAsync("riders", [], null, default));
        await store.DeleteRowsByValuesAsync("riders", "id", [], default);
        Assert.Empty(client.Roles);
    }

    private sealed class RecordingClient : ISupabaseRestClient
    {
        public List<SupabaseQuery> Reads { get; } = [];
        public List<int> WriteSizes { get; } = [];
        public List<string?> ConflictKeys { get; } = [];
        public List<SupabaseRole> Roles { get; } = [];

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            Reads.Add(query);
            Roles.Add(role);
            return Task.FromResult(new SupabaseQueryResult([], null));
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        )
        {
            var rows = payload.AsArray().OfType<JsonObject>().ToArray();
            WriteSizes.Add(rows.Length);
            ConflictKeys.Add(onConflict);
            Roles.Add(role);
            return Task.FromResult<IReadOnlyList<JsonObject>>(rows);
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
