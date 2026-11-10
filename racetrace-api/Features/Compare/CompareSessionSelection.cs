using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Compare;

/// <summary>Selects rider-owned results and shared started sessions without constructing response DTOs.</summary>
internal static class CompareSessionSelection
{
    internal static IEnumerable<string> SessionIds(IReadOnlyList<CompareRiderSource> sources) =>
        sources
            .SelectMany(SourceResults)
            .Select(ResultSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static SelectedSessionResult[] SelectSessionResults(
        IReadOnlyList<CompareRiderSource> sources,
        string sessionId
    ) =>
        sources
            .Select(source => new
            {
                Source = source,
                Result = SourceResults(source)
                    .Where(result =>
                        string.Equals(
                            ResultSessionId(result),
                            sessionId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .OrderByDescending(result => PublicDtoMapping.Long(result.String("id")))
                    .FirstOrDefault(),
            })
            .Where(item => item.Result is not null)
            .Select(item => new SelectedSessionResult(item.Source, item.Result!))
            .ToArray();

    internal static IReadOnlyList<string> FindCommonSessionIds(
        IReadOnlyList<CompareRiderSource> sources
    )
    {
        HashSet<string>? common = null;
        foreach (var source in sources)
        {
            var riderSessions = SourceResults(source)
                .Where(RankingService.HasStarted)
                .Select(ResultSessionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (common is null)
            {
                common = riderSessions;
            }
            else
            {
                common.IntersectWith(riderSessions);
            }
        }

        return common?.ToArray() ?? [];
    }

    internal static IReadOnlyList<JsonObject> SourceResults(CompareRiderSource source) =>
        source
            .Snapshot.Results.Where(result =>
                string.Equals(
                    result.String("rider_id"),
                    source.RiderId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();

    internal static string? ResultSessionId(JsonObject result) =>
        result.String("session_id") ?? result.Object("sessions")?.String("id");

    internal static string ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id")
            ?? session?.Object("rounds")?.String("id")
            ?? string.Empty;
    }

    internal static string ResultDate(JsonObject result) =>
        result.Object("sessions")?.String("session_date")
        ?? result.Object("sessions")?.Object("rounds")?.String("start_date")
        ?? string.Empty;

    internal sealed record SelectedSessionResult(CompareRiderSource Source, JsonObject Result);
}
