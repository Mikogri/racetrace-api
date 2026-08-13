using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public interface IPointAwardService
{
    Task<PointAwardCalculationResult> RecalculateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class PointAwardService : IPointAwardService
{
    private const int WriteBatchSize = 500;

    private readonly ISupabaseRestClient _supabase;

    public PointAwardService(ISupabaseRestClient supabase)
    {
        _supabase = supabase;
    }

    public async Task<PointAwardCalculationResult> RecalculateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _supabase.GetSingleAsync(
            new SupabaseQuery("sessions")
                .Select(DomainQueryService.SessionSelect)
                .Filter("id", "eq", sessionId),
            SupabaseRole.Service,
            cancellationToken);
        if (session is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "SESSION_NOT_FOUND",
                $"Session '{sessionId}' was not found.");
        }

        DomainQueryService.NormalizeSessionEmbed(session);
        var round = session.Object("rounds");
        var category = session.Object("categories");
        var seasonYear = round?.Int("season_year");
        var competitionClassId = round?.String("competition_class_id");
        var categoryId = session.String("category_id") ?? category?.String("id");
        var sessionType = session.String("session_type");
        if (seasonYear is null ||
            string.IsNullOrWhiteSpace(competitionClassId) ||
            string.IsNullOrWhiteSpace(categoryId) ||
            string.IsNullOrWhiteSpace(sessionType))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "POINT_CONTEXT_INCOMPLETE",
                "The session must have a season, competition class, category, and session type before points can be calculated.");
        }

        var results = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("id,position,is_classified,status")
                .Filter("session_id", "eq", sessionId)
                .Order("id", false),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);
        var resultIds = results.Data
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        var systems = await _supabase.GetAsync(
            new SupabaseQuery("points_systems")
                .Select("id,code,name")
                .Filter("competition_level_id", "eq", competitionClassId)
                .Filter("category_id", "eq", categoryId)
                .Filter("discipline", "eq", "DHI")
                .Filter("valid_from_season", "lte", seasonYear.Value)
                .Filter("valid_to_season", "gte", seasonYear.Value)
                .Order("valid_from_season", true)
                .Limit(2),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);
        if (systems.Data.Count == 0)
        {
            await DeleteExistingPointsAsync(resultIds, cancellationToken);
            return NoSystem(sessionId, results.Data.Count, "No matching point system was found.");
        }

        if (systems.Data.Count > 1)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "AMBIGUOUS_POINT_SYSTEM",
                $"More than one point system matches session '{sessionId}'.");
        }

        var system = systems.Data[0];
        var pointSystemId = system.String("id")!;
        var normalizedSessionType = DomainQueryService.NormalizeSessionType(sessionType);
        var stage = await _supabase.GetSingleAsync(
            new SupabaseQuery("point_system_stages")
                .Select("id,point_system_id,session_type,awards_points,awards_points_on_season_final_round")
                .Filter("point_system_id", "eq", pointSystemId)
                .Filter("session_type", "eq", normalizedSessionType),
            SupabaseRole.Service,
            cancellationToken);
        if (stage is null)
        {
            await DeleteExistingPointsAsync(resultIds, cancellationToken);
            return NoSystem(
                sessionId,
                results.Data.Count,
                $"Point system '{system.String("code")}' has no stage for session type '{normalizedSessionType}'.",
                system.String("code"));
        }

        var rulesResponse = await _supabase.GetAsync(
            new SupabaseQuery("point_rules")
                .Select("id,point_system_stage_id,position_from,position_to,points")
                .Filter("point_system_stage_id", "eq", stage.String("id"))
                .Order("position_from", false),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);
        EnsureRulesDoNotOverlap(rulesResponse.Data, system.String("code") ?? pointSystemId, normalizedSessionType);
        await DeleteExistingPointsAsync(resultIds, cancellationToken);

        var awardsPoints = stage.Bool("awards_points") == true &&
            (round?.Bool("is_season_final_round") != true ||
             stage.Bool("awards_points_on_season_final_round") == true);
        var rows = new List<JsonObject>(results.Data.Count);
        var totalPoints = 0;
        foreach (var result in results.Data)
        {
            var position = IsClassified(result) ? result.Int("position") : null;
            var rule = awardsPoints && position is > 0
                ? FindRule(rulesResponse.Data, position.Value)
                : null;
            var points = rule?.Int("points") ?? 0;
            totalPoints += points;
            rows.Add(new JsonObject
            {
                ["result_id"] = result.String("id"),
                ["points_system_id"] = pointSystemId,
                ["point_system_stage_id"] = stage.String("id"),
                ["point_rule_id"] = rule?.String("id"),
                ["position_used"] = position,
                ["points_awarded"] = points,
                ["calculated_at"] = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        foreach (var batch in rows.Chunk(WriteBatchSize))
        {
            await _supabase.PostAsync(
                "result_points",
                new JsonArray(batch.Select(row => (JsonNode)row).ToArray()),
                SupabaseRole.Service,
                onConflict: "result_id,points_system_id",
                cancellationToken);
        }

        return new PointAwardCalculationResult(
            sessionId,
            PointSystemApplied: true,
            results.Data.Count,
            rows.Count,
            totalPoints,
            system.String("code"),
            Warning: null);
    }

    private async Task DeleteExistingPointsAsync(
        IReadOnlyList<string> resultIds,
        CancellationToken cancellationToken)
    {
        if (resultIds.Count == 0)
        {
            return;
        }

        await _supabase.DeleteAsync(
            "result_points",
            new SupabaseQuery("result_points").In("result_id", resultIds),
            SupabaseRole.Service,
            cancellationToken);
    }

    private static PointAwardCalculationResult NoSystem(
        string sessionId,
        int resultsProcessed,
        string warning,
        string? pointSystemCode = null)
    {
        return new PointAwardCalculationResult(
            sessionId,
            PointSystemApplied: false,
            resultsProcessed,
            PointRowsWritten: 0,
            TotalPoints: 0,
            pointSystemCode,
            warning);
    }

    private static JsonObject? FindRule(IReadOnlyList<JsonObject> rules, int position)
    {
        return rules.FirstOrDefault(rule =>
            rule.Int("position_from") is { } from &&
            position >= from &&
            (rule.Int("position_to") is not { } to || position <= to));
    }

    private static void EnsureRulesDoNotOverlap(
        IReadOnlyList<JsonObject> rules,
        string systemCode,
        string sessionType)
    {
        var ordered = rules
            .OrderBy(rule => rule.Int("position_from") ?? int.MaxValue)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previousTo = ordered[index - 1].Int("position_to");
            var currentFrom = ordered[index].Int("position_from");
            if (previousTo is null || currentFrom is null || currentFrom <= previousTo)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "OVERLAPPING_POINT_RULES",
                    $"Point system '{systemCode}' contains overlapping rules for stage '{sessionType}'.");
            }
        }
    }

    private static bool IsClassified(JsonObject result)
    {
        var status = result.String("status");
        return result.Int("position") is > 0 &&
            result.Bool("is_classified") == true &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }
}
