using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Services;

internal enum SectorAnomalyKind { Normal, PossibleIncident, LikelyIncident, InvalidTiming }

internal sealed record SectorAnomaly(
    SectorAnomalyKind Kind, double? ExpectedSeconds, double? LossSeconds,
    double? RobustDeviation, string Reason, bool AllowsFinishCorrection = false);

/// <summary>
/// Sector durations, not cumulative splits. Field summaries must belong to the
/// same session/category. Diagnoses anomalies, not the physical cause of a crash.
/// </summary>
internal static class SectorIncidentDetector
{
    internal static IReadOnlyList<SectorAnomaly> Analyze(
        IReadOnlyList<PacePrecomputedSector> sectors, PaceOptions options,
        IncidentDetectionOptions? thresholds = null)
    {
        using var measurement = RaceTrace.Api.Infrastructure.Telemetry.ApiTelemetry.Measure("incident_detection");
        thresholds ??= AlgorithmConfiguration.Incidents;
        var result = sectors.Select(s => new SectorAnomaly(
            SectorAnomalyKind.Normal, null, null, null,
            s.TimeSeconds is null ? "Missing timing" : "Insufficient evidence")).ToArray();
        var invalid = new bool[sectors.Count];
        for (var i = 0; i < sectors.Count; i++)
        {
            var s = sectors[i];
            invalid[i] = s.TimeSeconds.HasValue &&
                (!double.IsFinite(s.TimeSeconds.Value) || s.TimeSeconds <= 0 ||
                 (ValidField(s) && s.TimeSeconds < s.Statistics.Median * thresholds.InvalidShortRatio));
            if (invalid[i]) result[i] = Invalid("Non-positive, non-finite or implausibly short sector");
        }

        // A displaced checkpoint produces a long sector followed by a short one
        // (or vice versa), while their combined duration remains ordinary.
        for (var i = 0; i + 1 < sectors.Count; i++)
        {
            var a = sectors[i];
            var b = sectors[i + 1];
            if (!Usable(a) || !Usable(b)) continue;
            var reference = RatiosExcept(i, i + 1);
            if (reference.Length < 2) continue;
            var factor = Math.Exp(Median(reference));
            var expectedA = a.Statistics.Median * factor;
            var expectedB = b.Statistics.Median * factor;
            var da = a.TimeSeconds!.Value - expectedA;
            var db = b.TimeSeconds!.Value - expectedB;
            if (((da > options.MinimumIncidentLossSeconds && b.TimeSeconds < Math.Min(expectedB, b.Statistics.Median) * thresholds.CheckpointShortRatio) ||
                 (db > options.MinimumIncidentLossSeconds && a.TimeSeconds < Math.Min(expectedA, a.Statistics.Median) * thresholds.CheckpointShortRatio)) &&
                da * db < 0 && Math.Abs(da + db) <= Math.Max(thresholds.CheckpointToleranceSeconds, thresholds.CheckpointToleranceRatio * (expectedA + expectedB)))
            {
                invalid[i] = invalid[i + 1] = true;
                result[i] = Invalid("Compensating long/short checkpoint pair");
                result[i + 1] = result[i];
            }
        }

        for (var i = 0; i < sectors.Count; i++)
        {
            if (invalid[i] || !Usable(sectors[i])) continue;
            var others = Enumerable.Range(0, sectors.Count)
                .Where(j => j != i && !invalid[j] && Usable(sectors[j])).ToArray();
            var references = others.OrderBy(j => Math.Abs(j - i)).ThenBy(j => j)
                .Take(thresholds.ReferenceSectors).Select(LogRatio).ToArray();
            if (references.Length == 0) continue;
            var (baseline, expected, loss, scale, deviation) = Profile(sectors[i], references, thresholds);
            var clean = others.Where(j => Math.Abs(LogRatio(j) - baseline) <= thresholds.CleanDeviation * scale).ToHashSet();
            var later = others.Where(j => j > i).ToArray();
            var recovers = later.Any(clean.Contains);
            var ranked = others.Where(j => sectors[j].Rank is > 0).ToArray();
            var rankDrop = sectors[i].Rank is > 0 && ranked.Length > 0
                ? RelativeRank(i) - Median(ranked.Select(RelativeRank)) : 0;
            var largeLoss = loss >= thresholds.LargeLossSeconds && deviation >= thresholds.LargeLossDeviation &&
                deviation + Math.Max(0, rankDrop) >= thresholds.CombinedEvidence && clean.Count >= thresholds.LargeLossCleanSectors;

            // Smaller losses need top-field pace on BOTH adjacent sectors,
            // recovery, and a meaningful rank collapse; never a terminal exception.
            var topReferences = ranked.Where(j => sectors[j].Statistics.Count >= thresholds.TopFieldMinimumSize).ToArray();
            var isolatedTopPace = loss >= thresholds.IsolatedLossSeconds && deviation >= thresholds.IsolatedDeviation && clean.Count >= thresholds.IsolatedCleanSectors &&
                rankDrop >= thresholds.IsolatedRankDrop && sectors[i].Statistics.Count >= thresholds.TopFieldMinimumSize && recovers &&
                others.Contains(i - 1) && others.Contains(i + 1) &&
                clean.Contains(i - 1) && clean.Contains(i + 1) &&
                topReferences.Length >= thresholds.TopReferenceCount && Median(topReferences.Select(RelativeRank)) <= thresholds.TopMedianRank &&
                new[] { i - 1, i + 1 }.All(j => sectors[j].Rank is > 0 &&
                    sectors[j].Statistics.Count >= thresholds.TopFieldMinimumSize && RelativeRank(j) <= thresholds.TopAdjacentRank);
            var detected = largeLoss || isolatedTopPace;
            result[i] = new(detected ? SectorAnomalyKind.LikelyIncident : SectorAnomalyKind.Normal,
                expected, loss, deviation,
                isolatedTopPace ? "Isolated top-pace loss corroborated by rank collapse and recovery"
                    : largeLoss ? "Large local time anomaly" : "Within rider sector profile",
                detected && (recovers || later.Length == 0));
        }
        AddExtremeTimeEvidence(sectors, result, invalid, options, thresholds);

        // A damaged majority can hide its own anomaly in a leave-one-out median.
        // Detect a persistent change from a stable prefix, but never "clean" it.
        for (var start = 2; start + 1 < sectors.Count; start++)
        {
            var prefix = Enumerable.Range(0, start)
                .Where(j => !invalid[j] && Usable(sectors[j]) && result[j].Kind == SectorAnomalyKind.Normal)
                .Select(j => Math.Log(sectors[j].TimeSeconds!.Value / sectors[j].Statistics.Median)).ToArray();
            if (prefix.Length < 2) continue;
            var suffix = Enumerable.Range(start, sectors.Count - start)
                .Where(j => !invalid[j] && Usable(sectors[j])).ToArray();
            if (suffix.Length < 2 || suffix.Any(j => result[j].Kind == SectorAnomalyKind.LikelyIncident)) continue;
            var baseline = Median(prefix);
            var spread = 1.4826 * Median(prefix.Select(v => Math.Abs(v - baseline)));
            var candidates = suffix.Select(j =>
            {
                var expected = sectors[j].Statistics.Median * Math.Exp(baseline);
                var loss = sectors[j].TimeSeconds!.Value - expected;
                var scale = Math.Max(thresholds.ScaleFloor, Math.Max(spread, sectors[j].Statistics.LogMadScale));
                return (Index: j, Expected: expected, Loss: loss,
                    Deviation: Math.Log(sectors[j].TimeSeconds!.Value / expected) / scale);
            }).ToArray();
            if (!candidates.All(c => c.Loss >= options.MinimumIncidentLossSeconds &&
                    c.Loss / c.Expected >= options.IncidentRelativeLossThreshold && c.Deviation >= options.IncidentPerformanceDropThreshold)) continue;
            foreach (var c in candidates)
                result[c.Index] = new(SectorAnomalyKind.PossibleIncident, c.Expected, c.Loss, c.Deviation,
                    "Sustained slowdown; cause uncertain");
            break;
        }
        return result;

        double[] RatiosExcept(int excluded, int second = -1) => sectors
            .Where((s, j) => j != excluded && j != second && !invalid[j] && Usable(s))
            .Select(s => Math.Log(s.TimeSeconds!.Value / s.Statistics.Median)).ToArray();
        bool Usable(PacePrecomputedSector s) => s.TimeSeconds is > 0 &&
            double.IsFinite(s.TimeSeconds.Value) && ValidField(s) && s.Statistics.Count >= options.MinimumFieldSize;
        double LogRatio(int j) => Math.Log(sectors[j].TimeSeconds!.Value / sectors[j].Statistics.Median);
        double RelativeRank(int j) => (sectors[j].Rank!.Value - 1.0) / Math.Max(1, sectors[j].Statistics.Count - 1);
    }

