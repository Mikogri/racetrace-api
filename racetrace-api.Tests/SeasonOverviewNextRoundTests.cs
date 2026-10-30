using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class SeasonOverviewNextRoundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextRoundCanComeFromNextSeasonWithoutChangingSeasonStatistics(bool currentUpcoming)
    {
        var client = new RoundClient(currentUpcoming);
        var service = new SeasonSummaryService(client, new QueryValidationService(), new DomainQueryService(client), null!, new FixedTime());
        var result = await service.GetOverviewAsync(2026, default);
        Assert.Equal(currentUpcoming ? 2026 : 2027, result.NextRound!.Season);
        Assert.Equal(2026, result.Season);
        Assert.Equal(1, result.Progress.RoundsCount);
        Assert.Equal(currentUpcoming ? 0 : 1, result.Progress.CompletedRoundsCount);
        Assert.Equal(currentUpcoming ? 1 : 2, client.RoundQueries.Count);
        if (!currentUpcoming)
        {
            Assert.Contains(client.RoundQueries[1].Parameters, p => p.Key == "season_year" && p.Value == "gt.2026");
            Assert.Contains(client.RoundQueries[1].Parameters, p => p.Key == "limit" && p.Value == "1");
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class RoundClient(bool currentUpcoming) : ISupabaseRestClient
    {
        public List<SupabaseQuery> RoundQueries { get; } = [];
        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        {
            if (query.Table != "rounds") return Task.FromResult(new SupabaseQueryResult([], null));
            RoundQueries.Add(query);
            var future = RoundQueries.Count == 2;
            var start = future ? "2027-05-29" : currentUpcoming ? "2026-11-01" : "2026-05-01";
            return Task.FromResult(new SupabaseQueryResult([new JsonObject
            {
                ["id"] = future ? "2" : "1", ["season_year"] = future ? 2027 : 2026,
                ["round_number"] = 1, ["start_date"] = start, ["end_date"] = start,
                ["status"] = future || currentUpcoming ? "scheduled" : "completed"
            }], null));
        }
        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
