using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.PodiumBacktest;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int Seed = 20260824;
    private static readonly string[] Models = ["production-equations", "rating-only", "pace-only", "clean-pace", "clean-pace-risk", "clean-pace-track-risk"];

    public static async Task Main(string[] args)
    {
        if (args.Contains("--incident-cache-audit"))
        {
            await IncidentCacheAudit.RunAsync(args);
            return;
        }
        if (args.Contains("--incident-study"))
        {
            await IncidentDetectorStudy.RunAsync(args);
            return;
        }
        if (args.Contains("--ranking-incident-comparison"))
        {
            await RankingIncidentComparison.RunAsync(args);
            return;
        }
        if (args.Contains("--incident-audit"))
        {
            await IncidentAudit.RunAsync(args);
            return;
        }
        string Argument(string key, string fallback) => Array.IndexOf(args, key) is var index && index >= 0 ? args[index + 1] : fallback;
        var apiDirectory = Path.GetFullPath(Argument("--api", "racetrace-api"));
        var output = Path.GetFullPath(Argument("--output", "artifacts/podium-backtest"));
        var iterations = int.Parse(Argument("--iterations", "10000"), CultureInfo.InvariantCulture);
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(apiDirectory, "appsettings.Development.json")))!;
        var options = config["Supabase"]!.Deserialize<SupabaseOptions>()!;
        using var http = new HttpClient(new ReadOnlyCacheHandler(Path.Combine(output, "cache"), args.Contains("--offline"))) { Timeout = TimeSpan.FromMinutes(3) };
        var client = new SupabaseRestClient(http, Options.Create(options));
        var domain = new DomainQueryService(client);
        var pace = new PaceService(client);
        var metrics = new MetricsService(domain, pace);
        var rounds = (await client.GetAsync(new SupabaseQuery("rounds")
            .Select("id,season_year,round_number,start_date,track_id,competition_levels(code),tracks(name,venue)")
            .Filter("season_year", "gte", 2021).Filter("season_year", "lte", 2024)
            .Order("start_date", false).Limit(1000))).Data;
        var history = new List<HistoricalRound>();
        foreach (var round in rounds)
        {
            var context = HistoricalData.Context(round.String("id"));
            var sessions = await domain.GetMatchingSessionsAsync(context);
            var data = sessions.Count == 0 ? new RankingDataBundle([], [], [], [], new Dictionary<string, decimal>()) :
                await domain.GetRankingDataAsync(context, resolvedSessionIds: sessions.Select(session => session.String("id")!));
            data = data with { Sessions = sessions };
            var observations = pace.CalculateSessionPaceObservations(data);
            history.Add(new HistoricalRound(round, data, observations));
            Console.WriteLine($"Loaded {round.Int("season_year")} R{round.Int("round_number")} / {round.String("id")}: {sessions.Count} sessions, {data.Results.Count} results, {data.Splits.Count} splits, {observations.Count} pace observations.");
        }
        Directory.CreateDirectory(output);
        if (args.Contains("--download-only")) return;

        var metricCache = new Dictionary<string, IReadOnlyDictionary<string, RiderPerformanceMetricValues>>();
        async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> Calculate(RankingDataBundle data)
        {
            var key = string.Join(",", data.Sessions.Select(session => session.String("id")).Order());
            if (!metricCache.TryGetValue(key, out var values))
            {
                values = await metrics.CalculatePerformanceByRiderAsync(data.Results, HistoricalData.Context(),
                    resultsContainFullField: true, availableSessions: data.Sessions, suppliedSplits: data.Splits, suppliedFieldResults: data.FieldResults);
                metricCache[key] = values;
            }
            return values;
        }

        var reports = new List<EventReport>();
        foreach (var target in history.Where(round => round.WorldCup && round.Year >= 2022 && round.Number >= 2))
        foreach (var category in new[] { "EM", "EW" })
        {
            var finalSessions = target.Data.Sessions.Where(session => HistoricalData.Category(session) == category &&
                PaceService.ParseSessionType(session.String("session_type"), session.String("name")) == PaceSessionType.Final).ToArray();
            if (finalSessions.Length != 1)
            {
                Console.WriteLine($"Skipping {target.Year} R{target.Number} {category}: {finalSessions.Length} final sessions (expected exactly one).");
                continue;
            }
            var finalResults = target.Data.Results.Where(result => result.String("session_id") == finalSessions[0].String("id")).ToArray();
            var actual = finalResults.ToDictionary(result => result.String("rider_id")!, result =>
                result.Int("position") is > 0 && !new[] { "DNF", "DNS", "DSQ" }.Contains(result.String("status")?.ToUpperInvariant())
                    ? result.Int("position")!.Value : int.MaxValue);
            if (!new[] { 1, 2, 3 }.All(position => actual.Values.Contains(position)))
            {
                Console.WriteLine($"Skipping {target.Year} R{target.Number} {category}: incomplete recorded podium.");
                continue;
            }
            var past = history.Where(round => round.Start < target.Start).ToArray();
            var all = HistoricalData.Combine(past, category, target.Start);
            var current = HistoricalData.Combine(past.Where(round => round.Year == target.Year), category, target.Start);
            var previous = HistoricalData.Combine(past.Where(round => round.Year == target.Year - 1), category, target.Start);
            var currentMetrics = await Calculate(current);
            var previousMetrics = await Calculate(previous);
            var historical = current.Results.Concat(previous.Results).ToArray();
            var trackMetrics = new Dictionary<string, RiderPerformanceMetricValues>();
            foreach (var priorTrack in past.Where(round => round.TrackId == target.TrackId && round.Year >= target.Year - 2).OrderBy(round => round.Start))
                foreach (var pair in await Calculate(HistoricalData.Combine([priorTrack], category, target.Start)))
                    trackMetrics[pair.Key] = pair.Value;
            var predictionOptions = new PodiumPredictionOptions { MonteCarloIterations = iterations, RandomSeed = Seed };
            var builder = new PodiumPredictionFeatureBuilder(predictionOptions);
            // Same historical cohort for all variants. Target entrants/results never choose candidates.
            var candidates = currentMetrics.Where(pair => pair.Value.PaceConfidence >= 35)
                .Select(pair => builder.Build(pair.Key, pair.Value, previousMetrics.GetValueOrDefault(pair.Key), trackMetrics.GetValueOrDefault(pair.Key),
                    historical, [], null, false, history.Count(round => round.Year == target.Year && round.Start >= target.Start), target.Year - 1))
                .OrderByDescending(value => value.Rating).ThenByDescending(value => value.Confidence).ThenBy(value => value.RiderId)
                .Take(30).ToArray();
            if (candidates.Length < 3) throw new InvalidOperationException($"Insufficient candidate history for {target.Id}/{category}.");
            var sessionMap = all.Sessions.ToDictionary(session => session.String("id")!);
            var observationMap = HistoricalData.Observations(past, all).ToDictionary(value => (value.SessionId, value.RiderId));
            var runs = all.Results.GroupBy(result => result.String("rider_id")!).ToDictionary(group => group.Key, group => group.Select(result =>
            {
                var session = sessionMap[result.String("session_id")!];
                observationMap.TryGetValue((result.String("session_id")!, group.Key), out var observation);
                var age = (target.Start - HistoricalData.SessionDate(session)).TotalDays;
                var status = result.String("status")?.ToUpperInvariant();
                var finished = result.Int("position") is > 0 && status is not ("DNS" or "DNF" or "DSQ");
                // Normalize exposure to one final. Missing pace must not reduce DNF exposure.
                var stageWeights = new SessionWeightOptions();
                var stageWeight = stageWeights.GetWeight(PaceService.ParseSessionType(session.String("session_type"), session.String("name"))) / stageWeights.Final;
                var weight = status is "DNS" or "DSQ" ? 0 : Math.Pow(0.5, age / 180) * stageWeight;
                return new HistoricalRun(observation?.Result.Pace, weight, observation?.Result.IncidentSectorIndex is not null,
                    status == "DNF", finished, session.Object("round_categories")?.Object("rounds")?.String("track_id") == target.TrackId);
            }).ToArray());
            var names = all.Results.Concat(finalResults).Where(result => result.Object("riders") is not null)
                .GroupBy(result => result.String("rider_id")!).ToDictionary(group => group.Key, group => group.Last().Object("riders")!.DisplayName());
            var modelReports = new List<ModelReport>();
            foreach (var model in Models)
            {
                IReadOnlyList<MonteCarloPrediction> simulations;
                if (model.StartsWith("clean-"))
                {
                    var distributions = candidates.Select(candidate => CleanPaceExperiment.Fit(candidate.RiderId,
                        runs.GetValueOrDefault(candidate.RiderId) ?? [], candidate.Pace, model.Contains("risk"), model.Contains("track"))).ToArray();
                    simulations = CleanPaceExperiment.Simulate(distributions, iterations, Seed);
                }
                else
                {
                    var distributions = new PodiumPredictionCalibration(predictionOptions).Calculate(candidates, false);
                    if (model != "production-equations")
                        distributions = distributions.Select(value => value with
                        {
                            ExpectedPerformance = model == "rating-only" ? value.Features.Rating : value.Features.Pace
                        }).ToArray();
                    simulations = new MonteCarloRaceSimulator(predictionOptions).Simulate(distributions);
                }
                var scores = candidates.Select((candidate, index) => new RiderPrediction(candidate.RiderId,
                    names.GetValueOrDefault(candidate.RiderId, candidate.RiderId), simulations[index].PodiumProbability, simulations[index].WinProbability,
                    simulations[index].ExpectedPosition, actual.GetValueOrDefault(candidate.RiderId, int.MaxValue)))
                    .OrderByDescending(value => value.PodiumProbability).ThenBy(value => value.ExpectedPosition).ToArray();
                var candidateIds = candidates.Select(candidate => candidate.RiderId).ToHashSet();
                // Include missed podium riders with probability zero, including those outside the cohort.
                var evaluation = scores.Select(value => new PodiumBacktestPrediction(target.Id + "/" + category, value.RiderId, value.ActualPosition,
                    value.PodiumProbability, value.PodiumProbability, value.WinProbability)).Concat(actual.Where(pair => !candidateIds.Contains(pair.Key)).Select(pair =>
                        new PodiumBacktestPrediction(target.Id + "/" + category, pair.Key, pair.Value, 0, -1, 0))).ToArray();
                modelReports.Add(new ModelReport(model, PodiumPredictionBacktester.Evaluate(evaluation), evaluation,
                    scores.OrderByDescending(value => value.PodiumProbability).ThenBy(value => value.ExpectedPosition).Take(10).ToArray()));
            }
            var report = new EventReport(target.Year, target.Number, category, target.Id, target.Venue, target.Start,
                all.Sessions.Count, all.Results.Count, observationMap.Values.Count(value => value.Result.IncidentSectorIndex.HasValue),
                candidates.Length, candidates.Count(candidate => actual.ContainsKey(candidate.RiderId)),
                actual.Where(pair => pair.Value <= 3).OrderBy(pair => pair.Value).Select(pair => names.GetValueOrDefault(pair.Key, pair.Key)).ToArray(), modelReports);
            reports.Add(report);
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(reports, Json));
            Console.WriteLine($"Tested {target.Year} R{target.Number} {category}: {all.Results.Count} historical results; " +
                string.Join("; ", modelReports.Select(model => $"{model.Model}={model.Metrics.PodiumRecallAt3:P0}")));
        }
        if (!reports.Any(value => value.Year == 2024 && value.Round == 4 && value.Category == "EM") ||
            !reports.Any(value => value.Year == 2024 && value.Round == 4 && value.Category == "EW") ||
            !reports.Any(value => value.Year < 2024))
            throw new InvalidOperationException("Missing required development or target events; no successful benchmark report can be produced.");
        var markdown = Report(reports, iterations);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), markdown);
        Console.WriteLine(markdown);
    }

    private static string Report(List<EventReport> reports, int iterations)
    {
        var text = new StringBuilder("# Historical podium prediction experiment\n\n");
        text.AppendLine($"Seed: {Seed}; Monte Carlo iterations per event/model: {iterations:N0}. History starts 2021-01-01.\n");
        text.AppendLine("Pre-weekend prediction: all target-weekend sessions are excluded. No target weather or qualifying results. Same top-30 current-season confidence >=35 cohort for every model, chosen before inspecting target outcomes. Non-attendees are NOT removed; missed podium riders count as misses. Probabilities assume the historical cohort participates, not a verified entry list.\n");
        text.AppendLine("Production-equations is a pre-weekend harness of the existing feature builder/calibration/simulator, not a call to the live endpoint (which can see target qualifying). Clean models use all eligible 2021+ history, 180-day half-life, the unchanged sector incident detector, separate clean variability and mutually exclusive incident/DNF outcomes. Fixed experimental priors; not statistically calibrated probabilities. No production model is automatically replaced.\n");
        foreach (var split in new[] { "development-2022-2023", "holdout-2024", "focus-2024-round-4" })
        {
            var events = reports.Where(value => split == "development-2022-2023" ? value.Year < 2024 : value.Year == 2024 && (split != "focus-2024-round-4" || value.Round == 4)).ToArray();
            text.AppendLine($"## {split} ({events.Length} category-events)\n");
            text.AppendLine("| Model | Winner | Podium in top 3 | Podium in top 5 | Brier ↓ | Log loss ↓ | NDCG@3 ↑ |\n|---|---:|---:|---:|---:|---:|---:|");
            foreach (var model in Models)
            {
                var metrics = PodiumPredictionBacktester.Evaluate(events.SelectMany(value => value.Models.Single(item => item.Model == model).Evaluation).ToArray());
                text.AppendLine(FormattableString.Invariant($"| {model} | {metrics.WinnerAccuracy:P1} | {metrics.PodiumRecallAt3:P1} | {metrics.PodiumRecallAt5:P1} | {metrics.BrierScore:F4} | {metrics.LogLoss:F4} | {metrics.NdcgAt3:F4} |"));
            }
            text.AppendLine();
        }
        var selected = Models.OrderByDescending(model => PodiumPredictionBacktester.Evaluate(reports.Where(value => value.Year < 2024).SelectMany(value => value.Models.Single(item => item.Model == model).Evaluation).ToArray()).PodiumRecallAt3)
            .ThenBy(model => PodiumPredictionBacktester.Evaluate(reports.Where(value => value.Year < 2024).SelectMany(value => value.Models.Single(item => item.Model == model).Evaluation).ToArray()).BrierScore).First();
        text.AppendLine($"Development-only selection rule (recall@3, then Brier): **{selected}**. Evaluate this selection on holdout; do not select again on 2024.\n");
        foreach (var item in reports.Where(value => value.Year == 2024 && value.Round == 4))
        {
            text.AppendLine($"## 2024 round 4 — {item.Venue}, {item.Category}\n\nActual podium: {string.Join("; ", item.ActualPodium)}. History: {item.HistorySessions} sessions, {item.HistoryResults} results, {item.DetectedIncidents} detected incident observations. Cutoff: {item.Cutoff:O}.\n");
            text.AppendLine("| Model | Predicted top 3 (podium probability) |\n|---|---|");
            foreach (var model in item.Models)
                text.AppendLine($"| {model.Model} | {string.Join("; ", model.Top10.Take(3).Select(value => $"{value.Name} ({value.PodiumProbability:P1})"))} |");
            text.AppendLine();
        }
        text.AppendLine("## Limitations\n\nRetrospective data snapshot, not an archived as-of database: later corrections may be present. Current-season top-30 selection excludes newcomers and some returnees; actual podium exclusions are penalized, not hidden. No confirmed entry lists/injuries/weather forecasts. Both elite categories only. Earlier 2021 history is unavailable. All six fixed models are compared on the same cohort. Brier/log loss include non-podium entrants, so recall@3 and per-event details must also be considered. Single-race success is not evidence of general improvement.\n");
        return text.ToString();
    }
}

internal sealed record RiderPrediction(string RiderId, string Name, double PodiumProbability, double WinProbability, double ExpectedPosition, int ActualPosition);
internal sealed record ModelReport(string Model, PodiumBacktestMetrics Metrics, IReadOnlyList<PodiumBacktestPrediction> Evaluation, IReadOnlyList<RiderPrediction> Top10);
internal sealed record EventReport(int Year, int Round, string Category, string RoundId, string Venue, DateTimeOffset Cutoff,
    int HistorySessions, int HistoryResults, int DetectedIncidents, int Candidates, int CandidatesInFinal, IReadOnlyList<string> ActualPodium, IReadOnlyList<ModelReport> Models);