    internal static SectorAnomaly? DescribeCachedNormal(IReadOnlyList<PacePrecomputedSector> sectors,
        int index, IReadOnlyList<SectorAnomaly> annotations, PaceOptions options)
    {
        var thresholds = AlgorithmConfiguration.Incidents;
        bool Usable(int j) => annotations[j].Kind != SectorAnomalyKind.InvalidTiming &&
            sectors[j].TimeSeconds is > 0 && double.IsFinite(sectors[j].TimeSeconds!.Value) &&
            ValidField(sectors[j]) && sectors[j].Statistics.Count >= options.MinimumFieldSize;
        if (!Usable(index)) return null;
        var references = Enumerable.Range(0, sectors.Count).Where(j => j != index && Usable(j))
            .OrderBy(j => Math.Abs(j - index)).ThenBy(j => j).Take(thresholds.ReferenceSectors)
            .Select(j => LogRatio(sectors[j])).ToArray();
        if (references.Length == 0) return null;
        var (_, expected, loss, _, deviation) = Profile(sectors[index], references, thresholds);
        return new(SectorAnomalyKind.Normal, expected, loss, deviation, "Within rider sector profile");
    }

    private static (double Baseline, double Expected, double Loss, double Scale, double Deviation) Profile(
        PacePrecomputedSector sector, double[] references, IncidentDetectionOptions thresholds)
    {
        var baseline = Median(references);
        var expected = sector.Statistics.Median * Math.Exp(baseline);
        var loss = sector.TimeSeconds!.Value - expected;
        var scale = Math.Max(thresholds.ScaleFloor, Math.Max(sector.Statistics.LogMadScale,
            1.4826 * Median(references.Select(v => Math.Abs(v - baseline)))));
        return (baseline, expected, loss, scale, Math.Log(sector.TimeSeconds.Value / expected) / scale);
    }

