using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class PointsService
{
    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly IPointAwardService _pointAwards;

    public PointsService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        IPointAwardService pointAwards
    )
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _pointAwards = pointAwards;
    }

    public async Task<ListResponse<JsonObject>> ListSystemsAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var query = new SupabaseQuery("points_systems").Select(
            "*,competition_levels(*),categories(*),point_system_stages(*,point_rules(*))"
        );

        var seasonYear = _validator.Int(queryString, "season_year");
        query.Filter("valid_from_season", "lte", seasonYear);
        query.Filter("valid_to_season", "gte", seasonYear);
        query.Filter("discipline", "eq", _validator.String(queryString, "discipline"));

        var categoryCode = _validator.String(queryString, "category_code");
        if (!string.IsNullOrWhiteSpace(categoryCode))
        {
            var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(
                categoryCode,
                cancellationToken: cancellationToken
            );
            if (categoryId is null)
            {
                return ResponseFactory.Page(Array.Empty<JsonObject>(), 1, 1, 0, "id", "asc");
            }

            query.Filter("category_id", "eq", categoryId);
        }

        var competitionClassCode = _validator.String(queryString, "competition_class_code");
        if (!string.IsNullOrWhiteSpace(competitionClassCode))
        {
            var classId = await _domainQueries.FindCompetitionClassIdByCodeAsync(
                competitionClassCode,
                cancellationToken: cancellationToken
            );
            if (classId is null)
            {
                return ResponseFactory.Page(Array.Empty<JsonObject>(), 1, 1, 0, "id", "asc");
            }

            query.Filter("competition_level_id", "eq", classId);
        }

        var sessionType = _validator.String(queryString, "session_type");
        if (!string.IsNullOrWhiteSpace(sessionType))
        {
            var stages = await _supabase.GetAsync(
                new SupabaseQuery("point_system_stages")
                    .Select("point_system_id")
                    .Filter(
                        "session_type",
                        "eq",
                        ResultEmbedMapping.NormalizeSessionType(sessionType)
                    ),
                cancellationToken: cancellationToken
            );
            var systemIds = stages
                .Data.Select(stage => stage.String("point_system_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (systemIds.Length == 0)
            {
                return ResponseFactory.Page(Array.Empty<JsonObject>(), 1, 1, 0, "id", "asc");
            }

            query.In("id", systemIds);
        }

        var response = await _supabase.GetAsync(
            query,
            count: true,
            cancellationToken: cancellationToken
        );
        foreach (var system in response.Data)
        {
            NormalizeCompetitionClass(system);
        }
        return ResponseFactory.Page(
            response.Data,
            1,
            Math.Max(1, response.Data.Count),
            response.Total,
            "id",
            "asc"
        );
    }

    public async Task<JsonObject> RecalculateAsync(
        PointsRecalculateRequest request,
        CancellationToken cancellationToken
    )
    {
        var recalculated = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            await TryRecalculateSessionAsync(request.SessionId, recalculated, cancellationToken);
        }
        else
        {
            var context = new RiderMetricsContext(
                request.SeasonYear,
                request.CategoryCode,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null
            );
            var sessionIds = await _domainQueries.ResolveSessionIdsAsync(
                context,
                SupabaseRole.Service,
                cancellationToken
            );
            foreach (var sessionId in sessionIds)
            {
                await TryRecalculateSessionAsync(sessionId, recalculated, cancellationToken);
            }
        }

        return new JsonObject
        {
            ["scope"] = request.Scope ?? "world_cup_overall",
            ["sessions_recalculated"] = recalculated.Count,
            ["session_ids"] = recalculated,
        };
    }

    public async Task<JsonObject> RefreshRankingPreaggregationsAsync(
        RankingPreaggregationRefreshRequest request,
        CancellationToken cancellationToken
    )
    {
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? "missing";
        if (mode is not ("missing" or "all" or "incidents"))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PREAGGREGATION_MODE",
                "mode must be missing, all or incidents."
            );
        }

        var context = new RiderMetricsContext(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );
        var allSessionIds = await _domainQueries.ResolveSessionIdsAsync(
            context,
            SupabaseRole.Service,
            cancellationToken
        );
        var sessionsWithResults = await SessionIdsWithResultsAsync(
            allSessionIds,
            cancellationToken
        );
        var existing = await ExistingPreaggregationIdsAsync(sessionsWithResults, cancellationToken);
        var targets = mode switch
        {
            "missing" => sessionsWithResults.Where(id => !existing.Contains(id)).ToArray(),
            "incidents" => sessionsWithResults.Where(existing.Contains).ToArray(),
            _ => sessionsWithResults.ToArray(),
        };

        var refreshed = new JsonArray();
        var failed = new JsonArray();
        foreach (var sessionId in targets)
        {
            try
            {
                var preparation = new RankingPreaggregationService(_supabase);
                if (
                    mode == "incidents"
                        ? await preparation.PrepareIncidentsAsync(sessionId, cancellationToken)
                        : await preparation.RefreshAsync(sessionId, cancellationToken)
                )
                    refreshed.Add(sessionId);
                else
                    failed.Add(sessionId);
            }
            catch (SupabaseApiException)
            {
                failed.Add(sessionId);
            }
        }

        var presentAfterRefresh = await ExistingPreaggregationIdsAsync(
            sessionsWithResults,
            cancellationToken
        );
        var stillMissing = sessionsWithResults
            .Where(id => !presentAfterRefresh.Contains(id))
            .Select(id => (JsonNode?)JsonValue.Create(id))
            .ToArray();

        return new JsonObject
        {
            ["mode"] = mode,
            ["sessions_with_results"] = sessionsWithResults.Count,
            ["sessions_requested"] = targets.Length,
            ["sessions_refreshed"] = refreshed.Count,
            ["sessions_failed"] = failed.Count,
            ["session_ids"] = refreshed,
            ["failed_session_ids"] = failed,
            ["still_missing_session_ids"] = new JsonArray(stillMissing),
        };
    }

    private async Task<IReadOnlyList<string>> SessionIdsWithResultsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken
    )
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in sessionIds.Chunk(10))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("results").Select("session_id").In("session_id", batch),
                role: SupabaseRole.Service,
                cancellationToken: cancellationToken
            );
            foreach (var row in response.Data)
            {
                if (row.String("session_id") is { Length: > 0 } id)
                {
                    found.Add(id);
                }
            }
        }

        return found
            .OrderBy(id => long.TryParse(id, out var parsed) ? parsed : long.MaxValue)
            .ToArray();
    }

    private async Task<HashSet<string>> ExistingPreaggregationIdsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken
    )
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in sessionIds.Chunk(100))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("ranking_session_preaggregates")
                    .Select("session_id")
                    .Filter("input_splits", "not.is", "null")
                    .In("session_id", batch),
                role: SupabaseRole.Service,
                cancellationToken: cancellationToken
            );
            foreach (var row in response.Data)
            {
                if (row.String("session_id") is { Length: > 0 } id)
                {
                    found.Add(id);
                }
            }
        }

        return found;
    }

    private async Task TryRecalculateSessionAsync(
        string sessionId,
        JsonArray recalculated,
        CancellationToken cancellationToken
    )
    {
        var result = await _pointAwards.RecalculateSessionAsync(sessionId, cancellationToken);
        await new RankingPreaggregationService(_supabase).RefreshAsync(
            sessionId,
            cancellationToken
        );
        recalculated.Add(
            new JsonObject
            {
                ["session_id"] = sessionId,
                ["point_system_applied"] = result.PointSystemApplied,
                ["results_processed"] = result.ResultsProcessed,
                ["point_rows_written"] = result.PointRowsWritten,
                ["total_points"] = result.TotalPoints,
                ["point_system_code"] = result.PointSystemCode,
                ["warning"] = result.Warning,
            }
        );
    }

    public async Task<ListResponse<JsonObject>> ListCompetitionClassesAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var query = new SupabaseQuery("competition_levels").Select("*").Order("priority", false);
        query.Filter("is_active", "eq", _validator.Bool(queryString, "is_active"));
        var response = await _supabase.GetAsync(
            query,
            count: true,
            cancellationToken: cancellationToken
        );
        return ResponseFactory.Page(
            response.Data,
            1,
            Math.Max(1, response.Data.Count),
            response.Total,
            "priority",
            "asc"
        );
    }

    private static void NormalizeCompetitionClass(JsonObject row)
    {
        var competitionClass = row.Object("competition_levels");
        row["competition_class_id"] =
            row.String("competition_level_id") ?? competitionClass?.String("id");
        row["competition_class"] = competitionClass?.DeepClone();
        row.Remove("competition_level_id");
        row.Remove("competition_levels");
        row.Remove("competition_rank_id");
    }
}
