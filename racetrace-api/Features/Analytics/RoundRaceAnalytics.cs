using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;
using RaceTrace.Api.Infrastructure.Telemetry;
using static RaceTrace.Api.Features.Results.ResultTimingProjection;

namespace RaceTrace.Api.Features.Analytics;

/// <summary>Pure round analytics. Times come from the results projection; Pace/incidents from the existing calculator.</summary>
internal static class RoundRaceAnalytics
{
    internal static RoundRaceAnalyticsDto Calculate(
        IReadOnlyList<JsonObject> results,
        IReadOnlyList<JsonObject> splits,
        IReadOnlyList<RiderSessionPaceObservation> observations,
        IReadOnlyList<JsonObject> preFinalResults,
        string? preFinalSession)
    {
        using var measurement = ApiTelemetry.Measure("round_analytics");
        var classified = results.Where(IsClassified).OrderBy(row => row.Int("position")).ToArray();
        var starters = results.Where(row => !string.Equals(row.String("status")?.Trim(), "DNS", StringComparison.OrdinalIgnoreCase)).ToArray();
        var eligible = starters.Where(row => !DownhillPaceCalculator.IsDisqualified(row.String("status"))).ToArray();
        var ids = eligible.Select(row => row.String("id")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validSplits = splits.Where(row => ids.Contains(row.String("result_id") ?? "")).ToArray();
        var projected = BuildSectorsByResultId(validSplits, eligible, includeFinishSector: true);
        // A missing intermediate split must not turn a multi-sector interval into one fast/slow sector.
        var timings = projected.Values.SelectMany(sectors => sectors.Where(sector =>
            sector.SectorTimeMs is > 0 && (sector.SplitIndex == 1 ||
                sectors.Any(previous => previous.SplitIndex == sector.SplitIndex - 1 && previous.RaceTimeMs is > 0))))
            .ToArray();
        var ranks = BuildSectorPositions(timings);
        var sectorCount = projected.Values.SelectMany(value => value).Select(value => value.SplitIndex).DefaultIfEmpty().Max();
        var efficiencies = timings.GroupBy(sector => sector.SplitIndex).OrderBy(group => group.Key)
            .Select(group => new SectorEfficiencyDto(group.Key, group.Count(), Density(group.Select(x => x.SectorTimeMs!.Value))))
            .ToArray();
        var correlation = Correlation(classified, preFinalResults, preFinalSession);
        var margin = WinningMargin(classified, timings, sectorCount);
        var winner = classified.FirstOrDefault(row => row.Int("position") == 1);
        RunEfficiencyDto? run = null;
        if (winner is not null)
        {
            var positions = ranks.Where(pair => pair.Key.ResultId == winner.String("id")).Select(pair => pair.Value).ToArray();
            run = new RunEfficiencyDto(PublicDtoMapping.Long(winner.String("rider_id")), Name(winner), sectorCount,
                positions.Length, positions.Count(rank => rank == 1), positions.Count(rank => rank <= 3),
                positions.Length > 0 ? Round(positions.Average()) : null, SectorsForGain(margin));
        }
        return new("round-analytics-v2", efficiencies, Chaos(starters, projected, observations, correlation),
            correlation, margin, run, Distribution(classified, observations));
    }

    internal static bool IsClassified(JsonObject row) => row.Int("position") is > 0 && row.Int("race_time_ms") is > 0 &&
        row.String("status")?.Trim().ToUpperInvariant() is not ("DNF" or "DNS" or "DSQ" or "DQ") &&
        !DownhillPaceCalculator.IsDisqualified(row.String("status"));

    internal static JsonObject? SelectPreFinal(IReadOnlyList<JsonObject> sessions, JsonObject final)
    {
        var finalDate = Date(final);
        var finalOrder = final.Int("session_order") ?? 0;
        return sessions.Where(session => Stage(session) > 0 &&
                RoundId(session) == RoundId(final) && CategoryId(session) == CategoryId(final) &&
                Date(session) is { } date && finalDate.HasValue && date <= finalDate.Value &&
                (date.Date != finalDate.Value.Date || finalOrder == 0 ||
                    (session.Int("session_order") ?? 0) == 0 || session.Int("session_order") < finalOrder))
            .OrderByDescending(Date).ThenByDescending(session => session.Int("session_order") ?? 0)
            .ThenByDescending(Stage).ThenByDescending(session => PublicDtoMapping.Long(session.String("id")))
            .FirstOrDefault();
    }

    internal static string PreFinalLabel(JsonObject session) => PaceService.ParseSessionType(session.String("session_type"), session.String("name")) switch
    {
        PaceSessionType.SemiFinal => "Semi", PaceSessionType.Qualifying2 => "Q2",
        PaceSessionType.Qualifying1 => "Q1", PaceSessionType.Seeding => "Seeding", _ => "Quali"
    };

    private static int Stage(JsonObject session) => PaceService.ParseSessionType(session.String("session_type"), session.String("name")) switch
    {
        PaceSessionType.Seeding => 1, PaceSessionType.Qualifying1 => 2, PaceSessionType.Qualifying => 2,
        PaceSessionType.Qualifying2 => 3, PaceSessionType.SemiFinal => 4, _ => 0
    };
    private static string? RoundId(JsonObject session) => session.String("round_id") ?? session.Object("round_categories")?.String("round_id");
    private static string? CategoryId(JsonObject session) => session.String("category_id") ?? session.Object("round_categories")?.String("category_id");
    private static DateTimeOffset? Date(JsonObject session) => DateTimeOffset.TryParse(session.String("session_date"), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static string Name(JsonObject row) => row.Object("riders")?.DisplayName() ?? "--";
    private static decimal Round(double value) => Math.Round((decimal)value, 3);

    // Median local rank/time slope; tied times use their average rank. Units are positions per second.
    private static decimal? Density(IEnumerable<int> milliseconds)
    {
        var ordered = milliseconds.Order().ToArray();
        if (ordered.Length < 3) return null;
        var ranks = AverageRanks(ordered.Select(value => (double)value).ToArray());
        var points = ordered.Select((time, index) => (Time: time, Rank: ranks[index])).Distinct().ToArray();
        var slopes = points.Zip(points.Skip(1), (left, right) => (right.Rank - left.Rank) * 1000 / (right.Time - left.Time)).ToArray();
        return slopes.Length > 0 ? Round(Percentile(slopes, 0.5)) : null;
    }

    private static QualiFinalCorrelationDto Correlation(JsonObject[] classified, IReadOnlyList<JsonObject> preFinal, string? label)
    {
        var previous = preFinal.Where(IsClassified).GroupBy(row => row.String("rider_id") ?? "")
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Int("position")).First());
        var points = classified.Where(row => previous.ContainsKey(row.String("rider_id") ?? ""))
            .Select(row => new CorrelationRiderDto(PublicDtoMapping.Long(row.String("rider_id")), Name(row),
                previous[row.String("rider_id")!].Int("position")!.Value, row.Int("position")!.Value)).ToArray();
        if (points.Length < 3) return new(label, null, points);
        var x = AverageRanks(points.Select(point => (double)point.PreFinalPosition).ToArray());
        var y = AverageRanks(points.Select(point => (double)point.FinalPosition).ToArray());
        var meanX = x.Average();
        var meanY = y.Average();
        var denominator = Math.Sqrt(x.Sum(value => Math.Pow(value - meanX, 2)) * y.Sum(value => Math.Pow(value - meanY, 2)));
        return new(label, denominator > 0 ? Round(Math.Clamp(x.Zip(y, (a, b) => (a - meanX) * (b - meanY)).Sum() / denominator, -1, 1)) : null, points);
    }

