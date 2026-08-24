using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

internal sealed record RiderSectorObservation(
    string RiderId,
    string SessionId,
    string? CategoryCode,
    int SectorIndex,
    int SectorCount,
    double Score);

internal sealed record RiderSimilarityProfile(
    string ProfileId,
    string RiderId,
    string? CategoryCode,
    int SectorCount,
    int SessionCount,
    double Evidence,
    RiderSectorProfileDto Profile);

internal sealed record RiderSimilarityAnalysis(
    IReadOnlyList<RiderSimilarityPairDto> Pairs,
    IReadOnlyList<RiderSimilarityClusterDto> Clusters,
    IReadOnlyList<RiderSimilarityMergeDto> Merges,
    IReadOnlyDictionary<string, int> ClusterByProfileId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TagsByProfileId);

internal static class RiderProfileBuilder
{
    public static RiderSimilarityProfile? Build(
        string profileId,
        string riderId,
        string? categoryCode,
        IReadOnlyList<RiderSectorObservation> observations,
        int minimumSectors,
        int minimumSessions)
    {
        var valid = observations
            .Where(item => double.IsFinite(item.Score) && item.Score is >= 0 and <= 100)
            .ToArray();
        var sessionCount = valid
            .Select(item => item.SessionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (valid.Length < minimumSectors || sessionCount < minimumSessions)
        {
            return null;
        }

        var start = valid.Where(item => RelativePosition(item) < 1.0 / 3.0).ToArray();
        var middle = valid.Where(item => RelativePosition(item) is >= (1.0 / 3.0) and < (2.0 / 3.0)).ToArray();
        var finish = valid.Where(item => RelativePosition(item) >= 2.0 / 3.0).ToArray();
        var scores = valid.Select(item => item.Score).OrderBy(value => value).ToArray();
        var tailCount = Math.Max(1, (int)Math.Ceiling(scores.Length * 0.25));
        var mean = scores.Average();
        var standardDeviation = Math.Sqrt(scores.Average(value => Math.Pow(value - mean, 2)));
        var evidence = Math.Sqrt(
            Math.Min(1.0, valid.Length / (minimumSectors * 3.0)) *
            Math.Min(1.0, sessionCount / (minimumSessions * 3.0)));

        return new RiderSimilarityProfile(
            profileId,
            riderId,
            categoryCode,
            valid.Length,
            sessionCount,
            Round(evidence),
            new RiderSectorProfileDto(
                Mean01(start),
                Mean01(middle),
                Mean01(finish),
                Technical: null,
                Fast: null,
                Consistency: Round(1.0 / (1.0 + Math.Pow(standardDeviation / 15.0, 2))),
                Peak: Round(scores.TakeLast(tailCount).Average() / 100.0),
                Weak: Round(scores.Take(tailCount).Average() / 100.0),
                Variance: Round(standardDeviation / 100.0)));
    }

    private static double RelativePosition(RiderSectorObservation item) =>
        item.SectorCount <= 0
            ? 0.5
            : (item.SectorIndex + 0.5) / item.SectorCount;

    private static double? Mean01(IReadOnlyCollection<RiderSectorObservation> values) =>
        values.Count == 0 ? null : Round(values.Average(item => item.Score) / 100.0);

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

internal static class RiderSimilarityCalculator
{
    internal static readonly string[] FeatureNames =
        ["start", "middle", "finish", "consistency", "peak", "weak", "variance"];

    public static RiderSimilarityAnalysis Analyze(
        IReadOnlyList<RiderSimilarityProfile> profiles,
        int minimumSharedFeatures,
        int? requestedClusterCount,
        bool allowCrossCategory)
    {
        var groups = allowCrossCategory
            ? new[] { profiles }
            : profiles
                .GroupBy(profile => profile.CategoryCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => (IReadOnlyList<RiderSimilarityProfile>)group.ToArray())
                .ToArray();
        var pairs = new List<RiderSimilarityPairDto>();
        var clusters = new List<RiderSimilarityClusterDto>();
        var merges = new List<RiderSimilarityMergeDto>();
        var clusterByProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagsByProfile = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var nextClusterId = 1;
        var nextNodeId = 1;

        foreach (var group in groups.Where(group => group.Count > 0))
        {
            var normalized = Normalize(group);
            var groupPairs = BuildPairs(group, normalized, minimumSharedFeatures);
            pairs.AddRange(groupPairs);
            var hierarchy = BuildHierarchy(group, groupPairs, ref nextNodeId);
            merges.AddRange(hierarchy.Merges);
            var targetCount = requestedClusterCount.HasValue
                ? Math.Clamp(requestedClusterCount.Value, 1, group.Count)
                : NaturalClusterCount(hierarchy.MergeDistances, group.Count);
            var flatClusters = hierarchy.StatesByCount.GetValueOrDefault(targetCount) ??
                hierarchy.StatesByCount.Values.OrderBy(state => Math.Abs(state.Count - targetCount)).First();

            foreach (var members in flatClusters.OrderBy(cluster => cluster.Min(StringComparer.OrdinalIgnoreCase)))
            {
                var memberProfiles = members.Select(id => group.First(profile => profile.ProfileId == id)).ToArray();
                var centroid = Centroid(memberProfiles);
                var tags = AnalyzeTags(centroid, Centroid(group));
                var id = nextClusterId++;
                clusters.Add(new RiderSimilarityClusterDto(id, members, members.Count, centroid, tags));
                foreach (var profileId in members)
                {
                    clusterByProfile[profileId] = id;
                    tagsByProfile[profileId] = tags;
                }
            }
        }

        if (!allowCrossCategory)
        {
            for (var left = 0; left < profiles.Count; left++)
            {
                for (var right = left + 1; right < profiles.Count; right++)
                {
                    if (!string.Equals(profiles[left].CategoryCode, profiles[right].CategoryCode, StringComparison.OrdinalIgnoreCase))
                    {
                        pairs.Add(new RiderSimilarityPairDto(
                            profiles[left].ProfileId,
                            profiles[right].ProfileId,
                            null,
                            0));
                    }
                }
            }
        }

        return new RiderSimilarityAnalysis(pairs, clusters, merges, clusterByProfile, tagsByProfile);
    }

    private static IReadOnlyDictionary<string, double?[]> Normalize(IReadOnlyList<RiderSimilarityProfile> profiles)
    {
        var raw = profiles.ToDictionary(
            profile => profile.ProfileId,
            profile => Features(profile.Profile),
            StringComparer.OrdinalIgnoreCase);
        var output = profiles.ToDictionary(
            profile => profile.ProfileId,
            _ => new double?[FeatureNames.Length],
            StringComparer.OrdinalIgnoreCase);
        for (var feature = 0; feature < FeatureNames.Length; feature++)
        {
            var values = raw.Values.Select(row => row[feature]).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            var mean = values.Average();
            var deviation = Math.Sqrt(values.Average(value => Math.Pow(value - mean, 2)));
            foreach (var profile in profiles)
            {
                if (raw[profile.ProfileId][feature] is { } value)
                {
                    output[profile.ProfileId][feature] = deviation <= 0.0000001 ? 0 : (value - mean) / deviation;
                }
            }
        }

        return output;
    }

    private static IReadOnlyList<RiderSimilarityPairDto> BuildPairs(
        IReadOnlyList<RiderSimilarityProfile> profiles,
        IReadOnlyDictionary<string, double?[]> normalized,
        int minimumSharedFeatures)
    {
        var pairs = new List<RiderSimilarityPairDto>();
        for (var left = 0; left < profiles.Count; left++)
        {
            for (var right = left + 1; right < profiles.Count; right++)
            {
                var a = normalized[profiles[left].ProfileId];
                var b = normalized[profiles[right].ProfileId];
                var shared = Enumerable.Range(0, a.Length)
                    .Where(index => a[index].HasValue && b[index].HasValue)
                    .ToArray();
                double? similarity = null;
                if (shared.Length >= minimumSharedFeatures)
                {
                    var dot = shared.Sum(index => a[index]!.Value * b[index]!.Value);
                    var normA = Math.Sqrt(shared.Sum(index => a[index]!.Value * a[index]!.Value));
                    var normB = Math.Sqrt(shared.Sum(index => b[index]!.Value * b[index]!.Value));
                    var cosine = normA <= 0.0000001 && normB <= 0.0000001
                        ? 1.0
                        : normA <= 0.0000001 || normB <= 0.0000001
                            ? 0.0
                            : Math.Clamp(dot / (normA * normB), -1, 1);
                    similarity = Round((cosine + 1.0) / 2.0);
                }

                pairs.Add(new RiderSimilarityPairDto(
                    profiles[left].ProfileId,
                    profiles[right].ProfileId,
                    similarity,
                    shared.Length));
            }
        }

        return pairs;
    }

    private static HierarchyResult BuildHierarchy(
        IReadOnlyList<RiderSimilarityProfile> profiles,
        IReadOnlyList<RiderSimilarityPairDto> pairs,
        ref int nextNodeId)
    {
        var distances = pairs.ToDictionary(
            pair => PairKey(pair.RiderAId, pair.RiderBId),
            pair => pair.Similarity.HasValue ? 1.0 - pair.Similarity.Value : 1.0,
            StringComparer.OrdinalIgnoreCase);
        var nodes = profiles.Select(profile => new ClusterNode(profile.ProfileId, [profile.ProfileId])).ToList();
        var merges = new List<RiderSimilarityMergeDto>();
        var mergeDistances = new List<double>();
        var states = new Dictionary<int, IReadOnlyList<IReadOnlyList<string>>>
        {
            [nodes.Count] = Snapshot(nodes)
        };

        while (nodes.Count > 1)
        {
            var bestLeft = 0;
            var bestRight = 1;
            var bestDistance = double.MaxValue;
            for (var left = 0; left < nodes.Count; left++)
            {
                for (var right = left + 1; right < nodes.Count; right++)
                {
                    var distance = AverageDistance(nodes[left].Members, nodes[right].Members, distances);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestLeft = left;
                        bestRight = right;
                    }
                }
            }

            var leftNode = nodes[bestLeft];
            var rightNode = nodes[bestRight];
            var nodeId = $"cluster-node-{nextNodeId++}";
            var members = leftNode.Members.Concat(rightNode.Members).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            merges.Add(new RiderSimilarityMergeDto(nodeId, leftNode.Id, rightNode.Id, Round(bestDistance), members.Length));
            mergeDistances.Add(bestDistance);
            nodes.RemoveAt(bestRight);
            nodes.RemoveAt(bestLeft);
            nodes.Add(new ClusterNode(nodeId, members));
            states[nodes.Count] = Snapshot(nodes);
        }

        return new HierarchyResult(merges, mergeDistances, states);
    }

    private static int NaturalClusterCount(IReadOnlyList<double> distances, int profileCount)
    {
        if (profileCount <= 2)
        {
            return 1;
        }

        var bestMergeIndex = 0;
        var bestJump = double.MinValue;
        for (var index = 0; index < distances.Count - 1; index++)
        {
            var jump = distances[index + 1] - distances[index];
            if (jump > bestJump)
            {
                bestJump = jump;
                bestMergeIndex = index;
            }
        }

        return Math.Clamp(profileCount - (bestMergeIndex + 1), 2, Math.Min(8, profileCount));
    }

    private static double AverageDistance(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        IReadOnlyDictionary<string, double> distances) =>
        left.SelectMany(_ => right, (a, b) => distances.GetValueOrDefault(PairKey(a, b), 1.0)).Average();

    private static string PairKey(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{left}\u001f{right}"
            : $"{right}\u001f{left}";

    private static RiderSectorProfileDto Centroid(IReadOnlyList<RiderSimilarityProfile> profiles)
    {
        var features = profiles.Select(profile => Features(profile.Profile)).ToArray();
        double? Mean(int index)
        {
            var values = features.Select(row => row[index]).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return values.Length == 0 ? null : Round(values.Average());
        }

        return new RiderSectorProfileDto(Mean(0), Mean(1), Mean(2), null, null, Mean(3), Mean(4), Mean(5), Mean(6));
    }

    private static IReadOnlyList<string> AnalyzeTags(RiderSectorProfileDto centroid, RiderSectorProfileDto population)
    {
        var tags = new List<string>();
        if (centroid.Start.HasValue && centroid.Finish.HasValue && centroid.Start > centroid.Finish + 0.06)
            tags.Add("Strong Starter");
        if (centroid.Start.HasValue && centroid.Finish.HasValue && centroid.Finish > centroid.Start + 0.06)
            tags.Add("Strong Finisher");
        if (centroid.Consistency > population.Consistency + 0.05)
            tags.Add("High Consistency");
        if (centroid.Variance > population.Variance + 0.03)
            tags.Add("High Variance");
        if (tags.Count == 0)
            tags.Add("Balanced");
        return tags;
    }

    private static double?[] Features(RiderSectorProfileDto profile) =>
        [profile.Start, profile.Middle, profile.Finish, profile.Consistency, profile.Peak, profile.Weak, profile.Variance];

    private static IReadOnlyList<IReadOnlyList<string>> Snapshot(IEnumerable<ClusterNode> nodes) =>
        nodes.Select(node => (IReadOnlyList<string>)node.Members.ToArray()).ToArray();

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record ClusterNode(string Id, IReadOnlyList<string> Members);

    private sealed record HierarchyResult(
        IReadOnlyList<RiderSimilarityMergeDto> Merges,
        IReadOnlyList<double> MergeDistances,
        IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<string>>> StatesByCount);
}
