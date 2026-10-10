using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Services;

/// <summary>Prepares field-dependent finish references using the same calculator as reads.</summary>
public sealed class RankingPreaggregationService(ISupabaseRestClient supabase)
{
    public static string AlgorithmVersion => "pace-field-v1:" + AlgorithmCatalog.Current.Fingerprint("pace");

    public async Task<bool> RefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var measurement = ApiTelemetry.Measure("preaggregation_rebuild");
        await supabase.RpcAsync("refresh_ranking_session_preaggregate",
            new JsonObject { ["p_session_id"] = long.Parse(sessionId) }, SupabaseRole.Service, cancellationToken);
        return await PrepareAsync(sessionId, cancellationToken);
    }

    public async Task<bool> PrepareAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var measurement = ApiTelemetry.Measure("preaggregation");
        var row = await supabase.GetSingleAsync(new SupabaseQuery("ranking_session_preaggregates")
            .Select("session_id,payload,source_revision").Filter("session_id", "eq", sessionId),
            SupabaseRole.Service, cancellationToken);
        if (row?["payload"] is not JsonObject payload || row.String("source_revision") is not { } revision)
            return false;
        var data = CompactMetricsPackageParser.ParseRiderDetailDataBundle(payload);
        var finishes = new JsonArray();
        foreach (var session in data.PaceSessions)
        {
            var finish = session.PaceFinish;
            var summary = finish?.Statistics;
            finishes.Add(new JsonArray(session.SessionId, session.Run.RiderId,
                finish?.TimeSeconds, finish?.RemovedLossSeconds,
                summary?.Count, summary?.Median, summary?.LogMedian, summary?.LogMadScale,
                summary?.LinearMadScale, summary?.BestValue, summary?.SecondBestValue));
        }
        var saved = await supabase.RpcAsync("save_prepared_ranking_session", new JsonObject
        {
            ["p_session_id"] = long.Parse(sessionId),
            ["p_source_revision"] = revision,
            ["p_algorithm_version"] = AlgorithmVersion,
            ["p_payload"] = finishes
        }, SupabaseRole.Service, cancellationToken);
        return saved?.GetValue<bool>() == true;
    }
}
