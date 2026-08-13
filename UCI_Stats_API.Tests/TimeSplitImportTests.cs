using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class TimeSplitImportTests
{
    [Fact]
    public async Task EmptyChronoRaceTimesPreserveExistingSplits()
    {
        var supabase = new TimeSplitSupabaseClient();
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());

        var imported = await service.ReplaceTimeSplitsAsync(
            "result-1",
            new JsonArray(),
            CancellationToken.None);

        Assert.Equal(0, imported);
        Assert.Equal(0, supabase.DeleteCount);
        Assert.Equal(0, supabase.PostCount);
    }

    [Fact]
    public async Task ChronoRaceTimesReplaceExistingSplits()
    {
        var supabase = new TimeSplitSupabaseClient();
        var service = new ChronoRaceImportService(supabase, null!, new TestPointAwardService());
        var times = new JsonArray
        {
            new JsonObject { ["RaceTime"] = 45_000, ["Speed"] = 42.5m },
            new JsonObject { ["RaceTime"] = 65_000, ["Speed"] = 47.25m }
        };

        var imported = await service.ReplaceTimeSplitsAsync(
            "result-1",
            times,
            CancellationToken.None);

        Assert.Equal(2, imported);
        Assert.Equal(1, supabase.DeleteCount);
        Assert.Equal(1, supabase.PostCount);

        var rows = Assert.IsType<JsonArray>(supabase.PostedPayload);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]!["split_index"]!.GetValue<int>());
        Assert.Equal(45_000, rows[0]!["race_time"]!.GetValue<int>());
        Assert.Equal(2, rows[1]!["split_index"]!.GetValue<int>());
        Assert.Equal(65_000, rows[1]!["race_time"]!.GetValue<int>());
    }

    private sealed class TimeSplitSupabaseClient : ISupabaseRestClient
    {
        public int DeleteCount { get; private set; }

        public int PostCount { get; private set; }

        public JsonNode? PostedPayload { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("time_splits", table);
            PostCount++;
            PostedPayload = payload.DeepClone();
            return Task.FromResult<IReadOnlyList<JsonObject>>([]);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("time_splits", table);
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
