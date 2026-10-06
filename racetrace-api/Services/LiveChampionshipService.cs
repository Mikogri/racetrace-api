using Microsoft.Extensions.Caching.Memory;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed record ChampionshipContext(bool Eligible, IReadOnlyList<string> SessionTypes);

public sealed class LiveChampionshipService(ISupabaseRestClient client, DomainQueryService domain, IMemoryCache cache)
{
    private sealed record Baseline(bool Eligible, bool Complete, IReadOnlyList<ChampionshipRider> Riders, IReadOnlyList<ChampionshipStage> Stages);

    public async Task<ChampionshipContext> GetContextAsync(string roundId, string category, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(roundId, category, cancellationToken);
        return new(data.Eligible, data.Stages.Select(stage => LiveChampionshipCalculator.Key(stage.SessionType)).ToArray());
    }

    public async Task<ChampionshipProjection> ProjectAsync(string roundId, string category, IReadOnlyList<ChampionshipSession> sessions, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(roundId, category, cancellationToken);
        if (!data.Eligible || !data.Complete) return new([], null, null, "unavailable");
        var types = sessions.Select(session => LiveChampionshipCalculator.Key(session.SessionType)).ToHashSet();
        // QUALI and Q1 are alternative formats in historical point systems.
        var stages = data.Stages.Where(stage => types.Contains(LiveChampionshipCalculator.Key(stage.SessionType))).ToArray();
        if (data.Stages.Any(stage => !PublicApiCodes.IsFinal(stage.SessionType)) && stages.All(stage => PublicApiCodes.IsFinal(stage.SessionType)))
            return new([], null, null, "unavailable");
        return LiveChampionshipCalculator.Calculate(data.Riders, stages, sessions);
    }

    private async Task<Baseline> LoadAsync(string roundId, string category, CancellationToken token)
    {
        var key = $"live-championship:{roundId}:{category.ToUpperInvariant()}";
        if (cache.TryGetValue<Baseline>(key, out var cached) && cached is not null) return cached;
        var result = await LoadCoreAsync(roundId, category, token);
        cache.Set(key, result, TimeSpan.FromMinutes(5));
        return result;
    }

    private async Task<Baseline> LoadCoreAsync(string roundId, string category, CancellationToken token)
    {
        var hidden = new Baseline(false, false, [], []);
        var unavailable = new Baseline(true, false, [], []);
        var round = await client.GetSingleAsync(new SupabaseQuery("rounds")
            .Select("id,season_year,start_date,is_season_final_round,competition_level_id,competition_rank_id,competition_levels(code)")
            .Filter("id", "eq", roundId), cancellationToken: token);
        if (round is null || round.Object("competition_levels")?.String("code") is not ("world_cup" or "wc")) return hidden;
        var season = round.Int("season_year");
        var level = round.String("competition_level_id");
        var rank = round.String("competition_rank_id");
        var date = round.String("start_date");
        if (season is null || level is null || date is null) return unavailable;
        var calendar = await client.GetAsync(new SupabaseQuery("rounds")
            .Select("id,start_date,is_season_final_round").Filter("season_year", "eq", season)
            .Filter("competition_level_id", "eq", level).Filter("competition_rank_id", "eq", rank), cancellationToken: token);
        // Explicit finale wins; otherwise use the complete, currently configured series calendar.
        var explicitFinals = calendar.Data.Where(item => item.Bool("is_season_final_round") == true).ToArray();
        var isFinale = explicitFinals.Length > 0
            ? explicitFinals.Length == 1 && explicitFinals[0].String("id") == roundId
            : calendar.Data.Count > 1 && calendar.Data.All(item => item.String("id") == roundId || string.CompareOrdinal(item.String("start_date"), date) < 0);
        if (!isFinale) return hidden;
        var categoryId = await domain.FindCategoryIdByCodeAsync(category, cancellationToken: token);
        if (categoryId is null) return unavailable;
        var systems = await client.GetAsync(new SupabaseQuery("points_systems")
            .Select("id,point_system_stages(session_type,awards_points,awards_points_on_season_final_round,point_rules(position_from,position_to,points,season_final_points))")
            .Filter("competition_level_id", "eq", level).Filter("competition_rank_id", "eq", rank)
            .Filter("category_id", "eq", categoryId).Filter("discipline", "eq", "DHI")
            .Filter("valid_from_season", "lte", season).Filter("valid_to_season", "gte", season), cancellationToken: token);
        if (systems.Data.Count != 1) return unavailable;
        var systemStages = (systems.Data[0].Array("point_system_stages")?.OfType<JsonObject>() ?? []).ToArray();
        var stages = systemStages
            .Where(stage => stage.Bool("awards_points") == true && stage.Bool("awards_points_on_season_final_round") == true)
            .Select(stage => new ChampionshipStage(stage.String("session_type") ?? "", (stage.Array("point_rules")?.OfType<JsonObject>() ?? [])
                .Select(rule => new ChampionshipRule(rule.Int("position_from") ?? 0, rule.Int("position_to"), rule.Int("season_final_points") ?? rule.Int("points") ?? 0)).ToArray())).ToArray();
        if (stages.Length == 0 || stages.Any(stage => stage.Rules.Count == 0 || stage.Rules.Any(rule => rule.From < 1 || rule.Points < 0))) return unavailable;
        var previousRoundIds = calendar.Data.Where(item => item.String("id") != roundId && string.CompareOrdinal(item.String("start_date"), date) < 0)
            .Select(item => item.String("id")!).ToArray();
        if (previousRoundIds.Length == 0) return unavailable;
        var sessions = await client.GetAsync(new SupabaseQuery("sessions").Select("id,session_type,points_session_type_override")
            .In("round_id", previousRoundIds).Filter("category_id", "eq", categoryId), cancellationToken: token);
        var scoringTypes = systemStages.Where(stage => stage.Bool("awards_points") == true)
            .Select(stage => LiveChampionshipCalculator.Key(stage.String("session_type") ?? "")).ToHashSet();
        var scoringSessions = sessions.Data.Where(session => scoringTypes.Contains(LiveChampionshipCalculator.Key(
            session.String("points_session_type_override") ?? session.String("session_type") ?? ""))).ToArray();
        if (scoringSessions.Length == 0) return unavailable;
        var results = await domain.GetMatchingResultsAsync(new RiderMetricsContext(season, category, null, null, null, null, null, null, null, null, null),
            resolvedSessions: scoringSessions, select: "id,rider_id,riders(id,uci_id,first_name,last_name),result_points(points_awarded)", cancellationToken: token);
        // Missing point rows are not equivalent to zero points.
        if (results.Count == 0 || results.Any(result => result.Array("result_points") is not { Count: > 0 })) return unavailable;
        var riders = results.GroupBy(result => result.String("rider_id")!).Select(group =>
        {
            var rider = group.First().Object("riders");
            return new ChampionshipRider(group.Key, rider?.String("uci_id") ?? $"unmapped:{group.Key}",
                rider is null ? "Unknown rider" : rider.DisplayName(), group.Sum(result => result.Int("awarded_points") ?? 0));
        }).ToArray();
        return new(true, true, riders, stages);
    }
}
