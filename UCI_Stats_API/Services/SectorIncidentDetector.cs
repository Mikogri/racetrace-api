namespace UCI_Stats_API.Services;

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
        IReadOnlyList<PacePrecomputedSector> sectors, PaceOptions options)
    {
        var result = sectors.Select(s => new SectorAnomaly(
            SectorAnomalyKind.Normal, null, null, null,
            s.TimeSeconds is null ? "Missing timing" : "Insufficient evidence")).ToArray();
        var invalid = new bool[sectors.Count];
        for (var i = 0; i < sectors.Count; i++)
        {
            var s = sectors[i];
            invalid[i] = s.TimeSeconds.HasValue &&
                (!double.IsFinite(s.TimeSeconds.Value) || s.TimeSeconds <= 0 ||
                 (ValidField(s) && s.TimeSeconds < s.Statistics.Median * .25));
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
            if (((da > options.MinimumIncidentLossSeconds && b.TimeSeconds < Math.Min(expectedB, b.Statistics.Median) * .65) ||
                 (db > options.MinimumIncidentLossSeconds && a.TimeSeconds < Math.Min(expectedA, a.Statistics.Median) * .65)) &&
                da * db < 0 && Math.Abs(da + db) <= Math.Max(1, .05 * (expectedA + expectedB)))
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
                .Take(3).Select(LogRatio).ToArray();
            if (references.Length < 2) continue;
            var baseline = Median(references);
            var expected = sectors[i].Statistics.Median * Math.Exp(baseline);
            var loss = sectors[i].TimeSeconds!.Value - expected;
            // Floors protect near-identical fields. Cross-sector MAD allows normal
            // strengths/weaknesses instead of assuming a rider has identical ranks.
            var scale = Math.Max(.02, Math.Max(sectors[i].Statistics.LogMadScale,
                1.4826 * Median(references.Select(v => Math.Abs(v - baseline)))));
            var deviation = Math.Log(sectors[i].TimeSeconds!.Value / expected) / scale;
            var clean = others.Where(j => Math.Abs(LogRatio(j) - baseline) <= 1.5 * scale).ToHashSet();
            var later = others.Where(j => j > i).ToArray();
            var recovers = later.Any(clean.Contains);
            var ranked = others.Where(j => sectors[j].Rank is > 0).ToArray();
            var rankDrop = sectors[i].Rank is > 0 && ranked.Length > 0
                ? RelativeRank(i) - Median(ranked.Select(RelativeRank)) : 0;
            var largeLoss = loss >= 4 && deviation >= 1.5 &&
                deviation + Math.Max(0, rankDrop) >= 4 && clean.Count >= 2;

            // Smaller losses need top-field pace on BOTH adjacent sectors,
            // recovery, and a meaningful rank collapse; never a terminal exception.
            var topReferences = ranked.Where(j => sectors[j].Statistics.Count >= 6).ToArray();
            var isolatedTopPace = loss >= 2 && deviation >= 2 && clean.Count >= 3 &&
                rankDrop >= .5 && sectors[i].Statistics.Count >= 6 && recovers &&
                others.Contains(i - 1) && others.Contains(i + 1) &&
                clean.Contains(i - 1) && clean.Contains(i + 1) &&
                topReferences.Length >= 3 && Median(topReferences.Select(RelativeRank)) <= .15 &&
                new[] { i - 1, i + 1 }.All(j => sectors[j].Rank is > 0 &&
                    sectors[j].Statistics.Count >= 6 && RelativeRank(j) <= .2);
            var detected = largeLoss || isolatedTopPace;
            result[i] = new(detected ? SectorAnomalyKind.LikelyIncident : SectorAnomalyKind.Normal,
                expected, loss, deviation,
                isolatedTopPace ? "Isolated top-pace loss corroborated by rank collapse and recovery"
                    : largeLoss ? "Large local time anomaly" : "Within rider sector profile",
                detected && (recovers || later.Length == 0));
        }
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
                var scale = Math.Max(.02, Math.Max(spread, sectors[j].Statistics.LogMadScale));
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
