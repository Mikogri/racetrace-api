using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class CompareInputPackageReader(
    ISupabaseRestClient supabase,
    IOptions<ReadOptimizationOptions>? options = null
)
{
    internal async Task<RiderDetailDataBundle?> ReadAsync(
        RiderMetricsContext context,
        IReadOnlyList<string> riderIds,
        CancellationToken cancellationToken
    )
    {
        var payload = DomainQueryService.BuildMetricsRpcPayload(context, null);
        payload["p_rider_ids"] = new JsonArray(
            riderIds
                .Select(id =>
                    (JsonNode?)JsonValue.Create(long.Parse(id, CultureInfo.InvariantCulture))
                )
                .ToArray()
        );
        payload["p_use_preaggregation"] =
            (options?.Value.PreparedRiderDetails ?? false)
            && string.IsNullOrWhiteSpace(context.Status);
        JsonNode? response;
        try
        {
            response = await supabase.RpcAsync(
                "get_compare_input_data",
                payload,
                SupabaseRole.Anon,
                cancellationToken
            );
        }
        catch (SupabaseApiException error)
            when (error.StatusCode == 404
                && error.ResponseBody.Contains("\"PGRST202\"", StringComparison.Ordinal)
            )
        {
            return null;
        }
        if (
            response is not JsonObject envelope
            || envelope.Bool("complete") != true
            || envelope["payload"] is not JsonObject data
            || !CompactMetricsPackageParser.HasCompleteInputMeasurements(data)
        )
            throw new ApiException(
                502,
                "COMPARE_DATA_INVALID",
                "Supabase returned an invalid comparison input package."
            );
        return DomainQueryService.RestrictToCategories(
            DomainQueryService.ParseRiderDetailDataBundle(data),
            context
        );
    }

    internal static RiderDetailDataBundle ForRider(RiderDetailDataBundle shared, string riderId)
    {
        var results = shared
            .Data.Results.Where(result =>
                string.Equals(
                    result.String("rider_id"),
                    riderId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var resultIds = results
            .Select(result => result.String("id")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RiderDetailDataBundle(
            shared.Data with
            {
                Results = results,
                Splits = shared
                    .Data.Splits.Where(split =>
                        resultIds.Contains(split.String("result_id") ?? string.Empty)
                    )
                    .ToArray(),
                PointsByResultId = shared
                    .Data.PointsByResultId.Where(pair => resultIds.Contains(pair.Key))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase
                    ),
            },
            shared
                .PaceSessions.Where(session =>
                    string.Equals(session.Run.RiderId, riderId, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray()
        );
    }
}
