using System.Text.Json.Nodes;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public static class PublicDtoMapping
{
    public static long Long(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;

    public static long? NullableLong(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : null;

    public static DateOnly? Date(string? value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(value, out var parsed)
                ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : DateOnly.TryParse(value, out date) ? date
            : null;
    }

    public static decimal? Seconds(int? milliseconds) =>
        milliseconds is null ? null : Math.Round(milliseconds.Value / 1000m, 3);

    public static ConditionDto? Condition(JsonObject? condition)
    {
        if (condition is null)
        {
            return null;
        }

        var name = condition.String("name");
        return new ConditionDto(NullableLong(condition.String("id")), Slug(name), name);
    }

    public static string? Slug(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant().Replace(' ', '_');
    }
}
