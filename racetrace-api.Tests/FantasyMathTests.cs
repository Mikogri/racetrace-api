using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Services;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Models;
using RaceTrace.Api.Features.Fantasy.Import;

namespace RaceTrace.Api.Tests;

public sealed class FantasyMathTests
{
    private static FantasyRules Rules(decimal budget = 1_500_000m) => new(2026, "test-only", budget, 6, 4, 2,
        new Dictionary<int, int> { [1] = 500, [2] = 400, [3] = 300, [4] = 250 },
        new Dictionary<int, int> { [1] = 500, [2] = 400, [3] = 300, [4] = 200 },
        new Dictionary<int, int> { [1] = 100, [2] = 75, [3] = 50 });

    [Fact]
    public void EveryOfficial2026PositionAndQ1BonusMatchesPublishedTables()
    {
        int[] men = [500, 400, 300, 275, 250, 240, 230, 220, 210, 200, 190, 180, 170, 160, 150, 140, 130, 120, 110, 100, 90, 80, 70, 60, 50, 40, 30, 20, 15, 10];
        int[] women = [500, 400, 300, 275, 250, 225, 200, 175, 150, 125, 100, 75, 50, 25, 10];
        var rules = FantasyRules2026.Rules;
        Assert.Equal(30, rules.MenFinalPlacementPoints.Count);
        Assert.Equal(15, rules.WomenFinalPlacementPoints.Count);
        foreach (var (gender, points) in new[] { ("male", men), ("female", women) })
        {
            for (var index = 0; index < points.Length; index++)
                Assert.Equal(points[index], FantasyScoring.ActualPoints(index + 1, null, null, false, FantasyQ1Retention.Unverified, rules, gender));
            Assert.Equal(0, FantasyScoring.ActualPoints(points.Length + 1, null, null, false, FantasyQ1Retention.Unverified, rules, gender));
            int[] bonuses = [100, 75, 50, 0];
            for (var index = 0; index < bonuses.Length; index++)
                Assert.Equal(500 + bonuses[index], FantasyScoring.ActualPoints(1, null, index + 1, true, FantasyQ1Retention.Unverified, rules, gender));
        }
        Assert.Equal(1_500_000m, rules.Budget);
        Assert.Equal(6, rules.TeamSize);
        Assert.Equal(4, rules.Men);
        Assert.Equal(2, rules.Women);
    }

    [Fact]
    public void MonteCarloDetailedDistributionPreservesExistingPredictionAndAccountsForEveryPlace()
    {
        var options = new PodiumPredictionOptions { MonteCarloIterations = 1000, RandomSeed = 873 };
        var distributions = Enumerable.Range(0, 8).Select(index => new PodiumPredictionDistribution(
            new PodiumPredictionFeatures(index.ToString(), 85, 85, 85, 85, 85, 90, 85, 0, 0, 0, 0, 0, 0, 0, .1, 20),
            85 - index * 2, 3, .1)).ToArray();
        var simulator = new MonteCarloRaceSimulator(options);
        var original = simulator.Simulate(distributions);
        var detailed = simulator.SimulateDetailed(distributions);
        Assert.Equal(original, detailed.Select(x => x.Summary));
        for (var rider = 0; rider < detailed.Count; rider++)
        {
            var probabilities = detailed[rider].PositionProbabilities;
            Assert.Equal(distributions.Length, probabilities.Count);
            Assert.Equal(1d, probabilities.Sum(), 10);
            Assert.Equal(original[rider].WinProbability, probabilities[0], 10);
            Assert.Equal(original[rider].PodiumProbability, probabilities.Take(3).Sum(), 10);
            Assert.Equal(original[rider].Top5Probability, probabilities.Take(5).Sum(), 10);
            Assert.Equal(original[rider].ExpectedPosition, probabilities.Select((probability, index) => probability * (index + 1)).Sum(), 10);
        }
        for (var position = 0; position < distributions.Length; position++)
            Assert.Equal(1d, detailed.Sum(x => x.PositionProbabilities[position]), 10);
    }

