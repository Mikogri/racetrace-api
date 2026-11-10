using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class RoundsService
{
    private static readonly Dictionary<string, string> RoundSorts = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["roundNumber"] = "round_number",
        ["round_number"] = "round_number",
        ["season"] = "season_year",
        ["trackName"] = "tracks(name)",
        ["venueName"] = "tracks(venue)",
        ["context_name"] = "context_name",
        ["venue"] = "tracks(venue)",
        ["countryCode"] = "tracks(country_code)",
        ["country_code"] = "tracks(country_code)",
        ["startDate"] = "start_date",
        ["start_date"] = "start_date",
        ["endDate"] = "end_date",
        ["end_date"] = "end_date",
        ["competitionClassName"] = "competition_levels(name)",
        ["competitionClassPriority"] = "competition_levels(priority)",
    };

    private static readonly Dictionary<string, string> SessionSorts = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["name"] = "name",
        ["sessionType"] = "session_type",
        ["type"] = "session_type",
        ["category"] = "category_id",
        ["sessionDate"] = "session_date",
        ["session_date"] = "session_date",
        ["status"] = "status",
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly QueryValidationService _validator;
    private readonly DomainQueryService _domainQueries;
    private readonly RoundImageQueries _images;

    public RoundsService(
        ISupabaseRestClient supabase,
        QueryValidationService validator,
        DomainQueryService domainQueries,
        RoundImageStorageService? imageStorage = null
    )
    {
        _supabase = supabase;
        _validator = validator;
        _domainQueries = domainQueries;
        _images = new RoundImageQueries(supabase, imageStorage);
    }

    public async Task<PublicPagedResponse<RoundListItemDto>> ListRoundsAsync(
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var page = _validator.Page(queryString);
        var requestedSort = _validator.Sort(queryString, RoundSorts, "startDate", "desc");
        var seasons = _validator.IntCsvAny(queryString, "season", "seasonYear", "season_year");
        if (seasons.Length != 1)
        {
            throw _validator.BadQuery("The rounds list requires exactly one season value.");
        }
        if (seasons[0] is < 1990 or > 2100)
        {
            throw _validator.BadQuery("season must be between 1990 and 2100.");
        }

        var query = new SupabaseQuery("rounds")
            .Select(
                "*,tracks(*,venue_images(storage_path)),competition_levels(*),conditions(*),round_categories(*,categories(*),sessions(status)),round_images(round_id,storage_path)"
            )
            .Limit(page.Limit)
            .Offset(page.Offset)
            .Filter("season_year", "eq", seasons[0]);
        var direction = requestedSort.Descending ? "desc" : "asc";
        query.Add("order", $"start_date.{direction}.nullslast,id.{direction}");

        await ApplyRoundFiltersAsync(query, queryString, cancellationToken);
        if (!await ApplyRoundReferenceFiltersAsync(query, queryString, cancellationToken))
        {
            return PublicPagedResponse<RoundListItemDto>.Create([], page.Page, page.Limit, 0);
        }

        var response = await _supabase.GetAsync(
            query,
            count: true,
            cancellationToken: cancellationToken
        );
        foreach (var round in response.Data)
        {
            ResultEmbedMapping.NormalizeRoundEmbed(round);
            round["sessions"] = new JsonArray(
                (round.Array("round_categories") ?? [])
                    .OfType<JsonObject>()
                    .SelectMany(category => category.Array("sessions")?.OfType<JsonObject>() ?? [])
                    .Select(session => session.DeepClone())
                    .ToArray()
            );
            _images.AttachEmbeddedImageMetadata(round);
        }

        var items = response.Data.Select(ToPublicRound).ToArray();
        return PublicPagedResponse<RoundListItemDto>.Create(
            items,
            page.Page,
            page.Limit,
            response.Total ?? items.Length
        );
    }

    public async Task<IReadOnlyList<int>> ListSeasonsAsync(CancellationToken cancellationToken)
    {
        var response = await _supabase.GetAsync(
            new SupabaseQuery("rounds")
                .Select("season_year")
                .Order("season_year", descending: true)
                .Limit(1000),
            cancellationToken: cancellationToken
        );

        return response
            .Data.Select(row => row["season_year"]?.GetValue<int?>())
            .Where(year => year is not null)
            .Select(year => year!.Value)
            .Distinct()
            .OrderByDescending(year => year)
            .ToArray();
    }

    public Task<RoundImageFile?> GetRoundImageAsync(
        long roundId,
        CancellationToken cancellationToken
    ) => _images.GetRoundImageAsync(roundId, cancellationToken);

    public async Task<JsonObject> GetRoundAsync(
        string roundId,
        string[] include,
        CancellationToken cancellationToken
    )
    {
        var round = await _domainQueries.FindByIdAsync(
            "rounds",
            roundId,
            "*,tracks(*),competition_levels(*),conditions(*),round_categories(*,categories(*))",
            cancellationToken: cancellationToken
        );

        if (round is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "ROUND_NOT_FOUND",
                "Round was not found."
            );
        }

        ResultEmbedMapping.NormalizeRoundEmbed(round);

        if (include.Contains("sessions", StringComparer.OrdinalIgnoreCase))
        {
            var sessions = await _supabase.GetAsync(
                new SupabaseQuery("sessions")
                    .Select(DomainQueryService.SessionSelect)
                    .Filter("round_id", "eq", roundId)
                    .Order("session_date", false),
                cancellationToken: cancellationToken
            );
            ResultEmbedMapping.NormalizeSessionEmbeds(sessions.Data);
            round["sessions"] = new JsonArray(
                sessions.Data.Select(session => session.DeepClone()).ToArray()
            );
        }

        return round;
    }

    public async Task<PublicPagedResponse<CommonSessionDto>> ListRoundSessionsAsync(
        string roundId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var page = _validator.Page(queryString);
        var requestedSort = _validator.Sort(queryString, SessionSorts, "sessionDate", "asc");
        var sort = new SortRequest(SessionSorts[requestedSort.Sort], requestedSort.Order);
        var query = new SupabaseQuery("sessions")
            .Select(DomainQueryService.SessionSelect)
            .Filter("round_id", "eq", roundId)
            .Limit(page.Limit)
            .Offset(page.Offset)
            .Order(sort.Sort, sort.Descending);

        var categoryValue = _validator.StringAny(
            queryString,
            "category",
            "categoryCode",
            "category_code"
        );
        string? categoryCode = null;
        if (!string.IsNullOrWhiteSpace(categoryValue))
        {
            try
            {
                categoryCode = await _domainQueries.ResolveCategoryCodeAsync(
                    categoryValue,
                    cancellationToken
                );
            }
            catch (ArgumentException)
            {
                throw _validator.BadQuery($"Unsupported category '{categoryValue}'.");
            }
        }

        var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(
            categoryCode,
            cancellationToken: cancellationToken
        );
        if (!string.IsNullOrWhiteSpace(categoryCode) && categoryId is null)
        {
            return PublicPagedResponse<CommonSessionDto>.Create([], page.Page, page.Limit, 0);
        }

        if (categoryId is not null)
        {
            query.Filter("category_id", "eq", categoryId);
        }

        query.Filter("status", "eq", _validator.String(queryString, "status"));
        var type = _validator.StringAny(queryString, "sessionType", "session_type", "type");
        if (!string.IsNullOrWhiteSpace(type))
        {
            try
            {
                query.Filter(
                    "session_type",
                    "eq",
                    await _domainQueries.ResolveSessionTypeCodeAsync(type, cancellationToken)
                );
            }
            catch (ArgumentException)
            {
                throw _validator.BadQuery($"Unsupported sessionType '{type}'.");
            }
        }

        var q = _validator.StringAny(queryString, "search", "q");
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Filter(
                "name",
                "ilike",
                $"*{q.Replace("*", string.Empty, StringComparison.Ordinal)}*"
            );
        }

        var response = await _supabase.GetAsync(
            query,
            count: true,
            cancellationToken: cancellationToken
        );
        ResultEmbedMapping.NormalizeSessionEmbeds(response.Data);
        var items = response
            .Data.Select(session => new CommonSessionDto(
                PublicDtoMapping.Long(session.String("id")),
                PublicDtoMapping.Long(
                    session.String("round_id") ?? session.Object("rounds")?.String("id")
                ),
                PublicApiCodes.ToPublicSessionType(session.String("session_type")),
                PublicApiCodes.ToPublicCategory(session.Object("categories")?.String("code")),
                ParseDate(session.String("session_date")),
                []
            ))
            .ToArray();
        return PublicPagedResponse<CommonSessionDto>.Create(
            items,
            page.Page,
            page.Limit,
            response.Total ?? items.Length
        );
    }

    public async Task<string> ResolveResultsSessionIdAsync(
        string roundId,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var categoryValue =
            _validator.StringAny(queryString, "category", "categoryCode", "category_code")
            ?? throw _validator.BadQuery("category is required for round results.");
        var sessionTypeValue =
            _validator.StringAny(queryString, "sessionType", "session_type")
            ?? throw _validator.BadQuery("sessionType is required for round results.");
        string categoryCode;
        string sessionType;
        try
        {
            categoryCode = await _domainQueries.ResolveCategoryCodeAsync(
                categoryValue,
                cancellationToken
            );
            sessionType = await _domainQueries.ResolveSessionTypeCodeAsync(
                sessionTypeValue,
                cancellationToken
            );
        }
        catch (ArgumentException exception)
        {
            throw _validator.BadQuery(exception.Message);
        }

        var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(
            categoryCode,
            cancellationToken: cancellationToken
        );
        if (categoryId is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "CATEGORY_NOT_FOUND",
                "Category was not found."
            );
        }

        var sessions = await _supabase.GetAsync(
            new SupabaseQuery("sessions")
                .Select("id")
                .Filter("round_id", "eq", roundId)
                .Filter("category_id", "eq", categoryId)
                .Filter("session_type", "eq", sessionType)
                .Limit(2),
            cancellationToken: cancellationToken
        );
        if (sessions.Data.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "SESSION_NOT_FOUND",
                "No session matches the requested round, category, and sessionType."
            );
        }

        if (sessions.Data.Count > 1)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "SESSION_AMBIGUOUS",
                "More than one session matches the requested round filters."
            );
        }

        return sessions.Data[0].String("id")!;
    }

    private async Task ApplyRoundFiltersAsync(
        SupabaseQuery query,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var q = _validator.StringAny(queryString, "search", "q");
        if (!string.IsNullOrWhiteSpace(q))
        {
            var safe = new string(
                q.Where(character => character is not ('*' or ',' or '(' or ')')).ToArray()
            ).Trim();
            var tracks = await _supabase.GetAsync(
                new SupabaseQuery("tracks")
                    .Select("id")
                    .Or($"name.ilike.*{safe}*,venue.ilike.*{safe}*,country_code.ilike.*{safe}*"),
                cancellationToken: cancellationToken
            );
            var trackIds = tracks
                .Data.Select(track => track.String("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var expressions = new List<string> { $"context_name.ilike.*{safe}*" };
            if (trackIds.Length > 0)
            {
                expressions.Add($"track_id.in.({string.Join(',', trackIds)})");
            }

            query.Or(string.Join(',', expressions));
        }
    }

    private async Task<bool> ApplyRoundReferenceFiltersAsync(
        SupabaseQuery query,
        IQueryCollection queryString,
        CancellationToken cancellationToken
    )
    {
        var competitionClassCode = _validator.StringAny(
            queryString,
            "competitionClassCode",
            "competition_class_code"
        );
        var competitionClassId = _validator.IntAny(
            queryString,
            "competitionClassId",
            "competition_class_id"
        );
        if (competitionClassId is not null)
        {
            if (competitionClassId <= 0)
            {
                throw _validator.BadQuery("competitionClassId must be a positive integer.");
            }

            query.Filter("competition_level_id", "eq", competitionClassId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(competitionClassCode))
        {
            var resolvedCompetitionClassId = await _domainQueries.FindCompetitionClassIdByCodeAsync(
                competitionClassCode,
                cancellationToken: cancellationToken
            );
            if (resolvedCompetitionClassId is null)
            {
                return false;
            }

            query.Filter("competition_level_id", "eq", resolvedCompetitionClassId);
        }

        var countryCode = _validator.StringAny(queryString, "countryCode", "country_code");
        var venue = _validator.StringAny(queryString, "venueName", "venue");
        var trackName = _validator.StringAny(queryString, "trackName", "track_name");
        if (
            !string.IsNullOrWhiteSpace(countryCode)
            || !string.IsNullOrWhiteSpace(venue)
            || !string.IsNullOrWhiteSpace(trackName)
        )
        {
            var trackIds = await _domainQueries.FindTrackIdsAsync(
                countryCode,
                venue,
                trackName,
                cancellationToken: cancellationToken
            );
            if (trackIds.Count == 0)
            {
                return false;
            }

            query.In("track_id", trackIds);
        }

        var status = _validator.String(queryString, "status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.ToLowerInvariant();
            if (
                normalizedStatus
                is not ("upcoming" or "confirmed" or "live" or "completed" or "cancelled")
            )
            {
                throw _validator.BadQuery(
                    "status must be upcoming, confirmed, live, completed, or cancelled."
                );
            }

            var current = DateTimeOffset.UtcNow;
            var now = current.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (normalizedStatus == "confirmed" || normalizedStatus == "cancelled")
            {
                query.Filter("status", "eq", normalizedStatus);
            }
            else if (normalizedStatus == "upcoming")
            {
                query.Filter("start_date", "gt", now);
            }
            else if (normalizedStatus == "completed")
            {
                query.Or(
                    $"status.eq.completed,end_date.lt.{current.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)}"
                );
            }
            else
            {
                var sessions = await _supabase.GetAsync(
                    new SupabaseQuery("sessions")
                        .Select("round_id")
                        .In("status", ["live", "active", "running"]),
                    cancellationToken: cancellationToken
                );
                var roundIds = sessions
                    .Data.Select(row => row.String("round_id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var expressions = new List<string>
                {
                    "status.eq.live",
                    "status.eq.active",
                    "status.eq.running",
                    $"and(start_date.lte.{now},end_date.gte.{current.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)})",
                };
                if (roundIds.Length > 0)
                {
                    expressions.Add($"id.in.({string.Join(',', roundIds)})");
                }

                query.Or(string.Join(',', expressions));
            }
        }

        var categoryValue = _validator.StringAny(
            queryString,
            "category",
            "categoryCode",
            "category_code"
        );
        string? categoryCode = null;
        if (!string.IsNullOrWhiteSpace(categoryValue))
        {
            try
            {
                categoryCode = await _domainQueries.ResolveCategoryCodeAsync(
                    categoryValue,
                    cancellationToken
                );
            }
            catch (ArgumentException)
            {
                throw _validator.BadQuery($"Unsupported category '{categoryValue}'.");
            }
        }
        if (!string.IsNullOrWhiteSpace(categoryCode))
        {
            var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(
                categoryCode,
                cancellationToken: cancellationToken
            );
            if (categoryId is null)
            {
                return false;
            }

            var roundCategories = await _supabase.GetAsync(
                new SupabaseQuery("round_categories")
                    .Select("round_id")
                    .Filter("category_id", "eq", categoryId),
                cancellationToken: cancellationToken
            );
            var roundIds = roundCategories
                .Data.Select(row => row.String("round_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToArray();

            if (roundIds.Length == 0)
            {
                return false;
            }

            query.In("id", roundIds);
        }

        return true;
    }

    private static RoundListItemDto ToPublicRound(JsonObject round)
    {
        var track = round.Object("tracks");
        var competitionClass =
            round.Object("competition_class") ?? round.Object("competition_levels");
        var roundId = PublicDtoMapping.Long(round.String("id"));
        var trackId = PublicDtoMapping.Long(round.String("track_id") ?? track?.String("id"));
        return new RoundListItemDto(
            roundId,
            round.Int("round_number"),
            round.Int("season_year") ?? 0,
            round.String("context_name"),
            round.String("round_image_url"),
            trackId,
            track?.String("name"),
            trackId == 0 ? null : trackId,
            track?.String("venue"),
            track?.String("country_code"),
            ParseDate(round.String("start_date")),
            ParseDate(round.String("end_date")),
            RoundStatus(round),
            PublicDtoMapping.NullableLong(
                round.String("competition_class_id")
                    ?? round.String("competition_level_id")
                    ?? competitionClass?.String("id")
            ),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            competitionClass?.Int("priority")
        );
    }

    internal static string RoundStatus(JsonObject round, DateTimeOffset? now = null)
    {
        var explicitStatus = round.String("status")?.Trim().ToLowerInvariant();
        if (explicitStatus is "active" or "running")
        {
            return "live";
        }

        if (explicitStatus is "confirmed" or "live" or "cancelled")
        {
            return explicitStatus;
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var sessionStatuses =
            round
                .Array("sessions")
                ?.OfType<JsonObject>()
                .Select(session => session.String("status")?.Trim().ToLowerInvariant())
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .ToArray()
            ?? [];
        if (sessionStatuses.Any(status => status is "live" or "active" or "running"))
        {
            return "live";
        }

        var start = ParseDateTime(round.String("start_date"));
        var end = InclusiveEndBoundary(round.String("end_date"));
        if (start is not null && start > current)
        {
            return "upcoming";
        }

        if (start is not null && start <= current && end is not null && end > current)
        {
            return "live";
        }

        if (
            (end is not null && end <= current)
            || (
                sessionStatuses.Length > 0
                && sessionStatuses.All(status => status is "finished" or "completed" or "final")
            )
        )
        {
            return "completed";
        }

        return "upcoming";
    }

    private static DateOnly? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : null;

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static DateTimeOffset? InclusiveEndBoundary(string? value)
    {
        var parsed = ParseDateTime(value);
        return parsed is not null && parsed.Value.TimeOfDay == TimeSpan.Zero
            ? parsed.Value.AddDays(1)
            : parsed;
    }
}
