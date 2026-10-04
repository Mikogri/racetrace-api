using RaceTrace.Api.Features.Metrics;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class SessionsService
{
    private static readonly IReadOnlyDictionary<string, string> Sorts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionDate"] = "session_date",
            ["sessionType"] = "session_type",
            ["sessionId"] = "id",
            ["roundId"] = "round_id"
        };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;

    public SessionsService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries)
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
    }

    public async Task<PublicPagedResponse<CommonSessionDto>> ListCommonSessionsAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken)
    {
        var page = _validator.Page(queryString);
        var requestedSort = _validator.Sort(queryString, Sorts, "sessionDate", "asc");
        var sortColumn = Sorts[requestedSort.Sort];
        var riderIds = _validator.CsvAny(queryString, "riderId", "rider_id")
            .Select(value =>
            {
                if (!long.TryParse(value, out var parsed) || parsed <= 0)
                {
                    throw _validator.BadQuery("riderId must contain positive integer identifiers.");
                }

                return parsed.ToString();
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var commonOnly = _validator.BoolAny(queryString, "commonSessionsOnly", "common_sessions_only") == true;
        var context = await _domainQueries.ParseMetricsContextAsync(queryString, _validator, cancellationToken);
        var matchingSessions = await _domainQueries.GetMatchingSessionsAsync(context, cancellationToken: cancellationToken);
        var sessionIds = matchingSessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        var participatingBySession = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (sessionIds.Length > 0 && riderIds.Length > 0)
        {
            foreach (var batch in sessionIds.Chunk(100))
            {
                var response = await _supabase.GetAsync(
                    new SupabaseQuery("results")
                        .Select("session_id,rider_id")
                        .In("session_id", batch)
                        .In("rider_id", riderIds)
                        .Limit(5000),
                    cancellationToken: cancellationToken);
                foreach (var result in response.Data)
                {
                    var sessionId = result.String("session_id");
                    var riderId = result.String("rider_id");
                    if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(riderId))
                    {
                        continue;
                    }

                    if (!participatingBySession.TryGetValue(sessionId, out var participants))
                    {
                        participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        participatingBySession[sessionId] = participants;
                    }

                    participants.Add(riderId);
                }
            }
        }

        var filteredSessionIds = matchingSessions
            .Where(session =>
            {
                if (!commonOnly || riderIds.Length < 2)
                {
                    return true;
                }

                return participatingBySession.TryGetValue(session.String("id") ?? string.Empty, out var participants) &&
                    riderIds.All(participants.Contains);
            })
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        IReadOnlyList<JsonObject> fullSessions = [];
        if (filteredSessionIds.Length > 0)
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("sessions")
                    .Select(DomainQueryService.SessionSelect)
                    .In("id", filteredSessionIds)
                    .Order(sortColumn, requestedSort.Descending)
                    .Limit(5000),
                cancellationToken: cancellationToken);
            DomainQueryService.NormalizeSessionEmbeds(response.Data);
            fullSessions = response.Data;
        }

        var items = fullSessions
            .Skip(page.Offset)
            .Take(page.Limit)
            .Select(session =>
            {
                var sessionId = session.String("id") ?? string.Empty;
                var round = session.Object("rounds");
                var participants = participatingBySession.GetValueOrDefault(sessionId) ?? [];
                return new CommonSessionDto(
                    PublicDtoMapping.Long(sessionId),
                    PublicDtoMapping.Long(session.String("round_id") ?? round?.String("id")),
                    PublicApiCodes.ToPublicSessionType(session.String("session_type")),
                    PublicApiCodes.ToPublicCategory(session.Object("categories")?.String("code")),
                    PublicDtoMapping.Date(session.String("session_date")),
                    participants.Select(PublicDtoMapping.Long).OrderBy(id => id).ToArray());
            })
            .ToArray();

        return PublicPagedResponse<CommonSessionDto>.Create(
            items,
            page.Page,
            page.Limit,
            fullSessions.Count);
    }
}
