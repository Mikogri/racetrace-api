using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Features.Fantasy.Import;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Fantasy;

/// <summary>Walk-forward calibration consumes the existing race engine; it does not retrain race metrics.</summary>
public sealed class FantasyPreparationService(
    FantasyRacePrediction source,
    IOptions<FantasyOptions> options,
    TimeProvider timeProvider,
    FantasyWorkCoordinator coordinator,
    FantasyPreparedStore store,
    FantasyPriceSnapshotStore prices,
    FantasyTrainingParticipants training,
    ILogger<FantasyPreparationService> logger)
{
    public async Task<FantasyPreparedRound> PrepareAsync(JsonObject targetRound, CancellationToken token)
    {
        await coordinator.PrepareGate.WaitAsync(token);
        try
        {
            return await PrepareCoreAsync(targetRound, token);
        }
        finally
        {
            coordinator.PrepareGate.Release();
        }
    }

    public static string EventFingerprint(FantasyEventOptions? eventOptions)
    {
        var canonical = new
        {
            eventOptions?.Q1BonusApplies,
            StartListRiderIds = eventOptions?.StartListRiderIds?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            eventOptions?.StartListSourceUrl,
            eventOptions?.StartListFetchedAtUtc
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    private async Task<FantasyPreparedRound> PrepareCoreAsync(JsonObject target, CancellationToken token)
    {
        var season = target.Int("season_year") ?? throw new InvalidDataException("The upcoming round has no season.");
        if (season != FantasyRules2026.Rules.Season)
            throw new InvalidDataException("Official Fantasy scoring is not verified for this season.");
        var roundId = target.String("id") ?? throw new InvalidDataException("The upcoming round has no ID.");
        var starts = DateTimeOffset.Parse(target.String("start_date")!, CultureInfo.InvariantCulture);
        var eventOptions = options.Value.Events.GetValueOrDefault(roundId);
        var priceSnapshot = await prices.GetLatestAsync(season, token);
        var previousForecast = await store.ReadAsync(roundId, token);
        var trainingFields = await training.ResolveAsync(target, priceSnapshot?.Riders ?? [], previousForecast?.TrainingFields, token);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var backtests = new List<FantasyRoundBacktest>();
        var preparedRiders = new List<FantasyPreparedRider>();
        var calibrations = new Dictionary<string, FantasyStageCalibration>(StringComparer.Ordinal);
        var dataRevision = await source.DataRevisionAsync(token);
        var rounds = await source.RoundsAsync(season, token);
        var archive = new List<FantasyRoundData>();
        foreach (var round in rounds.Where(round => DateTimeOffset.Parse(round.String("start_date")!, CultureInfo.InvariantCulture) < starts)
            .OrderBy(round => DateTimeOffset.Parse(round.String("start_date")!, CultureInfo.InvariantCulture)))
        {
            token.ThrowIfCancellationRequested();
            archive.Add(await source.LoadAsync(round, token));
        }

        var q1Applies = ResolveQ1(target, eventOptions, warnings);
        var salarySnapshots = new Dictionary<string, FantasyPriceSnapshot>(StringComparer.Ordinal);
        var salaryObservations = new Dictionary<string, List<FantasyBacktestRider>>(StringComparer.Ordinal);
        var salaryTrainingThrough = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var historical in archive.Where(round => round.Season >= season - 1))
        {
            var snapshot = await prices.GetAtOrBeforeAsync(historical.Season, historical.Start.AddTicks(-1), token);
            if (IsHistoricalSalaryContext(historical, snapshot, options.Value.Events.GetValueOrDefault(historical.Id)))
                salarySnapshots[historical.Id] = snapshot!;
        }
        warnings.Add("Historical backtests apply the 2026 Fantasy scoring retrospectively; they are not historical Pinkbike season totals.");
        warnings.Add("Historical salary optimization is evaluated only when matching pre-round price snapshots, verified entry lists and complete eligible-rider outcomes exist.");
        if (eventOptions?.StartListRiderIds is null)
            warnings.Add("Race projections use the historical field until a confirmed entry list is available.");

        foreach (var (category, gender) in new[] { ("EM", "male"), ("EW", "female") })
        {
            var finalHistory = new List<FantasyCalibrationObservation>();
            var q1History = new List<FantasyCalibrationObservation>();
            foreach (var historical in archive.Where(round => round.Season >= season - 1))
            {
                token.ThrowIfCancellationRequested();
                var stages = Stages(historical, category);
                if (stages.Final is null && stages.Q1 is null) continue;
                var salaryEvent = salarySnapshots.ContainsKey(historical.Id) ? options.Value.Events[historical.Id] : null;
                var historicalQ1Applies = salaryEvent?.Q1BonusApplies ?? stages.Q1 is not null;
                var forecasts = await source.ForecastAsync(archive, historical.Round, category, 10_000,
                    salaryEvent?.StartListRiderIds?.ToHashSet(StringComparer.Ordinal), token);
                if (forecasts.Count == 0) continue;
                var finalFit = FitBefore(finalHistory, historical.Start);
                var q1Fit = FitBefore(q1History, historical.Start);
                var observations = new List<FantasyBacktestRider>();
                foreach (var forecast in forecasts)
                {
                    var actualQ1 = FindResult(historical, stages.Q1, forecast.RiderId);
                    var actualFinal = ResolveFinalResult(historical, stages.Final, actualQ1, forecast.RiderId);
                    if (actualFinal is not null && finalFit is not null && (!historicalQ1Applies || q1Fit is not null))
                    {
                        var actual = ActualPoints(actualFinal, actualQ1, historicalQ1Applies, gender, warnings);
                        if (actual.HasValue)
                        {
                            var projection = FantasyScoring.Project(FantasyCalibration.Apply(forecast.Positions, finalFit),
                                !historicalQ1Applies ? null : FantasyCalibration.Apply(forecast.Positions, q1Fit!),
                                FantasyRules2026.Rules, historicalQ1Applies, gender);
                            observations.Add(new(forecast.RiderId, forecast.RiderId, gender, projection.ExpectedFantasyPoints, actual.Value));
                        }
                    }
                    AddObservation(finalHistory, historical, stages.Final, actualFinal, forecast);
                    AddObservation(q1History, historical, stages.Q1, actualQ1, forecast);
                }
                if (observations.Count > 0)
                {
                    var trainedThrough = MaxTrainingDate(finalFit!, historicalQ1Applies ? q1Fit : null);
                    backtests.Add(new(historical.Season, historical.Id, category, FantasyBacktester.Evaluate(observations,
                        FantasyRules2026.Rules, historical.Id, historical.Start,
                        trainedThrough, [], cancellationToken: token)));
                    if (salaryEvent is not null)
                    {
                        if (!salaryObservations.TryGetValue(historical.Id, out var combined))
                            salaryObservations[historical.Id] = combined = [];
                        combined.AddRange(observations);
                        if (!salaryTrainingThrough.TryGetValue(historical.Id, out var previous) || previous < trainedThrough)
                            salaryTrainingThrough[historical.Id] = trainedThrough;
                    }
                }
            }

            if (!q1Applies.HasValue) continue;
            var currentFinalFit = FitBefore(finalHistory, starts);
            var currentQ1Fit = q1Applies.Value ? FitBefore(q1History, starts) : null;
            if (currentFinalFit is not null) calibrations[category + ".final"] = currentFinalFit;
            if (currentQ1Fit is not null) calibrations[category + ".q1"] = currentQ1Fit;
            if (currentFinalFit is null || q1Applies.Value && currentQ1Fit is null)
            {
                warnings.Add($"{category}: at least two earlier category events with valid final and applicable Q1 observations are required for calibrated projections.");
                continue;
            }
            // Training matches cover the Pinkbike catalogue, not necessarily the entire race field.
            // They constrain team selection only; do not artificially improve odds by removing competitors.
            var startList = eventOptions?.StartListRiderIds?.ToHashSet(StringComparer.Ordinal);
            var current = await source.ForecastAsync(archive, target, category, 50_000, startList, token);
            foreach (var forecast in current)
            {
                var finalDistribution = FantasyCalibration.Apply(forecast.Positions, currentFinalFit);
                var projection = FantasyScoring.Project(finalDistribution,
                    currentQ1Fit is null ? null : FantasyCalibration.Apply(forecast.Positions, currentQ1Fit),
                    FantasyRules2026.Rules, q1Applies.Value, gender);
                preparedRiders.Add(new(forecast.RiderId, forecast.Name, forecast.CountryCode, gender,
                    forecast.Confidence, FantasyScoring.ExpectedClassifiedPosition(finalDistribution), projection.ExpectedFinalPoints,
                    projection.ExpectedQ1Bonus, projection.ExpectedFantasyPoints));
            }
        }
        foreach (var historical in archive.Where(round => salarySnapshots.ContainsKey(round.Id)))
        {
            var salary = EvaluateHistoricalSalary(historical, salarySnapshots[historical.Id], options.Value.Events[historical.Id],
                salaryObservations.GetValueOrDefault(historical.Id) ?? [], salaryTrainingThrough.GetValueOrDefault(historical.Id), warnings, token);
            if (salary is not null) backtests.Add(new(historical.Season, historical.Id, "ALL", salary));
        }
        warnings.Add("Absent final riders count as non-scoring only after confirmed Q1 participation and a completed, non-empty final dataset; otherwise missing observations are excluded.");
        warnings.Add("Only completed historical sessions are accepted as calibration or backtest outcomes; provisional live results are excluded.");
        warnings.Add("Predicted position is the calibrated expected position conditional on classification, not a winning probability or a guaranteed finish.");
        if (dataRevision != await source.DataRevisionAsync(token, forceRefresh: true))
            throw new InvalidDataException("Race data changed during Fantasy preparation. Retry to prepare a consistent forecast.");
        var prepared = new FantasyPreparedRound(season, roundId, starts, timeProvider.GetUtcNow(), FantasyRacePrediction.EngineVersion,
            FantasyCalibration.Version, FantasyRules2026.Rules.Version, EventFingerprint(eventOptions),
            preparedRiders.OrderByDescending(rider => rider.ExpectedFantasyPoints).ToArray(), warnings.Order(StringComparer.Ordinal).ToArray(), backtests, calibrations, dataRevision, trainingFields);
        await store.WriteAsync(prepared, token);
        logger.LogInformation("Prepared Fantasy round {RoundId}: {RiderCount} projections and {BacktestCount} walk-forward category tests.",
            roundId, prepared.Riders.Count, backtests.Count);
        return prepared;
    }

    private static bool? ResolveQ1(JsonObject round, FantasyEventOptions? eventOptions, HashSet<string> warnings)
    {
        if (eventOptions?.Q1BonusApplies is not null) return eventOptions.Q1BonusApplies;
        if (round.Int("season_year") == 2026 && round.Object("competition_levels")?.String("code") == "world_cup") return true;
        warnings.Add("Q1 bonus applicability is unverified for this event format; configure it before preparing a usable projection.");
        return null;
    }

    internal static bool IsHistoricalSalaryContext(FantasyRoundData round, FantasyPriceSnapshot? snapshot, FantasyEventOptions? eventOptions) =>
        snapshot is not null && snapshot.Season == round.Season && snapshot.UpcomingRoundId == round.Id && snapshot.FetchedAtUtc < round.Start &&
        eventOptions?.StartListRiderIds is { Count: > 0 } && eventOptions.StartListFetchedAtUtc is not null && eventOptions.StartListFetchedAtUtc < round.Start &&
        Uri.TryCreate(eventOptions.StartListSourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps;

    internal static FantasyBacktestMetrics? EvaluateHistoricalSalary(FantasyRoundData round, FantasyPriceSnapshot snapshot,
        FantasyEventOptions? eventOptions, IReadOnlyList<FantasyBacktestRider> observations, DateTimeOffset trainedThrough,
        HashSet<string> warnings, CancellationToken token = default)
    {
        if (!IsHistoricalSalaryContext(round, snapshot, eventOptions)) return null;
        var startList = eventOptions!.StartListRiderIds!.ToHashSet(StringComparer.Ordinal);
        var eligible = snapshot.Riders.Where(rider => rider.RiderId is not null && startList.Contains(rider.RiderId) && !rider.InjuryFlag).ToArray();
        var byRider = observations.Where(row => row.RiderId is not null).GroupBy(row => row.RiderId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (eligible.Length == 0 || eligible.Select(rider => rider.RiderId).Distinct(StringComparer.Ordinal).Count() != eligible.Length ||
            eligible.Select(rider => rider.ExternalId).Distinct(StringComparer.Ordinal).Count() != eligible.Length ||
            eligible.Any(rider => rider.Price <= 0 || rider.Gender is not ("male" or "female") ||
                !byRider.TryGetValue(rider.RiderId!, out var rows) || rows.Length != 1 || rows[0].Gender != rider.Gender))
        {
            warnings.Add($"Round {round.Id}: historical salary evaluation is skipped because the confirmed eligible pool has missing, ambiguous or invalid prices, projections or outcomes.");
            return null;
        }
        var mapped = eligible.Select(rider => byRider[rider.RiderId!][0] with { PinkbikeExternalId = rider.ExternalId }).ToArray();
        var historicalPrices = eligible.Select(rider => new FantasyHistoricalPrice(rider.ExternalId, round.Id, rider.Price, snapshot.FetchedAtUtc)).ToArray();
        var result = FantasyBacktester.Evaluate(mapped, FantasyRules2026.Rules, round.Id, round.Start, trainedThrough, historicalPrices, cancellationToken: token);
        if (result.PredictedTeamActualPoints is null)
        {
            warnings.Add($"Round {round.Id}: confirmed historical candidates cannot form a legal salary-constrained lineup.");
            return null;
        }
        return result;
    }

    private static FantasyStageCalibration? FitBefore(List<FantasyCalibrationObservation> history, DateTimeOffset cutoff) =>
        FantasyCalibration.Fit(history.Where(observation => observation.CompletedAtUtc < cutoff).ToArray(), cutoff);

    private static DateTimeOffset MaxTrainingDate(FantasyStageCalibration final, FantasyStageCalibration? q1) =>
        q1 is not null && q1.TrainedThroughUtc > final.TrainedThroughUtc ? q1.TrainedThroughUtc : final.TrainedThroughUtc;

    internal static (JsonObject? Final, JsonObject? Q1) Stages(FantasyRoundData round, string category)
    {
        var sessions = round.Data.Sessions.Where(session => FantasyRacePrediction.Category(session) == category)
            .Where(session => session.String("status")?.Trim().ToLowerInvariant() is "completed" or "finished" or "final")
            .OrderBy(FantasyRacePrediction.SessionDate).ToArray();
        return (sessions.LastOrDefault(session => PaceService.ParseSessionType(session.String("session_type"), session.String("name")) == PaceSessionType.Final),
            sessions.FirstOrDefault(session => PaceService.ParseSessionType(session.String("session_type"), session.String("name")) == PaceSessionType.Qualifying1));
    }

    private static JsonObject? FindResult(FantasyRoundData round, JsonObject? session, string riderId) => session is null ? null :
        round.Data.Results.SingleOrDefault(result => result.String("session_id") == session.String("id") && result.String("rider_id") == riderId);

    internal static JsonObject? ResolveFinalResult(FantasyRoundData round, JsonObject? finalSession, JsonObject? q1Result, string riderId)
    {
        var actual = FindResult(round, finalSession, riderId);
        if (actual is not null || finalSession is null || q1Result is null) return actual;
        var complete = finalSession.String("status")?.Trim().ToLowerInvariant() is "completed" or "finished" or "final";
        var hasFinalResults = round.Data.Results.Any(result => result.String("session_id") == finalSession.String("id"));
        // get_ranking_data returns the full field. Without the completed flag and evidence of
        // an imported field, absence is unknown, not a fabricated DNQ or a last-place finish.
        return complete && hasFinalResults ? new JsonObject { ["status"] = "NO_SCORING_FINAL" } : null;
    }

    private static void AddObservation(List<FantasyCalibrationObservation> history, FantasyRoundData round,
        JsonObject? session, JsonObject? actual, FantasyRaceForecast forecast)
    {
        if (session is null || actual is null) return;
        var outcome = Outcome(actual);
        if (!outcome.HasValue) return;
        history.Add(new(round.Id, FantasyRacePrediction.SessionDate(session), forecast.Positions, outcome.Value));
    }

    private static int? Outcome(JsonObject result)
    {
        var status = result.String("status")?.Trim().ToUpperInvariant();
        if (status is "DNS" or "DNF" or "DSQ" or "DNQ" or "NOT_STARTED" or "NOT_QUALIFIED" or "NO_SCORING_FINAL") return 0;
        var position = result.Int("position");
        return position > 0 ? position : null;
    }

    private static int? ActualPoints(JsonObject final, JsonObject? q1, bool bonusApplies, string gender, HashSet<string> warnings)
    {
        var finalOutcome = Outcome(final);
        if (!finalOutcome.HasValue || bonusApplies && (q1 is null || !Outcome(q1).HasValue)) return null;
        var q1Position = q1 is null ? null : Outcome(q1);
        var hasBonus = bonusApplies && q1Position is > 0 and <= 3;
        var finalStatus = final.String("status")?.Trim().ToUpperInvariant();
        if (finalOutcome == 0 && hasBonus && finalStatus != "DNF")
        {
            warnings.Add("Q1 bonus retention after DNS/DSQ/DNQ has not been verified; affected historical total-point observations are excluded.");
            return null;
        }
        return FantasyScoring.ActualPoints(finalOutcome, finalStatus, q1Position, bonusApplies,
            finalStatus == "DNF" ? FantasyQ1Retention.Retained : FantasyQ1Retention.Unverified, FantasyRules2026.Rules, gender);
    }
}
