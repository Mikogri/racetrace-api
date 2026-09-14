using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Metrics;

/// <summary>
/// Maps HTTP aliases to the existing metric context. Missing and empty lists both mean no restriction;
/// preserve input order and do not introduce sorting or defaults here.
/// </summary>
public static class MetricsQueryParser
{
    public static RiderMetricsContext Parse(IQueryCollection query, QueryValidationService validator, string? forcedStatus = null)
    {
        var seasonYears = ParseSeasonYears(query, validator);
        var trackIds = validator.CsvAny(query, "trackId", "track_id", "tracks");
        var venueIds = validator.CsvAny(query, "venueId", "venue_id");
        trackIds = trackIds.Concat(venueIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var venueNames = validator.CsvAny(query, "venue", "venueName", "venue_name");
        var countryCodes = validator.CsvAny(query, "countryCode", "country_code")
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categoryValues = validator.CsvAny(
            query,
            "category",
            "categories",
            "categoryCode",
            "categoryCodes",
            "category_code",
            "category_codes");
        var categoryCodes = categoryValues.Select(value =>
        {
            try
            {
                return PublicApiCodes.ToDatabaseCategory(value);
            }
            catch (ArgumentException)
            {
                throw validator.BadQuery($"Unsupported category '{value}'. Allowed values: {string.Join(", ", PublicApiCodes.PublicCategories)}.");
            }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sessionValues = validator.CsvAny(query, "sessionType", "session_type");
        var sessionTypes = sessionValues.Select(value =>
        {
            try
            {
                return PublicApiCodes.ToDatabaseSessionType(value);
            }
            catch (ArgumentException)
            {
                throw validator.BadQuery($"Unsupported sessionType '{value}'. Allowed values: {string.Join(", ", PublicApiCodes.PublicSessionTypes)}.");
            }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dateFrom = validator.DateAny(query, "dateFrom", "date_from", "from_date");
        var dateTo = validator.DateAny(query, "dateTo", "date_to", "to_date");
        validator.ValidateDateRange(dateFrom, dateTo);
        var competitionClassIds = validator.IntCsvAny(query, "competitionClassId", "competition_class_id");
        var competitionClassCodes = validator.CsvAny(query, "competitionClassCode", "competition_class_code", "class");
        var conditionIds = validator.IntCsvAny(query, "conditionId", "condition_id", "conditions");
        var conditionNames = validator.CsvAny(query, "condition");

        return new RiderMetricsContext(
            seasonYears.Count == 1 ? seasonYears[0] : null,
            categoryCodes.Length == 1 ? categoryCodes[0] : null,
            dateFrom,
            dateTo,
            validator.StringAny(query, "roundId", "round_id"),
            trackIds.Length == 1 ? trackIds[0] : null,
            countryCodes.Length == 1 ? countryCodes[0] : null,
            conditionIds.Length == 1 ? conditionIds[0] : null,
            competitionClassCodes.Length == 1 ? competitionClassCodes[0] : null,
            sessionTypes.Length == 1 ? sessionTypes[0] : null,
            forcedStatus ?? validator.String(query, "status"))
        {
            ConditionIds = conditionIds,
            SeasonYears = seasonYears,
            TrackIds = trackIds,
            Venue = venueNames.Length == 1 ? venueNames[0] : null,
            VenueNames = venueNames,
            CountryCodes = countryCodes,
            TrackName = validator.StringAny(query, "trackName", "track_name"),
            CompetitionClassIds = competitionClassIds,
            CompetitionClassCodes = competitionClassCodes,
            CategoryCodes = categoryCodes,
            SessionTypes = sessionTypes,
            ConditionNames = conditionNames
        };
    }

    private static IReadOnlyList<int> ParseSeasonYears(IQueryCollection query, QueryValidationService validator)
    {
        var years = validator.IntCsvAny(query, "season", "seasons", "seasonYears", "season_years");
        if (years.Length > 0)
        {
            if (years.Any(year => year is < 1990 or > 2100))
            {
                throw validator.BadQuery("season must be between 1990 and 2100.");
            }

            return years;
        }

        var year = validator.IntAny(query, "seasonYear", "season_year");
        if (year is < 1990 or > 2100)
        {
            throw validator.BadQuery("season must be between 1990 and 2100.");
        }

        return year is null ? [] : [year.Value];
    }

}