    private static WinningMarginDto? WinningMargin(JsonObject[] classified, SectorCalculation[] timings, int sectorCount)
    {
        var winner = classified.FirstOrDefault(row => row.Int("position") == 1);
        var runnerUp = classified.FirstOrDefault(row => row.Int("position") == 2);
        if (winner is null || runnerUp is null || sectorCount == 0) return null;
        var winnerSectors = timings.Where(sector => sector.ResultId == winner.String("id")).OrderBy(sector => sector.SplitIndex).ToArray();
        var secondSectors = timings.Where(sector => sector.ResultId == runnerUp.String("id")).ToDictionary(sector => sector.SplitIndex);
        if (winnerSectors.Length != sectorCount || secondSectors.Count != sectorCount) return null;
        var margin = (runnerUp.Int("race_time_ms")!.Value - winner.Int("race_time_ms")!.Value) / 1000m;
        if (margin < 0) return null;
        var sectors = winnerSectors.Select(sector =>
        {
            var gap = (secondSectors[sector.SplitIndex].SectorTimeMs!.Value - sector.SectorTimeMs!.Value) / 1000m;
            return new SectorContributionDto(sector.SplitIndex, gap, margin > 0 ? Math.Round(gap * 100 / margin, 2) : null);
        }).ToArray();
        // Do not present a partial timing decomposition as the whole winning margin.
        if (sectors.Sum(sector => sector.GapSeconds) != margin) return null;
        return new(PublicDtoMapping.Long(winner.String("rider_id")), Name(winner), PublicDtoMapping.Long(runnerUp.String("rider_id")), Name(runnerUp), margin, sectors);
    }

