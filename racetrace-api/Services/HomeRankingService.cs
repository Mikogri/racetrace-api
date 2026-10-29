using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class HomeRankingService(RankingService ranking)
{
    public async Task<HomeRankingResponseDto> GetAsync(IQueryCollection query, CancellationToken cancellationToken)
    {
        var filters = new Dictionary<string, StringValues>
        {
            ["season"] = query["season"],
            ["category"] = query["category"],
            ["page"] = "1",
            ["pageSize"] = "5",
            ["sortDirection"] = "desc",
            ["sortBy"] = "rating"
        };
        // Only the projection changes: both lists reuse the same cached ranking snapshot.
        var topRated = await ranking.ListRankingAsync(new QueryCollection(filters), cancellationToken);
        filters["sortBy"] = "totalPoints";
        var standings = await ranking.ListRankingAsync(new QueryCollection(filters), cancellationToken);
        return new HomeRankingResponseDto(topRated, standings);
    }
}
