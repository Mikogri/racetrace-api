using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Infrastructure.Telemetry;
using RaceTrace.Api.Models;
using static RaceTrace.Api.Services.DomainQueryService;

namespace RaceTrace.Api.Services;

internal sealed class RankingInputPackageReader(
    ISupabaseRestClient supabase,
    IMemoryCache? packageCache,
    ReadOptimizationOptions readOptions,
    DataRevisionService? revisions)
{
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly IMemoryCache? _packageCache = packageCache;
    private readonly ReadOptimizationOptions _readOptions = readOptions;
    private readonly DataRevisionService? _revisions = revisions;
    private readonly Dictionary<JsonObject, IReadOnlyList<PacePrecomputedSessionData>> _preparedPackages = new(ReferenceEqualityComparer.Instance);

    internal async Task<RiderDetailDataBundle?> ReadAsync(
        IReadOnlyList<JsonObject> sessions,
        string? riderQuery = null,
        string? nation = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default,
        bool includeSplits = true)
    {
        var sessionIds = sessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sessionIds.Length == 0)
        {
            return new RiderDetailDataBundle(
                new RankingDataBundle([], [], [], [], new Dictionary<string, decimal>()),
                []);
        }

        var packages = new Dictionary<string, JsonObject>();
        var packageKind = includeSplits ? "inputs-full" : "inputs-metrics";
        var revision = _revisions is { Enabled: true } ? await _revisions.GetAsync(cancellationToken) : null;
        string PackageKey(string id) => $"ranking-package:{packageKind}:{role}:{id}" +
            (revision is null ? "" : ":" + revision.ForSessions([id]));
        foreach (var id in sessionIds)
        {
            if (_packageCache?.TryGetValue<JsonObject>(PackageKey(id), out var package) == true)
            {
                ApiTelemetry.Cache("memory", "hit");
                packages[id] = package!;
            }
            else ApiTelemetry.Cache("memory", _packageCache is null ? "bypass" : "miss");
        }
        var missingIds = sessionIds.Where(id => !packages.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            var response = await _supabase.RpcAsync(
                "get_ranking_input_packages",
                new JsonObject { ["p_session_ids"] = LongArray(missingIds), ["p_include_splits"] = includeSplits }, role, cancellationToken);
            foreach (var row in (response as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (row.String("session_id") is not { } id) continue;
                packages[id] = row;
                _packageCache?.Set(PackageKey(id), row, TimeSpan.FromSeconds(30));
            }
        }
        // Keep RPC ordering; the parser reads cached rows without creating a merged JSON copy.
        var cacheRows = packages.Values.OrderBy(row => PublicDtoMapping.Long(row.String("session_id"))).ToArray();
        if (cacheRows.Length == 0)
        {
            ApiTelemetry.Cache("preaggregation", "unavailable");
            ApiTelemetry.Cache("preaggregation", "fallback");
            return null;
        }

        var cachedSessionIds = cacheRows
            .Select(row => row.String("session_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sessionIds.Any(id => !cachedSessionIds.Contains(id)))
        {
            ApiTelemetry.Cache("preaggregation", "unavailable");
            ApiTelemetry.Cache("preaggregation", "fallback");
            return null;
        }

        ApiTelemetry.Cache("preaggregation", "hit");

        if (_readOptions.TypedRanking && !includeSplits)
        {
            var payloads = cacheRows.Select(row => row["payload"]).OfType<JsonObject>().ToArray();
            var read = CompactMetricsPackageParser.ParseRankingReadData(payloads);
            var pace = new List<PacePrecomputedSessionData>();
            lock (_preparedPackages)
            {
                var unprepared = payloads.Where(payload => !_preparedPackages.ContainsKey(payload)).ToArray();
                if (unprepared.Length > 0 && unprepared.All(payload => payload.Int("v") == 3))
                {
                    // Decode the full field once, then retain session-scoped inputs for subset comparisons.
                    var prepared = CompactMetricsPackageParser.ParsePreparedRankingRead(unprepared,
                        unprepared.Length == payloads.Length ? read : null).PaceSessions;
                    var bySession = prepared.GroupBy(session => session.SessionId)
                        .ToDictionary(group => group.Key, group => (IReadOnlyList<PacePrecomputedSessionData>)group.ToArray());
                    foreach (var payload in unprepared)
                    {
                        var ids = (payload["s"] as JsonArray)?.OfType<JsonArray>()
                            .Select(row => row[0]!.ToString()).ToHashSet() ?? [];
                        _preparedPackages[payload] = ids.SelectMany(id => bySession.GetValueOrDefault(id, [])).ToArray();
                    }
                }
            }
            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Packages are immutable and revision-specific. Scoped reuse never outlives the request.
                lock (_preparedPackages)
                {
                    if (!_preparedPackages.TryGetValue(payload, out var prepared))
                    {
                        prepared = CompactMetricsPackageParser.ParsePreparedRankingRead([payload]).PaceSessions;
                        _preparedPackages[payload] = prepared;
                    }
                    pace.AddRange(prepared);
                }
            }
            read = read with { Results = read.Results.Where(r => MatchesRankingRider(r.Rider, riderQuery, nation)).ToArray() };
            var selectedRiderIds = read.Results.Select(r => r.String("rider_id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new RiderDetailDataBundle(read.ToLegacy(), pace.Where(p => selectedRiderIds.Contains(p.Run.RiderId)).ToArray(), read);
        }

        var parsed = CompactMetricsPackageParser.ParseRiderDetailDataBundle(cacheRows
            .Select(row => row["payload"])
            .OfType<JsonObject>().ToArray());
        if (string.IsNullOrWhiteSpace(riderQuery) && string.IsNullOrWhiteSpace(nation))
        {
            return parsed;
        }

        var filteredResults = parsed.Data.Results
            .Where(result => MatchesRankingRider(result.Object("riders"), riderQuery, nation))
            .ToArray();
        var riderIds = filteredResults
            .Select(result => result.String("rider_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RiderDetailDataBundle(
            parsed.Data with { Results = filteredResults },
            parsed.PaceSessions
                .Where(session => riderIds.Contains(session.Run.RiderId))
                .ToArray());
    }

    internal static bool MatchesRankingRider(JsonObject? rider, string? riderQuery, string? nation)
    {
        if (rider is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(nation) &&
            !string.Equals(rider.String("nation"), nation.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(riderQuery))
        {
            return true;
        }

        var query = riderQuery.Trim();
        return new[]
            {
                rider.String("first_name"), rider.String("last_name"), rider.String("uci_id"),
                rider.String("uci_code"), rider.DisplayName()
            }
            .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }
}
