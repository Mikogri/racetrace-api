namespace RaceTrace.Api.Services;

public sealed record ChampionshipRule(int From, int? To, int Points);
public sealed record ChampionshipStage(string SessionType, IReadOnlyList<ChampionshipRule> Rules)
{
    public int PointsAt(int position) => Rules.FirstOrDefault(rule => position >= rule.From && (rule.To is null || position <= rule.To))?.Points ?? 0;
}
public sealed record ChampionshipRider(string? RiderId, string UciId, string Name, int Points);
public sealed record ChampionshipRun(string? UciId, string Name, int? Position,
    [property: Infrastructure.ApiContractValues("finished", "pending", "dnf", "dns", "dsq")] string State);
public sealed record ChampionshipSession(string SessionType, bool Complete, IReadOnlyList<ChampionshipRun> Riders);
public sealed record ChampionshipProjection(IReadOnlyList<ChampionshipRider> Items, int? Lead, bool? CanChange,
    [property: Infrastructure.ApiContractValues("unavailable", "incomplete", "tied", "open", "secured", "decided")] string Status);

public static class LiveChampionshipCalculator
{
    public static ChampionshipProjection Calculate(IReadOnlyList<ChampionshipRider> baseline,
        IReadOnlyList<ChampionshipStage> stages, IReadOnlyList<ChampionshipSession> sessions)
    {
        var stageMap = stages.ToDictionary(stage => Key(stage.SessionType));
        var sessionGroups = sessions.GroupBy(session => Key(session.SessionType)).ToArray();
        if (!stageMap.ContainsKey("final") || sessionGroups.Any(group => group.Count() != 1))
            return new([], null, null, "unavailable");
        var sessionMap = sessionGroups.ToDictionary(group => group.Key, group => group.Single());
        if (stageMap.Keys.Any(key => !sessionMap.ContainsKey(key)) ||
            sessionMap.Where(pair => stageMap.ContainsKey(pair.Key) && pair.Key != "final").Any(pair => !pair.Value.Complete))
            return new([], null, null, "unavailable");

        var final = sessionMap["final"];
        if (final.Riders.Count == 0) return new([], null, null, "unavailable");
        var totals = baseline.ToDictionary(rider => rider.UciId);
        var identityMissing = baseline.Any(rider => rider.UciId.StartsWith("unmapped:", StringComparison.Ordinal));
        foreach (var (type, stage) in stageMap)
        {
            var session = sessionMap[type];
            identityMissing |= session.Riders.Any(run => string.IsNullOrWhiteSpace(run.UciId));
            foreach (var group in session.Riders.Where(run => !string.IsNullOrWhiteSpace(run.UciId)).GroupBy(run => run.UciId!))
            {
                identityMissing |= group.Count() > 1;
                var run = group.First();
                var rider = totals.GetValueOrDefault(group.Key) ?? new ChampionshipRider(null, group.Key, run.Name, 0);
                var points = run.State == "finished" && run.Position is > 0 ? stage.PointsAt(run.Position.Value) : 0;
                totals[group.Key] = rider with { Points = rider.Points + points };
            }
        }

        var ordered = totals.Values.OrderByDescending(rider => rider.Points).ThenBy(rider => rider.Name, StringComparer.Ordinal).ToArray();
        var top = ordered.Take(2).ToArray();
        int? lead = top.Length > 1 ? top[0].Points - top[1].Points : null;
        if (top.Length < 2 || identityMissing) return new(top, lead, null, "incomplete");

        var pending = final.Riders.Count(run => run.State == "pending");
        var finalRules = stageMap["final"];
        // No official tie-break is guessed: a possible tie keeps the title open.
        if (pending == 0 && !final.Complete) return new(top, lead, null, "incomplete");
        if (lead == 0 && pending == 0) return new(top, lead, null, "tied");
        var leader = top[0];
        var leaderRun = final.Riders.FirstOrDefault(run => run.UciId == leader.UciId);
        var leaderMinimum = leader.Points;
        if (leaderRun is { State: "finished", Position: > 0 })
            leaderMinimum += finalRules.PointsAt(leaderRun.Position.Value + pending) - finalRules.PointsAt(leaderRun.Position.Value);
        var winnerPoints = finalRules.PointsAt(1);
        var canChange = ordered.Skip(1).Any(rider =>
        {
            var run = final.Riders.FirstOrDefault(item => item.UciId == rider.UciId);
            if (leaderRun is { State: "finished", Position: > 0 } && run is { State: "finished", Position: > 0 } && run.Position >= leaderRun.Position)
            {
                // Any new finisher ahead of the leader also overtakes a challenger behind them.
                // Placing riders between them only hurts that challenger, so test shared demotions.
                for (var overtakes = 0; overtakes <= pending; overtakes++)
                {
                    var leaderPoints = leader.Points - finalRules.PointsAt(leaderRun.Position.Value) + finalRules.PointsAt(leaderRun.Position.Value + overtakes);
                    var challengerPoints = rider.Points - finalRules.PointsAt(run.Position.Value) + finalRules.PointsAt(run.Position.Value + overtakes);
                    if (challengerPoints >= leaderPoints) return true;
                }
                return false;
            }
            return rider.Points + (run?.State == "pending" ? winnerPoints : 0) >= leaderMinimum;
        });
        return new(top, lead, canChange, canChange ? "open" : final.Complete && pending == 0 ? "decided" : "secured");
    }

    internal static string Key(string type) => PublicApiCodes.ToPublicSessionType(type) ?? type;
}
