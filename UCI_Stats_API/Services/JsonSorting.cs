using System.Text.Json.Nodes;

namespace UCI_Stats_API.Services;

public static class JsonSorting
{
    public static IReadOnlyList<JsonObject> Sort(IReadOnlyList<JsonObject> rows, string sort, bool descending)
    {
        IOrderedEnumerable<JsonObject> sorted = descending
            ? rows.OrderByDescending(row => SortValue(row, sort))
            : rows.OrderBy(row => SortValue(row, sort));

        return sorted.ThenBy(row => row.String("id")).ToArray();
    }

    private static object SortValue(JsonObject row, string sort)
    {
        var directNumber = row.Decimal(sort);
        if (directNumber is not null)
        {
            return directNumber.Value;
        }

        var directString = row.String(sort);
        if (directString is not null)
        {
            return directString;
        }

        return sort switch
        {
            "rider_name" => row.Object("riders")?.DisplayName() ?? row.Object("rider")?.DisplayName() ?? string.Empty,
            "rider_nation" => row.Object("riders")?.String("nation") ?? row.Object("rider")?.String("nation") ?? string.Empty,
            "display_name" => row.DisplayName(),
            "category" => row.Object("categories")?.String("code") ?? row.Object("sessions")?.Object("categories")?.String("code") ?? string.Empty,
            "date" => row.Object("sessions")?.String("session_date") ?? row.Object("sessions")?.Object("rounds")?.String("start_date") ?? string.Empty,
            "track" => row.Object("sessions")?.Object("rounds")?.Object("tracks")?.String("name") ??
                row.Object("sessions")?.Object("rounds")?.Object("tracks")?.String("venue") ??
                string.Empty,
            "venue" => row.Object("tracks")?.String("venue") ?? row.String("venue") ?? string.Empty,
            "competition_class" => row.Object("competition_levels")?.String("code") ?? string.Empty,
            "round_status" => row.Array("sessions")?.OfType<JsonObject>().Select(session => session.String("status")).FirstOrDefault(status => !string.IsNullOrWhiteSpace(status)) ?? string.Empty,
            _ => string.Empty
        };
    }
}
