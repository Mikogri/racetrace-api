using System.Globalization;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Infrastructure.Calculations;

public sealed class DataRevisionSnapshot(IReadOnlyDictionary<string, long> revisions)
{
    public static DataRevisionSnapshot Unversioned { get; } = new(new Dictionary<string, long>());
    public string ForScopes(IEnumerable<string> scopes) => AlgorithmCatalog.Hash(scopes.Append("global")
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .ToDictionary(scope => scope, scope => revisions.GetValueOrDefault(scope)));

    public string ForSessions(IEnumerable<string> ids) => ForScopes(ids.Select(id => "session:" + id));
    public string ForContext(RiderMetricsContext? context)
    {
        if (!string.IsNullOrEmpty(context?.RoundId))
            return ForScopes(long.TryParse(context.RoundId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var round)
                ? ["round:" + round.ToString(CultureInfo.InvariantCulture)] : ["all"]);
        var years = context?.SeasonYears ?? [];
        return ForScopes(years.Count == 0 ? ["all"] : years.Select(year => "season:" + year.ToString(CultureInfo.InvariantCulture)));
    }
}