    [Fact]
    public void CalibrationRejectsFutureOutcomesAndInsufficientHistory()
    {
        var forecast = DateTimeOffset.Parse("2024-06-14T00:00:00Z");
        var observation = new FantasyCalibrationObservation("event1", forecast.AddDays(-1), new Dictionary<int, decimal> { [1] = 1 }, 1);
        Assert.Null(FantasyCalibration.Fit([observation], forecast));
        Assert.Throws<ArgumentException>(() => FantasyCalibration.Fit([observation with { CompletedAtUtc = forecast }], forecast));
        Assert.Throws<ArgumentException>(() => FantasyCalibration.Fit([observation with { CompletedAtUtc = forecast.AddDays(1) }], forecast));
        Assert.Throws<ArgumentException>(() => FantasyCalibration.Fit([observation with { Outcome = -1 }], forecast));
        Assert.Throws<ArgumentException>(() => FantasyCalibration.Fit([observation with { RawProbabilities = new Dictionary<int, decimal> { [1] = .9m } }], forecast));
        Assert.Throws<ArgumentException>(() => FantasyCalibration.Fit([observation with { RawProbabilities = new Dictionary<int, decimal> { [0] = 1m } }], forecast));
    }

    [Fact]
    public void CalibrationLearnsFromObservedOutcomesAndPreservesExplicitNonFinishMass()
    {
        var forecast = DateTimeOffset.Parse("2024-06-14T00:00:00Z");
        var perfect = Enumerable.Range(1, 4).Select(index => new FantasyCalibrationObservation($"event{index}", forecast.AddDays(-index),
            new Dictionary<int, decimal> { [index] = 1 }, index)).ToArray();
        var noAdjustment = FantasyCalibration.Fit(perfect, forecast);
        Assert.NotNull(noAdjustment);
        Assert.Equal(0m, noAdjustment.Blend);
        var failed = Enumerable.Range(1, 4).Select(index => new FantasyCalibrationObservation($"event{index}", forecast.AddDays(-index),
            new Dictionary<int, decimal> { [1] = 1, [2] = 0 }, index % 2 == 0 ? 0 : 2)).ToArray();
        var fitted = FantasyCalibration.Fit(failed, forecast);
        Assert.NotNull(fitted);
        Assert.Equal(1m, fitted.Blend);
        var calibrated = FantasyCalibration.Apply(new Dictionary<int, decimal> { [1] = 1, [2] = 0 }, fitted);
        Assert.Equal(.5m, calibrated.NonScoringProbability);
        Assert.Equal(.375m, calibrated.PositionProbabilities[1]);
        Assert.Equal(.125m, calibrated.PositionProbabilities[2]);
        Assert.Equal(1m, calibrated.NonScoringProbability + calibrated.PositionProbabilities.Values.Sum());
        Assert.Equal(237.5m, FantasyScoring.Project(calibrated, null, FantasyRules2026.Rules, false, "male").ExpectedFantasyPoints);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.3)]
    [InlineData(1)]
    public void CalibratedFullFieldHasAtMostOneWinnerAndOneRiderInEveryPosition(double nonScoringValue)
    {
        const int fieldSize = 8;
        var nonScoring = (decimal)nonScoringValue;
        var calibration = new FantasyStageCalibration(.75m,
            new Dictionary<int, decimal> { [0] = nonScoring, [1] = 1 - nonScoring }, 40, 4, DateTimeOffset.UtcNow.AddDays(-1));
        var field = Enumerable.Range(1, fieldSize).Select(rider => FantasyCalibration.Apply(
            Enumerable.Range(1, fieldSize).ToDictionary(position => position, position => position == rider ? 1m : 0m), calibration)).ToArray();
        foreach (var rider in field)
        {
            Assert.InRange(Math.Abs(rider.PositionProbabilities.Values.Sum() + rider.NonScoringProbability - 1), 0m, .000000001m);
            Assert.Equal(.75m * nonScoring, rider.NonScoringProbability);
        }
        for (var position = 1; position <= fieldSize; position++)
            Assert.InRange(field.Sum(rider => rider.PositionProbabilities[position]), 0m, 1.000000001m);
        Assert.InRange(Math.Abs(field.Sum(rider => rider.PositionProbabilities.Values.Sum()) - fieldSize * (1 - .75m * nonScoring)), 0m, .000000001m);
    }

    [Theory]
    [InlineData(199, 0.6138364779874214)]
    [InlineData(500, 0.01)]
    [InlineData(500, 0.99)]
    public void CoherentBaselineRemainsNormalizedForLargeFieldsAndExtremeNonFinishRates(int fieldSize, double nonScoringValue)
    {
        var nonScoring = (decimal)nonScoringValue;
        var calibration = new FantasyStageCalibration(1m,
            new Dictionary<int, decimal> { [0] = nonScoring, [1] = 1 - nonScoring }, 2000, 20, DateTimeOffset.UtcNow.AddDays(-1));
        var raw = Enumerable.Range(1, fieldSize).ToDictionary(position => position, position => position == 1 ? 1m : 0m);
        var rider = FantasyCalibration.Apply(raw, calibration);
        Assert.InRange(Math.Abs(rider.PositionProbabilities.Values.Sum() + rider.NonScoringProbability - 1), 0m, .000000001m);
        foreach (var probability in rider.PositionProbabilities.Values)
            Assert.InRange(probability * fieldSize, 0m, 1.000000001m);
    }

    [Fact]
    public void ExactOptimizerMatchesIndependentExhaustiveSearch()
    {
        var random = new Random(801);
        for (var sample = 0; sample < 35; sample++)
        {
            var riders = Enumerable.Range(0, 12).Select(i => new FantasyCandidate($"external-{i}", $"rider-{i}",
                i < 8 ? "male" : "female", random.Next(50_000, 450_000) + 0.17m,
                random.Next(0, 600) + 0.23m)).ToArray();
            var optimum = FantasyOptimizer.Optimize(riders, Rules());
            decimal? bruteForce = null;
            for (var mask = 0; mask < (1 << riders.Length); mask++)
            {
                var selected = riders.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                if (selected.Length != 6 || selected.Count(x => x.Gender == "male") != 4
                    || selected.Sum(x => x.Price) > 1_500_000m) continue;
                var score = selected.Sum(x => x.ExpectedFantasyPoints);
                if (bruteForce is null || score > bruteForce) bruteForce = score;
            }
            Assert.Equal(bruteForce, optimum?.ExpectedFantasyPoints);
            if (optimum is null) continue;
            Assert.Equal(6, optimum.Riders.Count);
            Assert.Equal(4, optimum.Riders.Count(x => x.Gender == "male"));
            Assert.Equal(2, optimum.Riders.Count(x => x.Gender == "female"));
            Assert.Equal(6, optimum.Riders.Select(x => x.RiderId).Distinct().Count());
            Assert.InRange(optimum.TotalPrice, 0, 1_500_000m);
        }
    }

    [Fact]
    public void ValueGreedyCanLoseToExactOptimizer()
    {
        FantasyCandidate[] riders =
        [
            new("m1", "1", "male", 1, 20), new("m2", "2", "male", 1, 20),
            new("m3", "3", "male", 1, 20), new("cheap", "4", "male", 2, 9),
            new("best", "5", "male", 5, 20),
            new("f1", "6", "female", 1, 10), new("f2", "7", "female", 1, 10)
        ];
        var greedy = riders.Where(x => x.Gender == "male").OrderByDescending(x => x.ExpectedFantasyPoints / x.Price)
            .Take(4).Concat(riders.Where(x => x.Gender == "female")).ToArray();
        var exact = FantasyOptimizer.Optimize(riders, Rules(10));
        Assert.NotNull(exact);
        Assert.Equal(100m, exact.ExpectedFantasyPoints);
        Assert.True(exact.ExpectedFantasyPoints > greedy.Sum(x => x.ExpectedFantasyPoints));
        Assert.Contains(exact.Riders, x => x.PinkbikeExternalId == "best");
    }

    [Fact]
    public void NoSingleRiderPriceCapAndNoSalaryRounding()
    {
        var riders = LegalPool();
        riders[0] = riders[0] with { Price = 600_000.01m };
        Assert.NotNull(FantasyOptimizer.Optimize(riders, Rules(1_600_000.01m)));
        Assert.Null(FantasyOptimizer.Optimize(riders, Rules(1_600_000m)));
    }

    [Fact]
    public void RejectsDuplicateInternalOrExternalIdentity()
    {
        var riders = LegalPool();
        Assert.Throws<ArgumentException>(() => FantasyOptimizer.Optimize([.. riders, riders[0]], Rules()));
        Assert.Throws<ArgumentException>(() => FantasyOptimizer.Optimize(
            [.. riders, riders[0] with { PinkbikeExternalId = "another-id", Gender = "female" }], Rules()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidPrices(int price)
    {
        var riders = LegalPool();
        riders[0] = riders[0] with { Price = price };
        Assert.Throws<ArgumentException>(() => FantasyOptimizer.Optimize(riders, Rules()));
    }

    [Fact]
    public void InfeasibleRosterReturnsNoTeamAndCancellationIsHonored()
    {
        Assert.Null(FantasyOptimizer.Optimize(LegalPool()[..5], Rules()));
        Assert.Null(FantasyOptimizer.Optimize(LegalPool(), Rules(1)));
        Assert.Throws<OperationCanceledException>(() => FantasyOptimizer.Optimize(LegalPool(), Rules(), new(true)));
    }

    [Fact]
    public void ProjectionUsesFullDistributionAndIndependentQ1Contribution()
    {
        var final = new FantasyPositionDistribution(new Dictionary<int, decimal> { [1] = .2m, [2] = .3m, [4] = .1m }, .4m);
        var q1 = new FantasyPositionDistribution(new Dictionary<int, decimal> { [1] = .1m, [2] = .2m, [3] = .3m, [4] = .1m }, .3m);
        var men = FantasyScoring.Project(final, q1, Rules(), true, "male");
        var women = FantasyScoring.Project(final, q1, Rules(), true, "female");
        Assert.Equal(245m, men.ExpectedFinalPoints);
        Assert.Equal(40m, men.ExpectedQ1Bonus);
        Assert.Equal(285m, men.ExpectedFantasyPoints);
        Assert.Equal(240m, women.ExpectedFinalPoints);
        Assert.Equal(0, FantasyScoring.Project(final, null, Rules(), false, "male").ExpectedQ1Bonus);
    }

    [Fact]
    public void ProjectionDoesNotSilentlyInventMissingMassOrQ1()
    {
        var incomplete = new FantasyPositionDistribution(new Dictionary<int, decimal> { [1] = .5m }, 0);
        Assert.Throws<ArgumentException>(() => FantasyScoring.Project(incomplete, null, Rules(), false, "male"));
        var complete = incomplete with { NonScoringProbability = .5m };
        Assert.Throws<ArgumentException>(() => FantasyScoring.Project(complete, null, Rules(), true, "male"));
        Assert.Throws<ArgumentException>(() => FantasyScoring.Project(complete, null, Rules(), false, "open"));
        Assert.Equal(0, FantasyScoring.Project(new(new Dictionary<int, decimal>(), 1), null, Rules(), false, "male").ExpectedFantasyPoints);
    }

    [Fact]
    public void DisplayedPositionUsesTheSameCalibratedDistributionAsPointsAndExcludesNonFinishMass()
    {
        var raw = new Dictionary<int, decimal> { [1] = 1m, [2] = 0m };
        var calibration = new FantasyStageCalibration(.5m,
            new Dictionary<int, decimal> { [0] = .5m, [5] = .5m }, 20, 2, DateTimeOffset.UtcNow.AddDays(-1));
        var calibrated = FantasyCalibration.Apply(raw, calibration);
        Assert.Equal(13m / 12m, FantasyScoring.ExpectedClassifiedPosition(calibrated));
        Assert.NotEqual(1m, FantasyScoring.ExpectedClassifiedPosition(calibrated));
        Assert.Null(FantasyScoring.ExpectedClassifiedPosition(new(new Dictionary<int, decimal>(), 1m)));
        Assert.Throws<ArgumentException>(() => FantasyScoring.ExpectedClassifiedPosition(new(raw, .5m)));
    }

    [Theory]
    [InlineData("DNF")]
    [InlineData("DNS")]
    [InlineData("DSQ")]
    [InlineData("DNQ")]
    public void NonScoringStatusIsNotLastPlaceAndQ1RetentionRequiresVerifiedPolicy(string status)
    {
        Assert.Equal(0, FantasyScoring.ActualPoints(1, status, null, true, FantasyQ1Retention.Unverified, Rules(), "male"));
        Assert.Throws<InvalidOperationException>(() => FantasyScoring.ActualPoints(1, status, 1, true, FantasyQ1Retention.Unverified, Rules(), "male"));
        Assert.Equal(100, FantasyScoring.ActualPoints(1, status, 1, true, FantasyQ1Retention.Retained, Rules(), "male"));
        Assert.Equal(0, FantasyScoring.ActualPoints(1, status, 1, true, FantasyQ1Retention.Forfeited, Rules(), "male"));
    }

    [Fact]
    public void BacktestUsesTiedRanksAndDoesNotFallbackToCurrentPrices()
    {
        FantasyBacktestRider[] observations = [new("a", "1", "male", 10, 20), new("b", "2", "male", 10, 20), new("c", "3", "female", 20, 40)];
        var cutoff = DateTimeOffset.Parse("2024-06-14T00:00:00Z");
        var report = FantasyBacktester.Evaluate(observations, Rules(), "23", cutoff, cutoff.AddDays(-1),
            [new("a", "23", 100_000, cutoff.AddYears(2))], 2);
        Assert.Equal(1d, report.SpearmanRankCorrelation);
        Assert.Equal(40d / 3, report.MeanAbsoluteError, 8);
        Assert.Equal(Math.Sqrt(200), report.RootMeanSquaredError, 8);
        Assert.Null(report.PredictedTeamActualPoints);
        Assert.Null(report.OracleActualPoints);
        Assert.Null(report.Regret);
        Assert.Contains("Historical prices", report.SalaryOptimizationUnavailableReason);
    }

    [Fact]
    public void HistoricalOracleRegretRequiresAllRealTargetRoundPrices()
    {
        var cutoff = DateTimeOffset.Parse("2024-06-14T00:00:00Z");
        var observations = LegalPool().Select(x => new FantasyBacktestRider(x.PinkbikeExternalId, x.RiderId, x.Gender, 10, 20)).ToArray();
        var prices = LegalPool().Select(x => new FantasyHistoricalPrice(x.PinkbikeExternalId, "23", x.Price, cutoff.AddDays(-2))).ToArray();
        var report = FantasyBacktester.Evaluate(observations, Rules(), "23", cutoff, cutoff.AddDays(-1), prices);
        Assert.Equal(120m, report.PredictedTeamActualPoints);
        Assert.Equal(120m, report.OracleActualPoints);
        Assert.Equal(0m, report.Regret);
        Assert.Null(report.SpearmanRankCorrelation);
        Assert.Null(report.SalaryOptimizationUnavailableReason);
        Assert.Null(FantasyBacktester.Evaluate(observations, Rules(), "different-round", cutoff, cutoff.AddDays(-1), prices).OracleActualPoints);
        var mixedTimes = prices.Select((price, index) => price with { FetchedAtUtc = price.FetchedAtUtc.AddMinutes(index) }).ToArray();
        Assert.Null(FantasyBacktester.Evaluate(observations, Rules(), "23", cutoff, cutoff.AddDays(-1), mixedTimes).OracleActualPoints);
        Assert.Throws<ArgumentException>(() => FantasyBacktester.Evaluate(observations, Rules(), "23", cutoff, cutoff, prices));
    }

    [Fact]
    public void HistoricalRegretUsesActualPointsOfTheChosenLineupNotItsPrediction()
    {
        var cutoff = DateTimeOffset.Parse("2024-06-14T00:00:00Z");
        FantasyBacktestRider[] observations = [
            .. LegalPool().Select(x => new FantasyBacktestRider(x.PinkbikeExternalId, x.RiderId, x.Gender, 10, 20)),
            new("unexpected-winner", "7", "male", 5, 100)
        ];
        var prices = observations.Select(x => new FantasyHistoricalPrice(x.PinkbikeExternalId, "23", 200_000, cutoff.AddDays(-2))).ToArray();
        var report = FantasyBacktester.Evaluate(observations, Rules(), "23", cutoff, cutoff.AddDays(-1), prices);
        Assert.Equal(120m, report.PredictedTeamActualPoints);
        Assert.Equal(200m, report.OracleActualPoints);
        Assert.Equal(80m, report.Regret);
    }

    [Fact]
    public void EventFingerprintTracksRelevantConfigurationButNotStartListOrdering()
    {
        var first = new FantasyEventOptions { Q1BonusApplies = true, StartListRiderIds = ["2", "1"] };
        var reordered = new FantasyEventOptions { Q1BonusApplies = true, StartListRiderIds = ["1", "2", "1"] };
        Assert.Equal(FantasyPreparationService.EventFingerprint(first), FantasyPreparationService.EventFingerprint(reordered));
        reordered.Q1BonusApplies = false;
        Assert.NotEqual(FantasyPreparationService.EventFingerprint(first), FantasyPreparationService.EventFingerprint(reordered));
        Assert.NotEqual(FantasyPreparationService.EventFingerprint(null), FantasyPreparationService.EventFingerprint(first));
    }

    [Fact]
    public void MissingFinalIsNonScoringOnlyForKnownQualifiersAndCompletedImportedField()
    {
        var session = new JsonObject { ["id"] = "final", ["status"] = "completed" };
        var q1 = new JsonObject { ["rider_id"] = "1", ["position"] = 10 };
        var fieldResult = new JsonObject { ["session_id"] = "final", ["rider_id"] = "2", ["position"] = 1 };
        var round = new FantasyRoundData(new JsonObject { ["id"] = "round", ["season_year"] = 2026 },
            new RankingDataBundle([session], [fieldResult], [], [], new Dictionary<string, decimal>()));
        var missing = FantasyPreparationService.ResolveFinalResult(round, session, q1, "1");
        Assert.Equal("NO_SCORING_FINAL", missing?["status"]?.GetValue<string>());
        Assert.Same(fieldResult, FantasyPreparationService.ResolveFinalResult(round, session, null, "2"));
        Assert.Null(FantasyPreparationService.ResolveFinalResult(round, session, null, "1"));
        session["status"] = "live";
        Assert.Null(FantasyPreparationService.ResolveFinalResult(round, session, q1, "1"));
        session["status"] = "completed";
        Assert.Null(FantasyPreparationService.ResolveFinalResult(round with { Data = round.Data with { Results = [] } }, session, q1, "1"));
    }

    [Fact]
    public void PreparationDoesNotTreatProvisionalLiveResultsAsHistoricalOutcomes()
    {
        var final = JsonNode.Parse("""{"id":"final","session_type":"FINAL","status":"live","session_date":"2026-08-02T12:00:00Z","categories":{"code":"EM"}}""")!.AsObject();
        var q1 = JsonNode.Parse("""{"id":"q1","session_type":"Q1","status":"completed","session_date":"2026-08-01T12:00:00Z","categories":{"code":"EM"}}""")!.AsObject();
        var round = new FantasyRoundData(new JsonObject { ["id"] = "round", ["season_year"] = 2026 },
            new RankingDataBundle([final, q1], [], [], [], new Dictionary<string, decimal>()));
        var stages = FantasyPreparationService.Stages(round, "EM");
        Assert.Null(stages.Final);
        Assert.Same(q1, stages.Q1);
        final["status"] = "finished";
        Assert.Same(final, FantasyPreparationService.Stages(round, "EM").Final);
        Assert.Null(FantasyPreparationService.Stages(round, "EW").Final);
    }

    [Fact]
    public void HistoricalSalaryEvaluationUsesOnlyVerifiedContemporaneousPricesAndCompleteCombinedGenderPool()
    {
        var start = DateTimeOffset.Parse("2026-06-14T00:00:00Z");
        var round = new FantasyRoundData(new JsonObject { ["id"] = "round", ["season_year"] = 2026, ["start_date"] = start.ToString("O") },
            new RankingDataBundle([], [], [], [], new Dictionary<string, decimal>()));
        var catalogue = LegalPool().Select(rider => new PinkbikeFantasyRider(rider.PinkbikeExternalId, rider.RiderId,
            rider.PinkbikeExternalId, rider.Gender, rider.Price, 0, false, start.AddDays(-1), "matched", null, [])).ToArray();
        var snapshot = new FantasyPriceSnapshot("snapshot", 2026, round.Id, start.AddDays(-1), catalogue, "https://example.com/athletes", "test");
        var entryList = new FantasyEventOptions { StartListRiderIds = catalogue.Select(rider => rider.RiderId!).ToList(),
            StartListFetchedAtUtc = start.AddDays(-2), StartListSourceUrl = "https://example.com/entries" };
        var observations = catalogue.Select(rider => new FantasyBacktestRider(rider.RiderId!, rider.RiderId, rider.Gender, 10, 20)).ToArray();
        var warnings = new HashSet<string>();
        var result = FantasyPreparationService.EvaluateHistoricalSalary(round, snapshot, entryList, observations, start.AddDays(-7), warnings);
        Assert.NotNull(result);
        Assert.Equal(120m, result.PredictedTeamActualPoints);
        Assert.Equal(120m, result.OracleActualPoints);
        Assert.Equal(0m, result.Regret);
        Assert.Empty(warnings);
        Assert.Null(FantasyPreparationService.EvaluateHistoricalSalary(round, snapshot, entryList, observations[..5], start.AddDays(-7), warnings));
        Assert.Contains(warnings, warning => warning.Contains("missing, ambiguous"));
        Assert.False(FantasyPreparationService.IsHistoricalSalaryContext(round, snapshot with { FetchedAtUtc = start }, entryList));
        Assert.False(FantasyPreparationService.IsHistoricalSalaryContext(round, snapshot with { UpcomingRoundId = "other-round" }, entryList));
        entryList.StartListFetchedAtUtc = start;
        Assert.False(FantasyPreparationService.IsHistoricalSalaryContext(round, snapshot, entryList));
        entryList.StartListFetchedAtUtc = start.AddDays(-1);
        entryList.StartListSourceUrl = "http://example.com/unverified";
        Assert.False(FantasyPreparationService.IsHistoricalSalaryContext(round, snapshot, entryList));
    }

    [Fact]
    public async Task PreparedForecastStoreRoundTripsAndCanceledWritePreservesLastSuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "racetrace-fantasy-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FantasyPreparedStore(Options.Create(new FantasyOptions { DataDirectory = "fantasy" }),
                new TestEnvironment { ContentRootPath = directory });
            Assert.Null(await store.ReadAsync("../round"));
            var prepared = new FantasyPreparedRound(2026, "../round", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "engine", "projection", "rules", "fingerprint", [], [], []);
            await store.WriteAsync(prepared);
            Assert.Equal(prepared.RoundId, (await store.ReadAsync("../round"))?.RoundId);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "fantasy", "predictions"), "*.json"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(prepared with { EngineVersion = "new" }, new(true)));
            Assert.Equal("engine", (await store.ReadAsync("../round"))?.EngineVersion);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "fantasy", "predictions"), "*.tmp"));
        }
        finally
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "racetrace-fantasy-store-tests")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "FantasyTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static FantasyCandidate[] LegalPool() => Enumerable.Range(1, 6)
        .Select(i => new FantasyCandidate($"pb-{i}", i.ToString(), i <= 4 ? "male" : "female", 200_000m, 10m)).ToArray();
}