    private static void AddExtremeTimeEvidence(IReadOnlyList<PacePrecomputedSector> sectors,
        SectorAnomaly[] result, bool[] invalid, PaceOptions options, IncidentDetectionOptions thresholds)
    {
        var usable = Enumerable.Range(0, sectors.Count).Where(i => !invalid[i] &&
            sectors[i].TimeSeconds is > 0 && double.IsFinite(sectors[i].TimeSeconds!.Value) &&
            ValidField(sectors[i]) && sectors[i].Statistics.Count >= options.MinimumFieldSize).ToArray();
        // The ordinary path above uses local robust deviation. This fallback is
        // only for extreme losses hidden by a wide field or incomplete timing.
        var references = usable.Where(i => result[i].Kind != SectorAnomalyKind.LikelyIncident).ToArray();
        foreach (var i in usable)
        {
            if (result[i].Kind == SectorAnomalyKind.LikelyIncident) continue;
            var sector = sectors[i];
            var others = references.Where(j => j != i).ToArray();
            var ratios = others.Select(j => LogRatio(sectors[j])).ToArray();
            var baseline = ratios.Length == 0 ? 0 : Median(ratios);
            var expected = sector.Statistics.Median * Math.Exp(baseline);
            var spread = ratios.Length == 0 ? 0 : 1.4826 * Median(ratios.Select(r => Math.Abs(r - baseline)));
            var scale = Math.Max(thresholds.ScaleFloor, Math.Max(spread, sector.Statistics.LogMadScale));
            var lossSeconds = sector.TimeSeconds!.Value - expected;
            var deviation = Math.Log(sector.TimeSeconds.Value / expected) / scale;
            var fieldOnly = others.Length == 0;
            var sparse = others.Length < 2;
            if (lossSeconds < (sparse ? thresholds.FieldOnlyLossSeconds : thresholds.ExtremeLossSeconds) ||
                lossSeconds / expected < (sparse ? thresholds.FieldOnlyLossRatio : thresholds.ExtremeLossRatio)) continue;

            // A personal baseline needs a majority of consistent references; a
            // single unusually quick sector cannot condemn the rest of the run.
            var clean = others.Where(j => Math.Abs(LogRatio(sectors[j]) - baseline) <=
                thresholds.RecoveryLogTolerance).ToArray();
            if (!fieldOnly && (2 * clean.Length < others.Length ||
                clean.Length == 0 || deviation < thresholds.TimeEvidenceDeviation)) continue;
            var recovers = clean.Any(j => j > i);
            var terminal = !usable.Any(j => j > i);
            // Detection is possible with zero/one other measurement. Reconstruction
            // still needs independent pace evidence; it never repairs a DNF finish.
            var correctable = clean.Length >= 2 && 2 * clean.Length >= others.Length && (recovers || terminal);
            result[i] = new(SectorAnomalyKind.LikelyIncident, expected, lossSeconds, deviation,
                fieldOnly ? "Extreme field-relative time loss; personal baseline unavailable" :
                    "Extreme time loss relative to median rider profile", correctable);
        }
    }

    private static double LogRatio(PacePrecomputedSector sector) => Math.Log(sector.TimeSeconds!.Value / sector.Statistics.Median);

    private static bool ValidField(PacePrecomputedSector s) =>
        double.IsFinite(s.Statistics.Median) && s.Statistics.Median > 0;
    private static SectorAnomaly Invalid(string reason) => new(SectorAnomalyKind.InvalidTiming, null, null, null, reason);
    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length % 2 == 1 ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }
}
