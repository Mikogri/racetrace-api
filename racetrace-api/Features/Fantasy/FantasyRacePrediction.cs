using System.Globalization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Fantasy;

internal sealed record FantasyRoundData(JsonObject Round, RankingDataBundle Data)
{
    public string Id => Round.String("id")!;
    public int Season => Round.Int("season_year")!.Value;
    public DateTimeOffset Start => DateTimeOffset.Parse(Round.String("start_date")!, CultureInfo.InvariantCulture);
    public string? TrackId => Round.String("track_id");
}

internal sealed record FantasyRaceForecast(string RiderId, string Name, string? CountryCode, decimal Confidence,
    decimal ExpectedPosition, IReadOnlyDictionary<int, decimal> Positions);

/// <summary>Data adapter for the existing prediction engine, not a second race model.</summary>
public sealed class FantasyRacePrediction(
    ISupabaseRestClient supabase, DomainQueryService queries, MetricsService metrics, IMemoryCache cache, DataRevisionService? revisionService = null)
{
    public static string EngineVersion => AlgorithmConfiguration.Podium.ModelVersion;
    internal bool CoherenceEnabled => revisionService?.Enabled == true;
    private readonly Dictionary<string, IReadOnlyDictionary<string, RiderPerformanceMetricValues>> _metricMemo = new();
    private string? _metricRevision;

    internal async Task<string> DataRevisionAsync(CancellationToken token, bool forceRefresh = false)
    {
        if (revisionService?.Enabled == true)
        {
            var sharedRevision = (await revisionService.GetAsync(token, forceRefresh)).ForContext(null);
            ResetMetricsForRevision(sharedRevision);
            return sharedRevision;
        }
        const string key = "fantasy-source-revision";
        if (!forceRefresh && cache.TryGetValue<string>(key, out var cached))
        {
            ResetMetricsForRevision(cached!);
            return cached!;
        }
        // Counts detect deletions; the latest update detects imports/corrections. Read-only,
        // bounded metadata queries, never a download of the full result history on page views.
        var revisions = await Task.WhenAll(new[] { "rounds", "sessions", "results", "time_splits" }.Select(async table =>
        {
            var response = await supabase.GetAsync(new SupabaseQuery(table).Select("id,updated_at")
                .Add("order", "updated_at.desc.nullslast").Limit(1), count: true, cancellationToken: token);
            if (response.Total is null) throw new InvalidDataException("Cannot verify the revision of Fantasy source data.");
            return $"{table}:{response.Total}:{response.Data.FirstOrDefault()?.String("id")}:{response.Data.FirstOrDefault()?.String("updated_at")}";
        }));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', revisions))));
        cache.Set(key, revision, TimeSpan.FromMinutes(1));
        ResetMetricsForRevision(revision);
        return revision;
    }

    private void ResetMetricsForRevision(string revision)
    {
        if (_metricRevision == revision) return;
        _metricMemo.Clear();
        _metricRevision = revision;
    }

    internal async Task<IReadOnlyList<JsonObject>> RoundsAsync(int season, CancellationToken token)
    {
        var key = $"fantasy-calendar:{season}:{await DataRevisionAsync(token)}";
        if (cache.TryGetValue<IReadOnlyList<JsonObject>>(key, out var cached)) return cached!;
        var response = await supabase.GetAsync(new SupabaseQuery("rounds")
            .Select("id,season_year,round_number,track_id,start_date,end_date,status,context_name,competition_levels(code),tracks(name,venue,country_code)")
            .Filter("season_year", "gte", Math.Max(2021, season - 3)).Filter("season_year", "lte", season)
            .Order("start_date", false).Limit(1000), cancellationToken: token);
        var rounds = response.Data.Where(round => round.Object("competition_levels")?.String("code") is "world_cup" or "world_championships" or "world_championship").ToArray();
        cache.Set<IReadOnlyList<JsonObject>>(key, rounds, TimeSpan.FromMinutes(1));
        return rounds;
    }

    internal async Task<FantasyRoundData> LoadAsync(JsonObject round, CancellationToken token)
    {
        var key = $"fantasy-round-data:{await DataRevisionAsync(token)}:{round.String("id")}";
        if (cache.TryGetValue<RankingDataBundle>(key, out var cached)) return new(round, cached!);
        var context = Context(round.String("id"));
        var sessions = await queries.GetMatchingSessionsAsync(context, cancellationToken: token);
        var data = sessions.Count == 0 ? new RankingDataBundle([], [], [], [], new Dictionary<string, decimal>()) :
            await queries.GetRankingDataAsync(context, resolvedSessionIds: sessions.Select(session => session.String("id")!), cancellationToken: token);
        data = data with { Sessions = sessions };
        cache.Set(key, data, TimeSpan.FromMinutes(5));
        return new(round, data);
    }

    internal async Task<IReadOnlyList<FantasyRaceForecast>> ForecastAsync(
        IReadOnlyList<FantasyRoundData> archive, JsonObject target, string category,
        int iterations, IReadOnlySet<string>? startList, CancellationToken token)
    {
        var cutoff = DateTimeOffset.Parse(target.String("start_date")!, CultureInfo.InvariantCulture);
        var season = target.Int("season_year")!.Value;
        var past = archive.Where(round => round.Start < cutoff && round.Id != target.String("id")).ToArray();
        var current = Combine(past.Where(round => round.Season == season), category, cutoff);
        var previous = Combine(past.Where(round => round.Season == season - 1), category, cutoff);
        var currentMetrics = await MetricsAsync(current, token);
        var previousMetrics = await MetricsAsync(previous, token);
        var trackMetrics = new Dictionary<string, RiderPerformanceMetricValues>();
        foreach (var trackRound in past.Where(round => round.TrackId == target.String("track_id") && round.Season >= season - 2).OrderBy(round => round.Start))
            foreach (var pair in await MetricsAsync(Combine([trackRound], category, cutoff), token)) trackMetrics[pair.Key] = pair.Value;

        var options = AlgorithmConfiguration.Podium with { MonteCarloIterations = iterations };
        var builder = new PodiumPredictionFeatureBuilder(options);
        var historical = current.Results.Concat(previous.Results).ToArray();
        // Opening rounds have no current-season measurements; use previous-season evidence, explicitly.
        var candidates = currentMetrics.Count == 0 ? previousMetrics : currentMetrics;
        var isWch = target.Object("competition_levels")?.String("code")?.Contains("champ", StringComparison.OrdinalIgnoreCase) == true;
        var roundsRemaining = Math.Max(1, (await RoundsAsync(season, token)).Count(round => round.Int("season_year") == season &&
            DateTimeOffset.Parse(round.String("start_date")!, CultureInfo.InvariantCulture) >= cutoff));
        var features = candidates.Where(pair => pair.Value.Pace.HasValue && (startList is null || startList.Contains(pair.Key)))
            .Select(pair => builder.Build(pair.Key, pair.Value, previousMetrics.GetValueOrDefault(pair.Key), trackMetrics.GetValueOrDefault(pair.Key),
                historical, [], null, isWch,
                roundsRemaining, season - 1))
            .OrderByDescending(value => value.Rating).ThenByDescending(value => value.Confidence).ThenBy(value => value.RiderId, StringComparer.Ordinal).ToArray();
        if (features.Length == 0) return [];
        var distributions = new PodiumPredictionCalibration(options).Calculate(features, isWch);
        var simulations = new MonteCarloRaceSimulator(options).SimulateDetailed(distributions);
        var identities = historical.Where(result => result.Object("riders") is not null).GroupBy(result => result.String("rider_id")!)
            .ToDictionary(group => group.Key, group => group.Last().Object("riders")!);
        return features.Select((feature, index) => new FantasyRaceForecast(feature.RiderId,
            identities.GetValueOrDefault(feature.RiderId)?.DisplayName() ?? feature.RiderId,
            identities.GetValueOrDefault(feature.RiderId)?.String("nation"), (decimal)feature.Confidence,
            (decimal)simulations[index].Summary.ExpectedPosition,
            simulations[index].PositionProbabilities.Select((probability, position) => (probability, position))
                .ToDictionary(value => value.position + 1, value => (decimal)value.probability))).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, RiderPerformanceMetricValues>> MetricsAsync(RankingDataBundle data, CancellationToken token)
    {
        var key = string.Join(',', data.Sessions.Select(session => session.String("id")).Order());
        if (!_metricMemo.TryGetValue(key, out var result))
        {
            result = await metrics.CalculatePerformanceByRiderAsync(data.Results, Context(null), token,
                resultsContainFullField: true, availableSessions: data.Sessions, suppliedSplits: data.Splits, suppliedFieldResults: data.FieldResults);
            _metricMemo[key] = result;
        }
        return result;
    }

    internal static string Category(JsonObject session) => session.Object("round_categories")?.Object("categories")?.String("code") ?? session.Object("categories")?.String("code") ?? "";
    internal static bool IsCompleted(JsonObject session) => session.String("status")?.Trim().ToLowerInvariant() is "completed" or "finished" or "final";
    internal static DateTimeOffset SessionDate(JsonObject session) => DateTimeOffset.TryParse(session.String("session_date"), CultureInfo.InvariantCulture, out var date)
        ? date : throw new InvalidDataException("A historical session is missing its date; the prediction cutoff cannot be verified.");

    internal static RankingDataBundle Combine(IEnumerable<FantasyRoundData> rounds, string category, DateTimeOffset cutoff)
    {
        var past = rounds.Where(round => round.Start < cutoff).ToArray();
        var sessions = past.SelectMany(round => round.Data.Sessions).Where(session => IsCompleted(session) && Category(session) == category && SessionDate(session) < cutoff).ToArray();
        var ids = sessions.Select(session => session.String("id")).ToHashSet();
        var results = past.SelectMany(round => round.Data.Results).Where(result => ids.Contains(result.String("session_id"))).ToArray();
        var resultIds = results.Select(result => result.String("id")).ToHashSet();
        return new(sessions, results, past.SelectMany(round => round.Data.FieldResults).Where(result => ids.Contains(result.String("session_id"))).ToArray(),
            past.SelectMany(round => round.Data.Splits).Where(split => resultIds.Contains(split.String("result_id"))).ToArray(),
            past.SelectMany(round => round.Data.PointsByResultId).Where(pair => resultIds.Contains(pair.Key)).ToDictionary());
    }

    private static RiderMetricsContext Context(string? roundId) => new(null, null, null, null, roundId, null, null, null, null, null, null) { CategoryCodes = ["EM", "EW"] };
}
