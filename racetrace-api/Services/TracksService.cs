using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class TracksService
{
    private static readonly Dictionary<string, string> TrackSorts = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["trackName"] = "name",
        ["name"] = "name",
        ["venueName"] = "venue",
        ["venue"] = "venue",
        ["countryCode"] = "country_code",
        ["country_code"] = "country_code",
        ["length_m"] = "length_m",
        ["avg_slope"] = "avg_slope",
    };

    private static readonly Dictionary<string, string> RecordsSorts = new[]
    {
        "race_time_ms",
        "top_speed",
        "date",
        "rider_name",
        "category",
    }.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;

    public TracksService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries
    )
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
    }

    public async Task<PublicPagedResponse<TrackFilterOptionDto>> ListTracksAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var page = _validator.Page(queryString);
        var requestedSort = _validator.Sort(queryString, TrackSorts, "venueName", "asc");
        var sort = new SortRequest(TrackSorts[requestedSort.Sort], requestedSort.Order);
        var query = new SupabaseQuery("tracks")
            .Select("*")
            .Limit(page.Limit)
            .Offset(page.Offset)
            .Order(sort.Sort, sort.Descending);

        query.Filter(
            "country_code",
            "eq",
            _validator.StringAny(queryString, "countryCode", "country_code")
        );
        query.Filter("venue", "ilike", _validator.StringAny(queryString, "venueName", "venue"));
        query.Filter("name", "ilike", _validator.StringAny(queryString, "trackName", "name"));
        query.Filter("length_m", "gte", _validator.Int(queryString, "min_length_m"));
        query.Filter("length_m", "lte", _validator.Int(queryString, "max_length_m"));
        query.Filter("avg_slope", "gte", _validator.Decimal(queryString, "min_avg_slope"));
        query.Filter("avg_slope", "lte", _validator.Decimal(queryString, "max_avg_slope"));

        var q = _validator.StringAny(queryString, "search", "q");
        if (!string.IsNullOrWhiteSpace(q))
        {
            var safe = q.Replace("*", string.Empty, StringComparison.Ordinal);
            query.Or($"name.ilike.*{safe}*,venue.ilike.*{safe}*,country_code.ilike.*{safe}*");
        }

        var response = await _supabase.GetAsync(
            query,
            count: true,
            cancellationToken: cancellationToken
        );
        var items = response.Data.Select(ToFilterOption).ToArray();
        return PublicPagedResponse<TrackFilterOptionDto>.Create(
            items,
            page.Page,
            page.Limit,
            response.Total ?? items.Length
        );
    }

    public async Task<JsonObject> GetTrackAsync(
        string trackId,
        string[] include,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var track = await _domainQueries.FindByIdAsync(
            "tracks",
            trackId,
            "*,rounds(*)",
            cancellationToken: cancellationToken
        );
        if (track is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "TRACK_NOT_FOUND",
                "Track was not found."
            );
        }

        SortEmbeddedRoundsByDate(track);

        if (include.Contains("records", StringComparer.OrdinalIgnoreCase))
        {
            track["records"] = new JsonArray(
                (await BuildRecordsAsync(trackId, queryString, cancellationToken))
                    .Data.Select(row => row.DeepClone())
                    .ToArray()
            );
        }

        return track;
    }

    internal static void SortEmbeddedRoundsByDate(JsonObject track)
    {
        var rounds = track.Array("rounds")?.OfType<JsonObject>().ToArray();
        if (rounds is null)
        {
            return;
        }

        track["rounds"] = new JsonArray(
            rounds
                .OrderByDescending(round => PublicDtoMapping.Date(round.String("start_date")))
                .ThenByDescending(round => PublicDtoMapping.Long(round.String("id")))
                .Select(round => round.DeepClone())
                .ToArray()
        );
    }

    public async Task<ListResponse<JsonObject>> BuildRecordsAsync(
        string trackId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var page = _validator.Page(queryString);
        var sort = _validator.Sort(queryString, RecordsSorts, "race_time_ms", "asc");
        var context = (
            await _domainQueries.ParseMetricsContextAsync(
                queryString,
                _validator,
                cancellationToken
            )
        ) with
        {
            TrackId = trackId,
        };
        var results = await _domainQueries.GetMatchingResultsAsync(
            context,
            cancellationToken: cancellationToken
        );
        var rows = results
            .Where(result => result.Decimal("race_time_ms") is not null)
            .Select(result =>
            {
                var session = result.Object("sessions");
                result["date"] =
                    session?.String("session_date")
                    ?? session?.Object("rounds")?.String("start_date");
                result["rider_name"] = result.Object("riders")?.DisplayName();
                result["category"] = session?.Object("categories")?.String("code");
                return result;
            })
            .ToArray();
        var sorted = JsonSorting.Sort(rows, sort.Sort, sort.Descending);
        return ResponseFactory.Page(
            sorted.Skip(page.Offset).Take(page.Limit).ToArray(),
            page.Page,
            page.Limit,
            sorted.Count,
            sort.Sort,
            sort.Order
        );
    }

    private static TrackFilterOptionDto ToFilterOption(JsonObject track)
    {
        var id = PublicDtoMapping.Long(track.String("id"));
        return new TrackFilterOptionDto(
            id,
            track.String("name"),
            id,
            track.String("venue"),
            track.String("country_code"),
            track.Int("length_m"),
            track.Decimal("avg_slope")
        );
    }
}
