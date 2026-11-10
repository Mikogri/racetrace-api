using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

/// <summary>Refreshes source packages. Incident annotations are retained for explicit experimental tooling only.</summary>
public sealed class RankingPreaggregationService(ISupabaseRestClient supabase)
{
    public static string AlgorithmVersion =>
        "pace-field-v1:" + AlgorithmCatalog.Current.Fingerprint("pace");
    public static string IncidentVersion => IncidentAnnotationCache.Version;

    public async Task<bool> RefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var measurement = ApiTelemetry.Measure("preaggregation_rebuild");
        await supabase.RpcAsync(
            "refresh_ranking_session_preaggregate",
            new JsonObject { ["p_session_id"] = long.Parse(sessionId) },
            SupabaseRole.Service,
            cancellationToken
        );
        return await supabase.GetSingleAsync(
            new SupabaseQuery("ranking_session_preaggregates")
                .Select("session_id")
                .Filter("session_id", "eq", sessionId)
                .Filter("input_splits", "not.is", "null"),
            SupabaseRole.Service,
            cancellationToken
        )
            is not null;
    }

    public async Task<bool> PrepareIncidentsAsync(
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        var source = await supabase.GetSingleAsync(
            new SupabaseQuery("ranking_session_preaggregates")
                .Select("session_id,source_revision")
                .Filter("session_id", "eq", sessionId),
            SupabaseRole.Service,
            cancellationToken
        );
        if (source?.String("source_revision") is not { } revision)
            return false;
        return await PrepareIncidentsAsync(sessionId, revision, cancellationToken);
    }

    private async Task<bool> PrepareIncidentsAsync(
        string sessionId,
        string revision,
        CancellationToken cancellationToken
    )
    {
        var response = await supabase.RpcAsync(
            "get_ranking_input_packages",
            new JsonObject
            {
                ["p_session_ids"] = new JsonArray(long.Parse(sessionId)),
                ["p_include_splits"] = false,
            },
            SupabaseRole.Service,
            cancellationToken
        );
        var payload =
            (response as JsonArray)?.OfType<JsonObject>().SingleOrDefault()?["payload"]
            as JsonObject;
        if (payload is null)
            return false;
        payload.Remove("ia");
        var sessions = CompactMetricsPackageParser.ParseRiderDetailDataBundle(payload).PaceSessions;
        var encoded = IncidentAnnotationCache.Encode(sessions);
        var saved = await supabase.RpcAsync(
            "save_ranking_incident_annotations",
            new JsonObject
            {
                ["p_session_id"] = long.Parse(sessionId),
                ["p_source_revision"] = revision,
                ["p_version"] = IncidentAnnotationCache.Version,
                ["p_payload"] = encoded,
            },
            SupabaseRole.Service,
            cancellationToken
        );
        return saved?.GetValue<bool>() == true;
    }

    public async Task<bool> PrepareAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var measurement = ApiTelemetry.Measure("preaggregation");
        var row = await supabase.GetSingleAsync(
            new SupabaseQuery("ranking_session_preaggregates")
                .Select("session_id,payload,source_revision")
                .Filter("session_id", "eq", sessionId),
            SupabaseRole.Service,
            cancellationToken
        );
        if (
            row?["payload"] is not JsonObject payload
            || row.String("source_revision") is not { } revision
        )
            return false;
        var data = CompactMetricsPackageParser.ParseRiderDetailDataBundle(payload);
        var finishes = new JsonArray();
        foreach (var session in data.PaceSessions)
        {
            var finish = session.PaceFinish;
            var summary = finish?.Statistics;
            finishes.Add(
                new JsonArray(
                    session.SessionId,
                    session.Run.RiderId,
                    finish?.TimeSeconds,
                    finish?.RemovedLossSeconds,
                    summary?.Count,
                    summary?.Median,
                    summary?.LogMedian,
                    summary?.LogMadScale,
                    summary?.LinearMadScale,
                    summary?.BestValue,
                    summary?.SecondBestValue
                )
            );
        }
        var saved = await supabase.RpcAsync(
            "save_prepared_ranking_session",
            new JsonObject
            {
                ["p_session_id"] = long.Parse(sessionId),
                ["p_source_revision"] = revision,
                ["p_algorithm_version"] = AlgorithmVersion,
                ["p_payload"] = finishes,
            },
            SupabaseRole.Service,
            cancellationToken
        );
        return saved?.GetValue<bool>() == true;
    }
}
