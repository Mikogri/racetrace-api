using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Fantasy;

public sealed class FantasyService(
    FantasyRacePrediction race, FantasyPriceSnapshotStore prices, FantasyPreparedStore prepared,
    IOptions<FantasyOptions> options, TimeProvider time, IMemoryCache cache)
{
    internal async Task<JsonObject?> NextRoundAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        return (await race.RoundsAsync(now.Year, token)).Where(round => round.Int("season_year") == now.Year &&
            (FantasyTrainingParticipants.IsLive(round) ||
             DateTimeOffset.TryParse(round.String("start_date"), CultureInfo.InvariantCulture, out var start) && start > now))
            .OrderByDescending(FantasyTrainingParticipants.IsLive)
            .ThenBy(round => round.String("start_date"), StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<FantasyCurrentDto> CurrentAsync(CancellationToken token)
    {
        var season = time.GetUtcNow().Year;
        var round = await NextRoundAsync(token);
        var priceStatus = await prices.GetStatusAsync(season, token);
        var snapshot = priceStatus.Snapshot;
        var warnings = new List<string>();
        var configuredRules = FantasyRuleCatalog.ForSeason(season);
        var rules = configuredRules ?? FantasyRuleCatalog.Default;
        if (configuredRules is null) warnings.Add("Official Fantasy scoring has not been configured for this season.");
        if (snapshot is null) warnings.Add("Pinkbike prices have not been imported. An administrator must synchronize the athlete catalogue.");
        if (priceStatus.LastSyncError is not null) warnings.Add(priceStatus.LastSyncError);
        if (snapshot is not null && priceStatus.IsStale) warnings.Add("Price refresh is overdue. Showing the last successfully imported prices; check current prices on Pinkbike before selecting your team.");
        if (round is null) warnings.Add("No upcoming eligible round is available.");
        var roundId = round?.String("id");
        var forecast = roundId is null ? null : await prepared.ReadAsync(roundId, token);
        var eventOptions = roundId is null ? null : options.Value.Events.GetValueOrDefault(roundId);
        var startList = eventOptions?.StartListRiderIds?.ToHashSet(StringComparer.Ordinal);
        var verifiedList = startList is { Count: > 0 } &&
            Uri.TryCreate(eventOptions?.StartListSourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps &&
            eventOptions?.StartListFetchedAtUtc is not null && eventOptions.StartListFetchedAtUtc <= time.GetUtcNow();
        if (round is not null && !verifiedList) warnings.Add("Team selection uses the Pinkbike catalogue and reported injuries; participation in the next round is not confirmed.");
        var validForecast = forecast is not null && forecast.Season == season && forecast.EngineVersion == FantasyRacePrediction.EngineVersion &&
            (forecast.CalculationFingerprint == AlgorithmCatalog.Current.Fingerprint("fantasy") ||
                forecast.CalculationFingerprint is null && !race.CoherenceEnabled && !options.Value.DistributedCoordinationEnabled) &&
            forecast.ProjectionVersion == FantasyCalibration.Version && forecast.RulesVersion == rules.Version &&
            forecast.EventFingerprint == FantasyPreparationService.EventFingerprint(eventOptions) &&
            forecast.DataRevision == await race.DataRevisionAsync(token);
        // The refresh interval is a scheduling target, not a reason to erase unchanged predictions.
        if (validForecast && time.GetUtcNow() - forecast!.PreparedAtUtc > options.Value.SyncInterval)
            warnings.Add("Projection refresh is overdue. Showing the last successful forecast for the unchanged race data and model.");
        if (round is not null && !validForecast) warnings.Add("Fantasy projections need preparation or refresh using the current model, rules and event data.");
        var trainingFields = validForecast ? forecast!.TrainingFields ?? [] : [];
        foreach (var field in trainingFields)
            warnings.Add($"{field.Gender}: candidate pool uses the published timed-training roster ({field.SourceUrl}). Training participation is not a guarantee of starting the race.");
        bool OnEntryList(FantasyRiderDto rider) => rider.RiderId is not null &&
            (verifiedList ? startList!.Contains(rider.RiderId) :
             trainingFields.FirstOrDefault(field => field.Gender == rider.Gender) is not { } field || field.RiderIds.Contains(rider.RiderId));
        // Older persisted snapshots contain the retired mandatory-entry-list policy.
        if (forecast is not null) warnings.AddRange(forecast.Warnings.Where(warning => warning !=
            "The current entry list has not been verified; projections are informational and cannot establish optimizer eligibility."));
        var projections = validForecast ? forecast!.Riders.ToDictionary(rider => rider.RiderId, StringComparer.Ordinal) : new Dictionary<string, FantasyPreparedRider>();
        var riders = snapshot?.Riders.Select(rider =>
        {
            var rowWarnings = new List<string>();
            var projection = rider.RiderId is null ? null : projections.GetValueOrDefault(rider.RiderId);
            if (projection is not null && projection.Gender != rider.Gender)
            {
                rowWarnings.Add("Prediction category does not match the Pinkbike rider category.");
                projection = null;
            }
            if (rider.RiderId is null) rowWarnings.Add("Internal rider mapping is missing or ambiguous.");
            if (projection is null) rowWarnings.Add("A verified projection is not available.");
            if (rider.InjuryFlag) rowWarnings.Add("Pinkbike reports an injury; excluded from automatic selection.");
            if (verifiedList && (rider.RiderId is null || !startList!.Contains(rider.RiderId))) rowWarnings.Add("Not on the confirmed start list.");
            if (!verifiedList && trainingFields.FirstOrDefault(field => field.Gender == rider.Gender) is { } trainingField &&
                (rider.RiderId is null || !trainingField.RiderIds.Contains(rider.RiderId))) rowWarnings.Add("Not matched to the published timed-training roster.");
            return new FantasyRiderDto(rider.ExternalId, rider.RiderId, rider.Name, projection?.CountryCode,
                rider.Gender, rider.Price, rider.SeasonPoints, rider.InjuryFlag, projection?.ExpectedFinalPoints,
                projection?.ExpectedQ1Bonus, projection?.ExpectedFantasyPoints, projection?.PredictedPosition,
                projection?.Confidence, false, rowWarnings);
        }).ToArray() ?? [];
        FantasyLineup? lineup = null;
        var pricesMatchRound = snapshot?.UpcomingRoundId == roundId;
        if (snapshot is not null && !pricesMatchRound) warnings.Add("The price catalogue belongs to another round; a matching price import is required for Best Team.");
        if (season == rules.Season && validForecast && pricesMatchRound)
        {
            var candidates = riders.Where(rider => OnEntryList(rider) &&
                !rider.InjuryFlag && rider.Price is > 0 && rider.ExpectedFantasyPoints.HasValue)
                .Select(rider => new FantasyCandidate(rider.PinkbikeExternalId, rider.RiderId, rider.Gender, rider.Price!.Value, rider.ExpectedFantasyPoints!.Value)).ToArray();
            var lineupKey = $"fantasy-lineup:{snapshot?.Id}:{forecast!.PreparedAtUtc:O}:{forecast.EventFingerprint}:{rules.Version}";
            if (!cache.TryGetValue<FantasyLineup?>(lineupKey, out lineup))
            {
                lineup = FantasyOptimizer.Optimize(candidates, rules, token);
                cache.Set(lineupKey, lineup, TimeSpan.FromMinutes(5));
            }
            if (lineup is null) warnings.Add("Available eligible riders cannot form a legal four-men/two-women team within the budget.");
        }
        var chosen = lineup?.Riders.Select(rider => rider.PinkbikeExternalId).ToHashSet(StringComparer.Ordinal) ?? [];
        riders = riders.Select(rider => rider with { IsRecommended = chosen.Contains(rider.PinkbikeExternalId) }).OrderByDescending(rider => rider.ExpectedFantasyPoints).ToArray();
        var recommendation = lineup is null ? null : new FantasyRecommendationDto(riders.Where(rider => rider.IsRecommended).ToArray(),
            lineup.TotalPrice, rules.Budget - lineup.TotalPrice, lineup.ExpectedFantasyPoints);
        return new(recommendation is not null ? "ready" : "incomplete", season,
            round is null ? null : new(roundId!, round.Int("round_number"), round.Object("tracks")?.String("venue"), round.Object("tracks")?.String("name"), round.String("start_date")),
            riders, recommendation, warnings.Distinct(StringComparer.Ordinal).ToArray(), snapshot?.FetchedAtUtc,
            snapshot?.Id, FantasyRules2026.AthletesUrl, FantasyRacePrediction.EngineVersion, FantasyCalibration.Version,
            validForecast && forecast!.CalculationFingerprint is not null ? new(forecast.ProjectionVersion, forecast.ConfigurationHash,
                forecast.DataRevision, forecast.PreparedAtUtc, forecast.EngineVersion, forecast.TrainingDataCutoff, forecast.PredictionAsOf,
                forecast.SimulationSeed, forecast.SimulationIterations, forecast.CalculationFingerprint) : null);
    }

    public static FantasyRulesDto Rules()
    {
        var rules = FantasyRuleCatalog.Default;
        return new(rules.Season, rules.Version, rules.Budget, rules.TeamSize, rules.Men, rules.Women,
            rules.MenFinalPlacementPoints.Keys.Union(rules.WomenFinalPlacementPoints.Keys).Order().Select(position =>
                new FantasyPlacementDto(position, rules.MenFinalPlacementPoints.TryGetValue(position, out var men) ? men : null,
                    rules.WomenFinalPlacementPoints.TryGetValue(position, out var women) ? women : null)).ToArray(),
            rules.Q1Bonus.Select(pair => new FantasyBonusDto(pair.Key, pair.Value)).ToArray(), true, FantasyRules2026.SourceUrl);
    }
}
