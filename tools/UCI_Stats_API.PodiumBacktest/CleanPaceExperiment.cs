using UCI_Stats_API.Services;

namespace UCI_Stats_API.PodiumBacktest;

internal sealed record HistoricalRun(double? Pace, double Weight, bool DetectedIncident, bool Dnf, bool Finished, bool SameTrack);
internal sealed record CleanPaceDistribution(string RiderId, double Mean, double StandardDeviation, double IncidentProbability, double DnfProbability);

// Fixed experimental assumptions, not fitted on 2024 outcomes. Not the production model.
internal static class CleanPaceExperiment
{
    public static CleanPaceDistribution Fit(string riderId, IReadOnlyList<HistoricalRun> runs, double fallbackPace, bool risk, bool track)
    {
        var clean = runs.Where(run => run.Finished && !run.DetectedIncident && run.Pace.HasValue && run.Weight > 0).ToArray();
        var weight = clean.Sum(run => run.Weight);
        var mean = weight > 0 ? clean.Sum(run => run.Weight * run.Pace!.Value) / weight : fallbackPace;
        // Shrink small samples, without treating composite Consistency as clean-run variance.
        var variance = (clean.Sum(run => run.Weight * Math.Pow(run.Pace!.Value - mean, 2)) + 5 * 25) / (weight + 5);
        if (track)
        {
            var local = clean.Where(run => run.SameTrack).ToArray();
            var localWeight = local.Sum(run => run.Weight);
            if (localWeight > 0)
                mean += 0.5 * localWeight / (localWeight + 3) * (local.Sum(run => run.Weight * run.Pace!.Value) / localWeight - mean);
        }
        var exposure = runs.Sum(run => run.Weight);
        // Mutually exclusive outcomes: DNF is not counted again as a finishing incident.
        var dnf = (runs.Where(run => run.Dnf).Sum(run => run.Weight) + 8 * 0.03) / (exposure + 8);
        var incident = (runs.Where(run => !run.Dnf && run.DetectedIncident).Sum(run => run.Weight) + 8 * 0.08) / (exposure + 8);
        return new(riderId, mean, Math.Clamp(Math.Sqrt(variance), 1.25, 12), risk ? incident : 0, risk ? dnf : 0);
    }

    public static IReadOnlyList<MonteCarloPrediction> Simulate(IReadOnlyList<CleanPaceDistribution> riders, int iterations, int seed)
    {
        var random = new Random(seed);
        var wins = new int[riders.Count];
        var podiums = new int[riders.Count];
        var top5 = new int[riders.Count];
        var positions = new long[riders.Count];
        var sampled = new (int Index, double Performance, bool Dnf)[riders.Count];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var i = 0; i < riders.Count; i++)
            {
                var rider = riders[i];
                var outcome = random.NextDouble();
                var dnf = outcome < rider.DnfProbability;
                var incident = !dnf && outcome < rider.DnfProbability + rider.IncidentProbability;
                var performance = rider.Mean + rider.StandardDeviation * Normal(random);
                if (incident) performance -= 18 + 12 * Math.Abs(Normal(random));
                sampled[i] = (i, performance, dnf);
            }
            Array.Sort(sampled, (a, b) => a.Dnf != b.Dnf ? a.Dnf.CompareTo(b.Dnf) : b.Performance.CompareTo(a.Performance));
            for (var place = 0; place < sampled.Length; place++)
            {
                var (index, _, dnf) = sampled[place];
                positions[index] += place + 1;
                if (dnf) continue;
                if (place == 0) wins[index]++;
                if (place < 3) podiums[index]++;
                if (place < 5) top5[index]++;
            }
        }
        return Enumerable.Range(0, riders.Count).Select(i => new MonteCarloPrediction(
            wins[i] / (double)iterations, podiums[i] / (double)iterations,
            top5[i] / (double)iterations, positions[i] / (double)iterations)).ToArray();
    }

    private static double Normal(Random random) =>
        Math.Sqrt(-2 * Math.Log(Math.Max(double.Epsilon, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble());
}
