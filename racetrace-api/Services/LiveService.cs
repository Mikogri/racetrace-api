using RaceTrace.Api.Features.Metrics;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class LiveService
{
    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly ResultsService _results;

    private static readonly Dictionary<string, string> SessionSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["session_date"] = "session_date",
        ["category"] = "category_id",
        ["name"] = "name",
        ["status"] = "status"
    };

    public LiveService(ISupabaseRestClient supabase, QueryValidationService validator, DomainQueryService domainQueries, ResultsService results)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _results = results;
    }

    public async Task<ListResponse<JsonObject>> ListLiveSessionsAsync(IQueryCollection queryString, CancellationToken cancellationToken)
    {
        var sort = _validator.Sort(queryString, SessionSorts, "session_date", "asc");
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var sessionIds = await _domainQueries.ResolveSessionIdsAsync(context, cancellationToken: cancellationToken);
        var hasFilters = context.SeasonYears.Count > 0 ||
            context.SeasonYear is not null ||
            context.FromDate is not null ||
            context.ToDate is not null ||
            !string.IsNullOrWhiteSpace(context.RoundId) ||
            context.TrackIds.Count > 0 ||
            !string.IsNullOrWhiteSpace(context.TrackId) ||
            context.ConditionIds.Count > 0 ||
            !string.IsNullOrWhiteSpace(context.CountryCode) ||
            !string.IsNullOrWhiteSpace(context.Venue) ||
            !string.IsNullOrWhiteSpace(context.TrackName) ||
            !string.IsNullOrWhiteSpace(context.CompetitionClassCode) ||
            !string.IsNullOrWhiteSpace(context.CategoryCode) ||
            !string.IsNullOrWhiteSpace(context.SessionType);

        if (hasFilters && sessionIds.Count == 0)
        {
            return ResponseFactory.Page(Array.Empty<JsonObject>(), 1, 30, 0, sort.Sort, sort.Order);
        }

        var query = new SupabaseQuery("sessions")
            .Select(DomainQueryService.SessionSelect)
            .Order(SessionSorts[sort.Sort], sort.Descending);

        if (sessionIds.Count > 0)
        {
            query.In("id", sessionIds);
        }

        var requestedStatus = _validator.String(queryString, "status") ?? "live";
        if (!string.Equals(requestedStatus, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(requestedStatus, "live", StringComparison.OrdinalIgnoreCase))
            {
                query.In("status", ["live", "active", "running"]);
            }
            else
            {
                query.Filter("status", "eq", requestedStatus);
            }
        }
        var response = await _supabase.GetAsync(query, count: true, cancellationToken: cancellationToken);
        DomainQueryService.NormalizeSessionEmbeds(response.Data);
        return ResponseFactory.Page(response.Data, 1, Math.Max(1, response.Data.Count), response.Total, sort.Sort, sort.Order);
    }

    public async Task<JsonObject> GetLiveSessionAsync(string sessionId, string[] include, CancellationToken cancellationToken)
    {
        var session = await _domainQueries.FindByIdAsync("sessions", sessionId, DomainQueryService.SessionSelect, cancellationToken: cancellationToken);
        if (session is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "SESSION_NOT_FOUND", "Session was not found.");
        }

        DomainQueryService.NormalizeSessionEmbed(session);

        if (include.Contains("leaderboard", StringComparer.OrdinalIgnoreCase))
        {
            var leaderboard = await _results.ListSessionResultsAsync(sessionId, new QueryCollection(), cancellationToken);
            session["leaderboard"] = new JsonArray(leaderboard.Data.Select(row => row.DeepClone()).ToArray());
        }

        return session;
    }

    public Task<ListResponse<JsonObject>> GetLeaderboardAsync(string sessionId, IQueryCollection queryString, CancellationToken cancellationToken)
    {
        return _results.ListSessionResultsAsync(sessionId, queryString, cancellationToken);
    }
}