    private static int? SectorsForGain(WinningMarginDto? margin)
    {
        if (margin is null) return null;
        var gains = margin.Sectors.Select(sector => sector.GapSeconds).Where(gap => gap > 0).OrderDescending().ToArray();
        if (gains.Length == 0) return null;
        var target = gains.Sum() * 0.8m;
        decimal accumulated = 0;
        for (var index = 0; index < gains.Length; index++)
        {
            accumulated += gains[index];
            if (accumulated >= target) return index + 1;
        }
        return gains.Length;
    }

    private static ChaosIndexDto Chaos(JsonObject[] starters, Dictionary<string, IReadOnlyList<SectorCalculation>> projected,
        IReadOnlyList<RiderSessionPaceObservation> observations, QualiFinalCorrelationDto correlation)
    {
        var starterIds = starters.Select(row => row.String("rider_id")).ToHashSet();
        var measured = observations.Where(item => starterIds.Contains(item.RiderId)).GroupBy(item => item.RiderId).Select(group => group.First()).ToArray();
        var dnf = starters.Count(row => string.Equals(row.String("status")?.Trim(), "DNF", StringComparison.OrdinalIgnoreCase));
        var incidents = measured.Count(item => item.Result.IncidentSectorIndex.HasValue);
        var stages = projected.Values.SelectMany(sectors => sectors).Where(sector => sector.RaceTimeMs is > 0)
            .GroupBy(sector => sector.SplitIndex).OrderBy(group => group.Key)
            .Select(group => (Index: group.Key, Times: group.ToDictionary(sector => sector.ResultId, sector => (double)sector.RaceTimeMs!.Value))).ToArray();
        var changes = stages.Zip(stages.Skip(1)).Where(pair => pair.Second.Index == pair.First.Index + 1)
            .Select(pair =>
            {
                var common = pair.First.Times.Keys.Intersect(pair.Second.Times.Keys).ToArray();
                return common.Length < 3 ? (double?)null : Footrule(common.Select(id => pair.First.Times[id]).ToArray(), common.Select(id => pair.Second.Times[id]).ToArray());
            }).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var reshuffle = correlation.Points.Count >= 3 ? Footrule(correlation.Points.Select(point => (double)point.PreFinalPosition).ToArray(),
            correlation.Points.Select(point => (double)point.FinalPosition).ToArray()) : (double?)null;
        var components = new[]
        {
            new ChaosComponentDto("positionChanges", changes.Length > 0 ? Round(changes.Average() * 100) : null, 0.4m, changes.Length),
            new ChaosComponentDto("incidents", measured.Length > 0 ? Math.Round(100m * incidents / measured.Length, 3) : null, 0.2m, measured.Length),
            new ChaosComponentDto("dnf", starters.Length > 0 ? Math.Round(100m * dnf / starters.Length, 3) : null, 0.2m, starters.Length),
            new ChaosComponentDto("preFinalChanges", reshuffle.HasValue ? Round(reshuffle.Value * 100) : null, 0.2m, correlation.Points.Count)
        };
        var present = components.Where(component => component.Score.HasValue).ToArray();
        // Without timing movement or a pre-final comparison, a DNF rate alone is not a chaos score.
        var score = starters.Length >= 3 && (changes.Length > 0 || reshuffle.HasValue)
            ? CalibrateChaos(present.Sum(component => component.Score!.Value * component.Weight) / present.Sum(component => component.Weight))
            : (decimal?)null;
        return new(score, starters.Length, dnf, incidents, components);
    }

