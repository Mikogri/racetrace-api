using System.Globalization;

namespace RaceTrace.Api.Infrastructure.Calculations;

/// <summary>Revision scopes for cached HTTP responses. Unknown/broad queries use all data.
/// This does not validate or rewrite the user's actual filters.</summary>
public static class RequestCalculationIdentity
{
    public static string[] Scopes(HttpContext context)
    {
        // Predictions consume historical seasons as well as the selected season.
        if ((context.Request.Path.Value ?? "").Contains("podium", StringComparison.OrdinalIgnoreCase)) return ["all"];
        if (context.Request.RouteValues.TryGetValue("roundId", out var round) && round is not null)
            return NumericScope("round", round.ToString());
        if (context.Request.RouteValues.TryGetValue("sessionId", out var session) && session is not null)
            return NumericScope("session", session.ToString());
        var query = context.Request.Query;
        string[] keys = ["season", "seasons", "seasonYear", "season_year", "seasonYears", "season_years"];
        var years = keys.SelectMany(key => Parts(query[key].ToString())).ToArray();
        if (years.Length == 0) return ["all"];
        // Comparison can refer to a second season set; omission may mean an unrestricted baseline.
        var comparisonYears = Parts(query["comparisonSeason"].ToString());
        if (query["comparison"].ToString().Equals("previousFilters", StringComparison.OrdinalIgnoreCase) && comparisonYears.Length == 0) return ["all"];
        var scopes = years.Concat(comparisonYears).SelectMany(year => NumericScope("season", year)).ToArray();
        return scopes.Contains("all") ? ["all"] : scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Parts(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string[] NumericScope(string prefix, string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? [prefix + ":" + id.ToString(CultureInfo.InvariantCulture)] : ["all"];

    public static string Algorithm(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.Contains("fantasy", StringComparison.OrdinalIgnoreCase)) return "fantasy";
        if (path.Contains("podium", StringComparison.OrdinalIgnoreCase)) return "podium";
        if (path.Contains("round", StringComparison.OrdinalIgnoreCase)) return "round-analytics";
        return "rider-analytics";
    }
}
