using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class TeamsService
{
    private const int PageSize = 1000;
    private const int FilterBatchSize = 100;

    private static readonly Dictionary<string, string> TeamSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["name"] = "name",
        ["code"] = "code"
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly MetricsService _metrics;

    public TeamsService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        MetricsService metrics)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _metrics = metrics;
    }

    public async Task<ListResponse<JsonObject>> ListTeamsAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, TeamSorts, "name", "asc");
        var query = new SupabaseQuery("teams")
            .Select("*")
            .Order(TeamSorts[sort.Sort], sort.Descending)
            .Limit(page.Limit)
            .Offset(page.Offset);

        query.Filter("code", "eq", _validator.String(queryString, "code")?.ToUpperInvariant());

        var q = _validator.String(queryString, "q");
        if (!string.IsNullOrWhiteSpace(q))
        {
            var safe = q.Replace("*", string.Empty, StringComparison.Ordinal);
            query.Or($"name.ilike.*{safe}*,code.ilike.*{safe}*");
        }

        var response = await _supabase.GetAsync(query, count: true, cancellationToken: cancellationToken);
        await DecorateTeamsAsync(response.Data, cancellationToken);
        return ResponseFactory.Page(response.Data, page.Page, page.Limit, response.Total, sort.Sort, sort.Order);
    }

    public async Task<JsonObject> GetTeamAsync(string teamId, CancellationToken cancellationToken)
    {
        var team = await _domainQueries.FindByIdAsync("teams", teamId, cancellationToken: cancellationToken);
        if (team is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "TEAM_NOT_FOUND", "Team was not found.");
        }

        await DecorateTeamsAsync([team], cancellationToken);
        return team;
    }

    private async Task DecorateTeamsAsync(IReadOnlyList<JsonObject> teams, CancellationToken cancellationToken)
    {
        var teamIds = teams
            .Select(team => team.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (teamIds.Length == 0)
        {
            return;
        }

        var currentAssignments = await FetchCurrentAssignmentsAsync(
            teamIds,
            cancellationToken);
        var riderIds = currentAssignments
            .Select(assignment => assignment.String("rider_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allRiderResults = await FetchResultsAsync("rider_id", riderIds, cancellationToken);
        var context = new RiderMetricsContext(null, null, null, null, null, null, null, null, null, null, null);
        var performanceByRiderId = await _metrics.CalculatePerformanceByRiderAsync(allRiderResults, context, cancellationToken);

        DecorateTeamsFromHistory(
            teams,
            currentAssignments,
            allRiderResults,
            performanceByRiderId);
    }

    internal static void DecorateTeamsFromHistory(
        IReadOnlyList<JsonObject> teams,
        IReadOnlyList<JsonObject> currentAssignments,
        IReadOnlyList<JsonObject> allRiderResults,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId)
    {
        var resultsByRiderId = allRiderResults
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JsonObject>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var riderIdsByTeamId = currentAssignments
            .Where(assignment =>
                !string.IsNullOrWhiteSpace(assignment.String("team_id")) &&
                !string.IsNullOrWhiteSpace(assignment.String("rider_id")))
            .GroupBy(assignment => assignment.String("team_id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(assignment => assignment.String("rider_id")!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var team in teams)
        {
            var teamId = team.String("id") ?? string.Empty;
            var riderIds = riderIdsByTeamId.GetValueOrDefault(teamId) ?? [];
            var riders = riderIds
                .Where(resultsByRiderId.ContainsKey)
                .Select(riderId => BuildTeamRider(
                    riderId,
                    resultsByRiderId[riderId],
                    performanceByRiderId))
                .OrderBy(rider => rider.String("last_name"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(rider => rider.String("first_name"), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ratings = riders
                .Select(rider => rider.Decimal("rating"))
                .Where(rating => rating is not null)
                .Select(rating => rating!.Value)
                .ToArray();

            team["riders"] = new JsonArray(riders.Select(rider => (JsonNode)rider).ToArray());
            team["riders_count"] = riders.Length;
            team["average_rating"] = ratings.Length == 0
                ? null
                : Math.Round(ratings.Average(), 2);
        }
    }

    internal static void DecorateTeams(
        IReadOnlyList<JsonObject> teams,
        IReadOnlyList<JsonObject> teamResults,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId)
    {
        var resultsByTeamId = teamResults
            .GroupBy(result => result.String("team_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<JsonObject>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var team in teams)
        {
            var teamId = team.String("id") ?? string.Empty;
            var results = resultsByTeamId.TryGetValue(teamId, out var matchingResults)
                ? matchingResults
                : [];
            var riders = results
                .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")) && result.Object("riders") is not null)
                .GroupBy(result => result.String("rider_id")!, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildTeamRider(group.Key, group.ToArray(), performanceByRiderId))
                .OrderBy(rider => rider.String("last_name"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(rider => rider.String("first_name"), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ratings = riders
                .Select(rider => rider.Decimal("rating"))
                .Where(rating => rating is not null)
                .Select(rating => rating!.Value)
                .ToArray();

            team["riders"] = new JsonArray(riders.Select(rider => (JsonNode)rider).ToArray());
            team["riders_count"] = riders.Length;
            team["average_rating"] = ratings.Length == 0
                ? null
                : Math.Round(ratings.Average(), 2);
        }
    }

    private static JsonObject BuildTeamRider(
        string riderId,
        IReadOnlyList<JsonObject> results,
        IReadOnlyDictionary<string, RiderPerformanceMetricValues> performanceByRiderId)
    {
        var rider = results.Select(result => result.Object("riders")).First(rider => rider is not null)!;
        var categories = results
            .Select(result => result.Object("sessions")?.Object("categories"))
            .Where(category => category is not null)
            .Select(category => category!)
            .GroupBy(
                category => category.String("id") ?? category.String("code") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group.First())
            .OrderBy(category => category.String("code"), StringComparer.OrdinalIgnoreCase)
            .Select(category => (JsonNode)new JsonObject
            {
                ["id"] = category.String("id"),
                ["code"] = category.String("code"),
                ["name"] = category.String("name")
            })
            .ToArray();
        var rating = performanceByRiderId.TryGetValue(riderId, out var performance)
            ? performance.Rating
            : null;

        return new JsonObject
        {
            ["id"] = riderId,
            ["uci_id"] = rider.String("uci_id"),
            ["first_name"] = rider.String("first_name"),
            ["last_name"] = rider.String("last_name"),
            ["display_name"] = rider.DisplayName(),
            ["rating"] = rating,
            ["raw_rating"] = performance?.RawRating,
            ["pace"] = performance?.Pace,
            ["raw_pace"] = performance?.RawPace,
            ["consistency"] = performance?.Consistency,
            ["result_score"] = performance?.ResultScore,
            ["categories"] = new JsonArray(categories)
        };
    }

    private async Task<IReadOnlyList<JsonObject>> FetchResultsAsync(
        string filterColumn,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = new List<JsonObject>();
        foreach (var batch in ids.Chunk(FilterBatchSize))
        {
            var offset = 0;
            while (true)
            {
                var response = await _supabase.GetAsync(
                    new SupabaseQuery("results")
                        .Select(DomainQueryService.ResultSelect)
                        .In(filterColumn, batch)
                        .Order("id", descending: false)
                        .Limit(PageSize)
                        .Offset(offset),
                    count: true,
                    cancellationToken: cancellationToken);
                DomainQueryService.NormalizeResultEmbeds(response.Data);
                rows.AddRange(response.Data);

                if (response.Data.Count == 0 ||
                    response.Data.Count < PageSize ||
                    (response.Total.HasValue && offset + response.Data.Count >= response.Total.Value))
                {
                    break;
                }

                offset += response.Data.Count;
            }
        }

        return rows;
    }

    private async Task<IReadOnlyList<JsonObject>> FetchCurrentAssignmentsAsync(
        IReadOnlyList<string> teamIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<JsonObject>();
        foreach (var batch in teamIds.Chunk(FilterBatchSize))
        {
            var offset = 0;
            while (true)
            {
                var response = await _supabase.GetAsync(
                    new SupabaseQuery("rider_team_history")
                        .Select("rider_id,team_id,valid_from")
                        .In("team_id", batch)
                        .Filter("valid_to", "is", "null")
                        .Order("valid_from", descending: true)
                        .Limit(PageSize)
                        .Offset(offset),
                    cancellationToken: cancellationToken);
                rows.AddRange(response.Data);
                if (response.Data.Count < PageSize)
                {
                    break;
                }

                offset += response.Data.Count;
            }
        }

        return rows;
    }
}