    // Fixed, monotonic calibration: halfway between the raw score and a stronger
    // contrast curve. Component scores remain raw evidence, not recalibrated rates.
    internal static decimal CalibrateChaos(decimal rawScore)
    {
        var raw = (double)Math.Clamp(rawScore, 0m, 100m);
        var contrast = 100 * (1 - Math.Exp(-Math.Pow(raw / 25, 2)));
        return Math.Round((decimal)((raw + contrast) / 2), 1);
    }

    private static double Footrule(double[] first, double[] second)
    {
        var a = AverageRanks(first);
        var b = AverageRanks(second);
        var maximum = Math.Floor((double)a.Length * a.Length / 2);
        return maximum > 0 ? Math.Clamp(a.Zip(b, (x, y) => Math.Abs(x - y)).Sum() / maximum, 0, 1) : 0;
    }

    private static double[] AverageRanks(double[] values)
    {
        var ranks = new double[values.Length];
        var sorted = values.Select((value, index) => (Value: value, Index: index)).OrderBy(item => item.Value).ToArray();
        for (var start = 0; start < sorted.Length;)
        {
            var end = start + 1;
            while (end < sorted.Length && sorted[end].Value == sorted[start].Value) end++;
            var rank = (start + 1 + end) / 2.0;
            for (var index = start; index < end; index++) ranks[sorted[index].Index] = rank;
            start = end;
        }
        return ranks;
    }

    private static PerformanceDistributionDto Distribution(JsonObject[] classified, IReadOnlyList<RiderSessionPaceObservation> observations)
    {
        var riders = classified.ToDictionary(row => row.String("rider_id")!);
        var scores = observations.Where(item => riders.ContainsKey(item.RiderId) && item.Result.Pace is >= 0 and <= 100)
            .GroupBy(item => item.RiderId).Select(group => group.First()).ToArray();
        var values = scores.Select(item => item.Result.Pace!.Value).ToArray();
        var q1 = Percentile(values, 0.25);
        var q3 = Percentile(values, 0.75);
        var iqr = q3 - q1;
        var bins = Enumerable.Range(0, 10).Select(index => new PerformanceBinDto(index * 10, (index + 1) * 10,
            values.Count(value => value >= index * 10 && (index == 9 ? value <= 100 : value < (index + 1) * 10)))).ToArray();
        var outliers = scores.Where(item => values.Length >= 4 && (item.Result.Pace < q1 - 1.5 * iqr || item.Result.Pace > q3 + 1.5 * iqr))
            .Select(item => new PerformanceOutlierDto(PublicDtoMapping.Long(item.RiderId), Name(riders[item.RiderId]), Round(item.Result.Pace!.Value))).ToArray();
        return new(values.Length, classified.Length, values.Length > 0 ? Round(Percentile(values, 0.5)) : null, bins, outliers);
    }

    private static double Percentile(double[] values, double fraction)
    {
        if (values.Length == 0) return 0;
        var sorted = values.Order().ToArray();
        var index = (sorted.Length - 1) * fraction;
        var lower = (int)index;
        return sorted[lower] + (sorted[(int)Math.Ceiling(index)] - sorted[lower]) * (index - lower);
    }
}
