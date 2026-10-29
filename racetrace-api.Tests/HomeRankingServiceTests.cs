using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Models;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class HomeRankingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleMatchesBothRankingResponsesUsingTheSameCachedSnapshot(bool empty)
    {
        var validator = new QueryValidationService();
        var domain = new DomainQueryService(null!);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var query = Query(new() { ["season"] = "2026", ["category"] = "elite_men" });
        var context = await domain.ParseMetricsContextAsync(query, validator, default);
        var rows = empty ? [] : Enumerable.Range(1, 8).Select(id => new JsonObject
        {
            ["rider_id"] = id, ["points"] = id * 20, ["rating"] = id == 1 ? null : id == 3 ? 98 : 100 - id,
            ["confidence"] = 0.8m, ["pace"] = 96m, ["consistency"] = 88m,
            ["rider"] = new JsonObject { ["first_name"] = "Test", ["last_name"] = $"Rider {id}", ["nation"] = "POL" }
        }).ToArray();
        var snapshot = new RankingSnapshot(rows, [], [], [], []);
        cache.Set(RankingService.BuildSingleFlightKey(context, null, null, null), snapshot);
        var ranking = new RankingService(validator, domain, null!, new RankingSingleFlight(), cache);
        var home = new HomeRankingService(ranking);

        var actual = await home.GetAsync(Query(new()
        {
            ["season"] = "2026", ["category"] = "elite_men",
            ["page"] = "2", ["pageSize"] = "150", ["comparison"] = "previousRound",
            ["search"] = "absent", ["refresh"] = "1"
        }), default);

        foreach (var sort in new[] { "rating", "totalPoints" })
        {
            var expected = await ranking.ListRankingAsync(Query(new()
            {
                ["season"] = "2026", ["category"] = "elite_men", ["page"] = "1",
                ["pageSize"] = "5", ["sortBy"] = sort, ["sortDirection"] = "desc"
            }), default);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(sort == "rating" ? actual.TopRated : actual.Standings));
        }
        Assert.Same(snapshot, cache.Get<RankingSnapshot>(RankingService.BuildSingleFlightKey(context, null, null, null)));
        Assert.Null(actual.TopRated.ComparisonBasis);
        Assert.Equal(empty ? 0 : 5, actual.TopRated.Items.Count);
        if (!empty)
        {
            Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, actual.TopRated.Items.Select(rider => rider.RiderId));
            Assert.Equal(new long[] { 8, 7, 6, 5, 4 }, actual.Standings.Items.Select(rider => rider.RiderId));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, actual.TopRated.Items.Select(rider => rider.Position));
            Assert.Equal("Test Rider 2", actual.TopRated.Items[0].FullName);
            Assert.Equal(0.8m, actual.TopRated.Items[0].Confidence);
            Assert.Null(actual.TopRated.Items[0].PositionChange);
        }
    }

    [Theory]
    [InlineData("not-a-year")]
    [InlineData("1989")]
    public async Task InvalidSeasonKeepsRankingValidation(string season)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ranking = new RankingService(new QueryValidationService(), new DomainQueryService(null!), null!, new RankingSingleFlight(), cache);
        var error = await Assert.ThrowsAsync<ApiException>(() => new HomeRankingService(ranking).GetAsync(
            Query(new() { ["season"] = season, ["category"] = "elite_men" }), default));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("INVALID_QUERY", error.Code);
    }

    private static QueryCollection Query(Dictionary<string, StringValues> values) => new(values);
}
