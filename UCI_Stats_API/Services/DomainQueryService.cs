using System.Globalization;
using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class DomainQueryService
{
    private const int ResultsPageSize = 1000;
    private const int MaximumConcurrentResultPageQueries = 3;
    private const int RiderFilterBatchSize = 100;
    private const int ResultFilterBatchSize = 300;
    private const int RankingPreaggregateBatchSize = 100;

    private readonly ISupabaseRestClient _supabase;

    public const string ResultSelect = "*,riders(*),teams(*),result_points(points_awarded,points_system_id,point_system_stage_id,point_rule_id),sessions(*,round_categories(*,rounds(*,tracks(*),competition_levels(*),conditions(*)),categories(*)))";

    public const string RankingResultSelect =
        "id,rider_id,session_id,race_time_ms,top_speed,position,is_classified,status," +
        "riders(*),teams(*)," +
        "sessions(id,round_id,category_id,name,session_type,session_date," +
        "round_categories(round_id,category_id," +
        "rounds(id,track_id,competition_level_id,is_season_final_round,season_year,start_date,competition_levels(id,code,name,priority)),categories(id,code)))";

    public const string SessionSelect = "*,round_categories(*,rounds(*,tracks(*),competition_levels(*),conditions(*)),categories(*))";

    public DomainQueryService(ISupabaseRestClient supabase)
    {
        _supabase = supabase;
    }

    public async Task<JsonObject?> FindByIdAsync(string table, string id, string select = "*", SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        return await _supabase.GetSingleAsync(
            new SupabaseQuery(table)
                .Select(select)
                .Filter("id", "eq", id),
            role,
            cancellationToken);
    }

    public async Task<string?> FindCategoryIdByCodeAsync(string? categoryCode, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            return null;
        }

        var normalizedCode = categoryCode.Trim();
        try
        {
            normalizedCode = PublicApiCodes.ToDatabaseCategory(normalizedCode);
        }
        catch (ArgumentException)
        {
            // Admin-managed categories outside the public DHI set keep their database code.
        }

        var category = await _supabase.GetSingleAsync(
            new SupabaseQuery("categories")
                .Select("id,code")
                .Filter("code", "eq", normalizedCode),
            role,
            cancellationToken);

        return category?.String("id");
    }

    internal async Task<string?> FindLegacyCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var competitionClass = await _supabase.GetSingleAsync(
            new SupabaseQuery("competition_ranks")
                .Select("id,code")
                .Filter("code", "eq", code),
            role,
            cancellationToken);

        return competitionClass?.String("id");
    }

    public async Task<string?> FindCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var competitionClass = await _supabase.GetSingleAsync(
            new SupabaseQuery("competition_levels")
                .Select("id,code,is_active")
                .Filter("code", "eq", code.Trim().ToLowerInvariant()),
            role,
            cancellationToken);

        return competitionClass?.String("id");
    }

    public async Task<IReadOnlyList<string>> FindTrackIdsByCountryAsync(string? countryCode, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        return await FindTrackIdsAsync(
            countryCode,
            venue: null,
            trackName: null,
            role,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindTrackIdsAsync(
        string? countryCode,
        string? venue,
        string? trackName,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode) &&
            string.IsNullOrWhiteSpace(venue) &&
            string.IsNullOrWhiteSpace(trackName))
        {
            return [];
        }

        var query = new SupabaseQuery("tracks").Select("id");
        query.Filter("country_code", "eq", countryCode?.Trim());
        query.Filter("venue", "ilike", venue?.Trim());
        query.Filter("name", "ilike", trackName?.Trim());
        var tracks = await _supabase.GetAsync(
            query,
            role: role,
            cancellationToken: cancellationToken);

        return Ids(tracks.Data);
    }

    private async Task<IReadOnlyList<string>> FindTrackIdsAsync(
        RiderMetricsContext filters,
        SupabaseRole role,
        CancellationToken cancellationToken)
    {
        var countries = filters.CountryCodes.Count > 0
            ? filters.CountryCodes
            : string.IsNullOrWhiteSpace(filters.CountryCode) ? [] : [filters.CountryCode];
        var venues = filters.VenueNames.Count > 0
            ? filters.VenueNames
            : string.IsNullOrWhiteSpace(filters.Venue) ? [] : [filters.Venue];

        if (countries.Count == 0 && venues.Count == 0 && string.IsNullOrWhiteSpace(filters.TrackName))
        {
            return [];
        }

        var query = new SupabaseQuery("tracks").Select("id");
        if (countries.Count == 1)
        {
            query.Filter("country_code", "eq", countries[0].ToUpperInvariant());
        }
        else if (countries.Count > 1)
        {
            query.In("country_code", countries.Select(value => value.ToUpperInvariant()));
        }

        if (venues.Count == 1)
        {
            query.Filter("venue", "ilike", SanitizeSearch(venues[0]));
        }
        else if (venues.Count > 1)
        {
            query.Or(string.Join(",", venues.Select(venue => $"venue.ilike.{SanitizeSearch(venue)}")));
        }

        query.Filter("name", "ilike", filters.TrackName?.Trim());
        var tracks = await _supabase.GetAsync(query, role: role, cancellationToken: cancellationToken);
        return Ids(tracks.Data);
    }

    private async Task<IReadOnlyList<string>> FindCompetitionClassIdsAsync(
        IReadOnlyList<string> codes,
        SupabaseRole role,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("competition_levels")
                .Select("id")
                .In("code", codes.Select(code => code.ToLowerInvariant())),
            role: role,
            cancellationToken: cancellationToken);
        return Ids(response.Data);
    }

    private async Task<IReadOnlyList<string>> FindConditionIdsAsync(
        IReadOnlyList<string> names,
        SupabaseRole role,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("conditions")
                .Select("id")
                .Or(string.Join(",", names.Select(name => $"name.ilike.{SanitizeSearch(name)}"))),
            role: role,
            cancellationToken: cancellationToken);
        return Ids(response.Data);
    }

    private async Task<IReadOnlyList<string>> FindCategoryIdsAsync(
        IReadOnlyList<string> codes,
        SupabaseRole role,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var response = await _supabase.GetAsync(
            new SupabaseQuery("categories")
                .Select("id")
                .In("code", codes),
            role: role,
            cancellationToken: cancellationToken);
        return Ids(response.Data);
    }

    public async Task<IReadOnlyList<string>> ResolveRoundIdsAsync(RiderMetricsContext filters, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        var query = new SupabaseQuery("rounds").Select("id");
        var hasRoundFilter = false;

        if (!string.IsNullOrWhiteSpace(filters.RoundId))
        {
            query.Filter("id", "eq", filters.RoundId);
            hasRoundFilter = true;
        }

        if (filters.SeasonYears.Count > 0)
        {
            query.In("season_year", filters.SeasonYears.Select(year => year.ToString(CultureInfo.InvariantCulture)));
            hasRoundFilter = true;
        }
        else if (filters.SeasonYear is not null)
        {
            query.Filter("season_year", "eq", filters.SeasonYear.Value);
            hasRoundFilter = true;
        }

        if (filters.TrackIds.Count > 0)
        {
            query.In("track_id", filters.TrackIds);
            hasRoundFilter = true;
        }
        else if (!string.IsNullOrWhiteSpace(filters.TrackId))
        {
            query.Filter("track_id", "eq", filters.TrackId);
            hasRoundFilter = true;
        }

        if (filters.CountryCodes.Count > 0 ||
            filters.VenueNames.Count > 0 ||
            !string.IsNullOrWhiteSpace(filters.CountryCode) ||
            !string.IsNullOrWhiteSpace(filters.Venue) ||
            !string.IsNullOrWhiteSpace(filters.TrackName))
        {
            var trackIds = await FindTrackIdsAsync(filters, role, cancellationToken);
            if (trackIds.Count == 0)
            {
                return [];
            }

            query.In("track_id", trackIds);
            hasRoundFilter = true;
        }

        if (filters.CompetitionClassIds.Count > 0)
        {
            query.In("competition_level_id", filters.CompetitionClassIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            hasRoundFilter = true;
        }
        else if (filters.CompetitionClassCodes.Count > 0)
        {
            var classIds = await FindCompetitionClassIdsAsync(filters.CompetitionClassCodes, role, cancellationToken);
            if (classIds.Count == 0)
            {
                return [];
            }

            query.In("competition_level_id", classIds);
            hasRoundFilter = true;
        }
        else if (!string.IsNullOrWhiteSpace(filters.CompetitionClassCode))
        {
            var classId = await FindCompetitionClassIdByCodeAsync(filters.CompetitionClassCode, role, cancellationToken);
            if (classId is null)
            {
                return [];
            }

            query.Filter("competition_level_id", "eq", classId);
            hasRoundFilter = true;
        }

        if (filters.ConditionIds.Count > 0)
        {
            query.In("condition_id", filters.ConditionIds.Select(conditionId => conditionId.ToString(CultureInfo.InvariantCulture)));
            hasRoundFilter = true;
        }
        else if (filters.ConditionNames.Count > 0)
        {
            var conditionIds = await FindConditionIdsAsync(filters.ConditionNames, role, cancellationToken);
            if (conditionIds.Count == 0)
            {
                return [];
            }

            query.In("condition_id", conditionIds);
            hasRoundFilter = true;
        }

        if (filters.FromDate is not null)
        {
            query.Filter("start_date", "gte", filters.FromDate.Value);
            hasRoundFilter = true;
        }

        if (filters.ToDate is not null)
        {
            query.Filter("start_date", "lte", filters.ToDate.Value);
            hasRoundFilter = true;
        }

        if (!hasRoundFilter)
        {
            return [];
        }

        var rounds = await _supabase.GetAsync(query, role: role, cancellationToken: cancellationToken);
        return Ids(rounds.Data);
    }

    public async Task<IReadOnlyList<string>> ResolveSessionIdsAsync(RiderMetricsContext filters, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        return Ids(await GetMatchingSessionsAsync(filters, role, cancellationToken));
    }

    public async Task<RankingDataBundle> GetRankingDataAsync(
        RiderMetricsContext filters,
        string? riderQuery = null,
        string? nation = null,
        string? riderId = null,
        IEnumerable<string>? resolvedSessionIds = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildMetricsRpcPayload(filters, resolvedSessionIds);
        payload["p_rider_query"] = riderQuery;
        payload["p_nation"] = nation;
        payload["p_rider_id"] = LongNode(riderId);

        var response = await _supabase.RpcAsync(
            "get_ranking_data",
            payload,
            role,
            cancellationToken);
        return ParseRankingDataBundle(response);
    }

    internal async Task<RiderDetailDataBundle?> GetRankingPreaggregatesAsync(
        IReadOnlyList<JsonObject> sessions,
        string? riderQuery = null,
        string? nation = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var sessionIds = sessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sessionIds.Length == 0)
        {
            return new RiderDetailDataBundle(
                new RankingDataBundle([], [], [], [], new Dictionary<string, decimal>()),
                []);
        }

        var cacheRows = new List<JsonObject>(sessionIds.Length);
        foreach (var batch in sessionIds.Chunk(RankingPreaggregateBatchSize))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("ranking_session_preaggregates")
                    .Select("session_id,payload")
                    .In("session_id", batch),
                role: role,
                cancellationToken: cancellationToken);
            cacheRows.AddRange(response.Data);
        }

        var cachedSessionIds = cacheRows
            .Select(row => row.String("session_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sessionIds.Any(id => !cachedSessionIds.Contains(id)))
        {
            return null;
        }

        var merged = MergeCompactPackages(cacheRows
            .Select(row => row["payload"])
            .OfType<JsonObject>());
        var parsed = ParseRiderDetailDataBundle(merged);
        if (string.IsNullOrWhiteSpace(riderQuery) && string.IsNullOrWhiteSpace(nation))
        {
            return parsed;
        }

        var filteredResults = parsed.Data.Results
            .Where(result => MatchesRankingRider(result.Object("riders"), riderQuery, nation))
            .ToArray();
        var riderIds = filteredResults
            .Select(result => result.String("rider_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RiderDetailDataBundle(
            parsed.Data with { Results = filteredResults },
            parsed.PaceSessions
                .Where(session => riderIds.Contains(session.Run.RiderId))
                .ToArray());
    }

    private static JsonObject MergeCompactPackages(IEnumerable<JsonObject> packages)
    {
        var merged = new JsonObject { ["v"] = 2 };
        foreach (var property in new[] { "s", "r", "f", "x", "d", "t", "a", "q", "g", "h" })
        {
            merged[property] = new JsonArray();
        }

        foreach (var package in packages)
        {
            foreach (var property in new[] { "s", "r", "f", "x", "d", "t", "a", "q", "g", "h" })
            {
                var target = merged[property]!.AsArray();
                foreach (var item in package[property]?.AsArray() ?? [])
                {
                    target.Add(item?.DeepClone());
                }
            }
        }

        return merged;
    }

    private static bool MatchesRankingRider(JsonObject? rider, string? riderQuery, string? nation)
    {
        if (rider is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(nation) &&
            !string.Equals(rider.String("nation"), nation.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(riderQuery))
        {
            return true;
        }

        var query = riderQuery.Trim();
        return new[]
            {
                rider.String("first_name"), rider.String("last_name"), rider.String("uci_id"),
                rider.String("uci_code"), rider.DisplayName()
            }
            .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    internal async Task<RiderDetailDataBundle> GetRiderDetailDataAsync(
        RiderMetricsContext filters,
        string riderId,
        IEnumerable<string>? resolvedSessionIds = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildMetricsRpcPayload(filters, resolvedSessionIds);
        payload["p_rider_id"] = LongNode(riderId);

        var response = await _supabase.RpcAsync(
            "get_rider_detail_data",
            payload,
            role,
            cancellationToken);
        return ParseRiderDetailDataBundle(response);
    }

    private static JsonObject BuildMetricsRpcPayload(
        RiderMetricsContext filters,
        IEnumerable<string>? resolvedSessionIds)
    {
        var seasonYears = filters.SeasonYears.Count > 0
            ? filters.SeasonYears
            : filters.SeasonYear is { } seasonYear ? [seasonYear] : [];
        var trackIds = filters.TrackIds.Count > 0
            ? filters.TrackIds
            : string.IsNullOrWhiteSpace(filters.TrackId) ? [] : [filters.TrackId];
        var countries = filters.CountryCodes.Count > 0
            ? filters.CountryCodes
            : string.IsNullOrWhiteSpace(filters.CountryCode) ? [] : [filters.CountryCode];
        var venues = filters.VenueNames.Count > 0
            ? filters.VenueNames
            : string.IsNullOrWhiteSpace(filters.Venue) ? [] : [filters.Venue];
        var conditionIds = filters.ConditionIds.Count > 0
            ? filters.ConditionIds
            : filters.ConditionId is { } conditionId ? [conditionId] : [];
        var classCodes = filters.CompetitionClassCodes.Count > 0
            ? filters.CompetitionClassCodes
            : string.IsNullOrWhiteSpace(filters.CompetitionClassCode) ? [] : [filters.CompetitionClassCode];
        var categoryCodes = filters.CategoryCodes.Count > 0
            ? filters.CategoryCodes
            : string.IsNullOrWhiteSpace(filters.CategoryCode) ? [] : [filters.CategoryCode];
        var sessionTypes = filters.SessionTypes.Count > 0
            ? filters.SessionTypes
            : string.IsNullOrWhiteSpace(filters.SessionType) ? [] : [NormalizeSessionType(filters.SessionType)];
        var payload = new JsonObject
        {
            ["p_season_years"] = IntArray(seasonYears),
            ["p_round_id"] = LongNode(filters.RoundId),
            ["p_track_ids"] = LongArray(trackIds),
            ["p_country_codes"] = StringArray(countries),
            ["p_venue_names"] = StringArray(venues),
            ["p_track_name"] = filters.TrackName,
            ["p_condition_ids"] = IntArray(conditionIds),
            ["p_condition_names"] = StringArray(filters.ConditionNames),
            ["p_competition_class_ids"] = IntArray(filters.CompetitionClassIds),
            ["p_competition_class_codes"] = StringArray(classCodes),
            ["p_category_codes"] = StringArray(categoryCodes),
            ["p_session_types"] = StringArray(sessionTypes),
            ["p_date_from"] = filters.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["p_date_to"] = filters.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["p_status"] = filters.Status,
            ["p_session_ids"] = LongArray(resolvedSessionIds ?? [])
        };
        return payload;
    }

    public async Task<IReadOnlyList<JsonObject>> GetMatchingSessionsAsync(
        RiderMetricsContext filters,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var query = new SupabaseQuery("sessions").Select(SessionSelect);
        var roundIds = await ResolveRoundIdsAsync(filters, role, cancellationToken);
        var hasRoundFilters = HasRoundFilters(filters);

        if (hasRoundFilters && roundIds.Count == 0)
        {
            return [];
        }

        if (roundIds.Count > 0)
        {
            query.In("round_id", roundIds);
        }

        IReadOnlyList<string> categoryIds = [];
        if (filters.CategoryCodes.Count > 0)
        {
            categoryIds = await FindCategoryIdsAsync(filters.CategoryCodes, role, cancellationToken);
            if (categoryIds.Count == 0)
            {
                return [];
            }
        }
        else if (!string.IsNullOrWhiteSpace(filters.CategoryCode))
        {
            var categoryId = await FindCategoryIdByCodeAsync(filters.CategoryCode, role, cancellationToken);
            if (categoryId is null)
            {
                return [];
            }

            categoryIds = [categoryId];
        }

        if (categoryIds.Count > 0)
        {
            query.In("category_id", categoryIds);
        }

        var sessionTypes = filters.SessionTypes.Count > 0
            ? filters.SessionTypes
            : string.IsNullOrWhiteSpace(filters.SessionType) ? [] : [NormalizeSessionType(filters.SessionType)];
        if (sessionTypes.Count > 0)
        {
            query.In("session_type", sessionTypes);
        }

        var sessions = await _supabase.GetAsync(query, role: role, cancellationToken: cancellationToken);
        NormalizeSessionEmbeds(sessions.Data);
        return sessions.Data;
    }

    public async Task<IReadOnlyList<JsonObject>> GetMatchingResultsAsync(
        RiderMetricsContext filters,
        string? riderId = null,
        string? riderQuery = null,
        string? nation = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default,
        IReadOnlyList<JsonObject>? resolvedSessions = null,
        string? select = null)
    {
        var sessionIds = resolvedSessions is null
            ? await ResolveSessionIdsAsync(filters, role, cancellationToken)
            : Ids(resolvedSessions);
        var hasSessionFilters = HasSessionFilters(filters);
        if (hasSessionFilters && sessionIds.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> nationRiderIds = [];
        if (!string.IsNullOrWhiteSpace(nation))
        {
            nationRiderIds = await FindRiderIdsAsync(riderQuery: null, nation: nation, role: role, cancellationToken: cancellationToken);
            if (nationRiderIds.Count == 0)
            {
                return [];
            }
        }

        IReadOnlyList<string> matchingRiderIds = [];
        if (!string.IsNullOrWhiteSpace(riderQuery))
        {
            matchingRiderIds = await FindRiderIdsAsync(riderQuery, null, role, cancellationToken);
            if (matchingRiderIds.Count == 0)
            {
                return [];
            }
        }

        SupabaseQuery ResultsQuery(int offset)
        {
            var query = new SupabaseQuery("results")
                .Select(select ?? ResultSelect);

            if (sessionIds.Count > 0)
            {
                query.In("session_id", sessionIds);
            }

            query.Filter("rider_id", "eq", riderId);
            query.Filter("status", "eq", filters.Status);
            if (nationRiderIds.Count > 0)
            {
                query.In("rider_id", nationRiderIds);
            }

            if (matchingRiderIds.Count > 0)
            {
                query.In("rider_id", matchingRiderIds);
            }

            query
                .Order("id", descending: false)
                .Limit(ResultsPageSize)
                .Offset(offset);

            return query;
        }

        var firstPage = await _supabase.GetAsync(
            ResultsQuery(0),
            count: true,
            role: role,
            cancellationToken: cancellationToken);
        var rows = new List<JsonObject>(firstPage.Total ?? firstPage.Data.Count);
        rows.AddRange(firstPage.Data);

        if (firstPage.Total is { } total && rows.Count < total)
        {
            var offsets = Enumerable
                .Range(1, (int)Math.Ceiling(total / (double)ResultsPageSize) - 1)
                .Select(pageIndex => pageIndex * ResultsPageSize)
                .ToArray();
            var pages = new IReadOnlyList<JsonObject>[offsets.Length];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, offsets.Length),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaximumConcurrentResultPageQueries,
                    CancellationToken = cancellationToken
                },
                async (pageIndex, token) =>
                {
                    var page = await _supabase.GetAsync(
                        ResultsQuery(offsets[pageIndex]),
                        role: role,
                        cancellationToken: token);
                    pages[pageIndex] = page.Data;
                });

            foreach (var page in pages)
            {
                rows.AddRange(page);
            }
        }
        else if (!firstPage.Total.HasValue && firstPage.Data.Count == ResultsPageSize)
        {
            var offset = ResultsPageSize;
            while (true)
            {
                var page = await _supabase.GetAsync(
                    ResultsQuery(offset),
                    role: role,
                    cancellationToken: cancellationToken);
                rows.AddRange(page.Data);
                if (page.Data.Count < ResultsPageSize)
                {
                    break;
                }

                offset += page.Data.Count;
            }
        }

        NormalizeResultEmbeds(rows);
        return rows;
    }

    public async Task<IReadOnlyList<JsonObject>> GetRiderTeamHistoryAsync(
        IEnumerable<string> riderIds,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var ids = riderIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = new List<JsonObject>();
        foreach (var batch in ids.Chunk(RiderFilterBatchSize))
        {
            var query = new SupabaseQuery("rider_team_history")
                .Select(
                    "id,rider_id,team_id,valid_from,valid_to,source_round_id," +
                    "teams(id,code,name),rounds(id,season_year,round_number,start_date)")
                .In("rider_id", batch)
                .Filter("valid_from", "lte", toDate)
                .Order("valid_from", descending: true)
                .Limit(ResultsPageSize);
            if (fromDate.HasValue)
            {
                query.Or($"valid_to.is.null,valid_to.gte.{fromDate:yyyy-MM-dd}");
            }

            var response = await _supabase.GetAsync(
                query,
                role: role,
                cancellationToken: cancellationToken);
            rows.AddRange(response.Data);
        }

        return rows;
    }

    public async Task DecorateMissingResultTeamsFromHistoryAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
    {
        var missing = results
            .Where(result =>
                result.Object("teams") is null &&
                string.IsNullOrWhiteSpace(result.String("team_id")) &&
                !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var dated = missing
            .Select(result => (Result: result, Date: ResultDateOnly(result)))
            .Where(item => item.Date.HasValue)
            .ToArray();
        if (dated.Length == 0)
        {
            return;
        }

        var history = await GetRiderTeamHistoryAsync(
            dated.Select(item => item.Result.String("rider_id")!),
            dated.Min(item => item.Date),
            dated.Max(item => item.Date),
            cancellationToken: cancellationToken);
        foreach (var item in dated)
        {
            var riderId = item.Result.String("rider_id")!;
            var period = FindTeamPeriod(history, riderId, item.Date!.Value);
            if (period?.Object("teams") is not { } team)
            {
                continue;
            }

            item.Result["team_id"] = team["id"]?.DeepClone();
            item.Result["teams"] = team.DeepClone();
        }
    }

    public async Task DecorateRankingRiderTeamsAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
    {
        var latestDates = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("rider_id")))
            .Select(result => new
            {
                RiderId = result.String("rider_id")!,
                Date = ResultDateOnly(result)
            })
            .Where(item => item.Date.HasValue)
            .GroupBy(item => item.RiderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => item.Date!.Value),
                StringComparer.OrdinalIgnoreCase);
        if (latestDates.Count == 0)
        {
            return;
        }

        var history = await GetRiderTeamHistoryAsync(
            latestDates.Keys,
            latestDates.Values.Min(),
            latestDates.Values.Max(),
            cancellationToken: cancellationToken);
        foreach (var result in results)
        {
            var riderId = result.String("rider_id");
            if (string.IsNullOrWhiteSpace(riderId) ||
                result.Object("riders") is not { } rider ||
                !latestDates.TryGetValue(riderId, out var effectiveDate))
            {
                continue;
            }

            if (result.Object("teams") is null &&
                string.IsNullOrWhiteSpace(result.String("team_id")) &&
                ResultDateOnly(result) is { } resultDate &&
                FindTeamPeriod(history, riderId, resultDate) is { } resultPeriod &&
                resultPeriod.Object("teams") is { } resultTeam)
            {
                result["team_id"] = resultTeam["id"]?.DeepClone();
                result["teams"] = resultTeam.DeepClone();
            }

            var team = FindTeamPeriod(history, riderId, effectiveDate)?
                .Object("teams");
            rider["teams"] = team is null
                ? new JsonArray()
                : new JsonArray(team.DeepClone());
        }
    }

    internal static JsonObject? FindTeamPeriod(
        IEnumerable<JsonObject> history,
        string riderId,
        DateOnly effectiveDate)
    {
        return history
            .Where(row => string.Equals(
                row.String("rider_id"),
                riderId,
                StringComparison.OrdinalIgnoreCase))
            .Where(row =>
                PublicDtoMapping.Date(row.String("valid_from")) is { } validFrom &&
                validFrom <= effectiveDate &&
                (PublicDtoMapping.Date(row.String("valid_to")) is not { } validTo ||
                    validTo >= effectiveDate))
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static DateOnly? ResultDateOnly(JsonObject result) =>
        PublicDtoMapping.Date(ResultDate(result));

    public async Task<IReadOnlyDictionary<string, decimal>> GetPointsByResultIdAsync(IEnumerable<string> resultIds, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        var ids = resultIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, decimal>();
        }

        var batches = ids.Chunk(ResultFilterBatchSize).ToArray();
        var pages = new IReadOnlyList<JsonObject>[batches.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, batches.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumConcurrentResultPageQueries,
                CancellationToken = cancellationToken
            },
            async (batchIndex, token) =>
            {
                var rows = new List<JsonObject>();
                var offset = 0;
                while (true)
                {
                    var points = await _supabase.GetAsync(
                        new SupabaseQuery("result_points")
                            .Select("result_id,points_awarded")
                            .In("result_id", batches[batchIndex])
                            .Limit(ResultsPageSize)
                            .Offset(offset),
                        role: role,
                        cancellationToken: token);
                    rows.AddRange(points.Data);
                    if (points.Data.Count < ResultsPageSize)
                    {
                        break;
                    }

                    offset += ResultsPageSize;
                }

                pages[batchIndex] = rows;
            });

        return pages
            .SelectMany(page => page)
            .GroupBy(row => row.String("result_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.Decimal("points_awarded") ?? 0m),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task DecorateGapMsAsync(IReadOnlyList<JsonObject> results, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        var sessionIds = results
            .Select(ResultSessionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sessionIds.Length == 0)
        {
            return;
        }

        var bestTimesBySessionId = await GetBestRaceTimeBySessionIdAsync(sessionIds, role, cancellationToken);
        foreach (var result in results)
        {
            var sessionId = ResultSessionId(result);
            var raceTime = result.Int("race_time_ms");
            if (string.IsNullOrWhiteSpace(sessionId) ||
                raceTime is not > 0 ||
                !bestTimesBySessionId.TryGetValue(sessionId, out var bestTime) ||
                !IsClassifiedResult(result))
            {
                result["gap_ms"] = null;
                continue;
            }

            var gap = raceTime.Value - bestTime;
            result["gap_ms"] = gap >= 0 ? gap : null;
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> GetBestRaceTimeBySessionIdAsync(IReadOnlyList<string> sessionIds, SupabaseRole role, CancellationToken cancellationToken)
    {
        var response = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("session_id,race_time_ms,is_classified,position,status")
                .In("session_id", sessionIds),
            role: role,
            cancellationToken: cancellationToken);

        return response.Data
            .Where(row => row.Int("race_time_ms") is > 0 && IsClassifiedResult(row))
            .GroupBy(row => row.String("session_id") ?? string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group.Min(row => row.Int("race_time_ms")!.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> FindRiderIdsAsync(
        string? riderQuery,
        string? nation,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
    {
        var query = new SupabaseQuery("riders").Select("id");
        var safe = string.Empty;

        if (!string.IsNullOrWhiteSpace(riderQuery))
        {
            safe = SanitizeSearch(riderQuery);
            var expressions = new List<string>
            {
                $"first_name.ilike.*{safe}*",
                $"last_name.ilike.*{safe}*",
                $"uci_code.ilike.*{safe}*",
                $"nation.ilike.*{safe}*"
            };

            if (long.TryParse(safe, out var numericQuery))
            {
                expressions.Add($"id.eq.{numericQuery}");
                expressions.Add($"uci_id.eq.{numericQuery}");
            }

            query.Or(string.Join(",", expressions));
        }

        if (!string.IsNullOrWhiteSpace(nation))
        {
            query.Filter("nation", "eq", nation);
        }

        var riders = await _supabase.GetAsync(query, role: role, cancellationToken: cancellationToken);
        var riderIds = Ids(riders.Data).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(safe))
        {
            var nameParts = safe.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (nameParts.Length >= 2)
            {
                var firstLast = await _supabase.GetAsync(
                    new SupabaseQuery("riders")
                        .Select("id")
                        .Filter("first_name", "ilike", $"*{nameParts[0]}*")
                        .Filter("last_name", "ilike", $"*{nameParts[^1]}*"),
                    role: role,
                    cancellationToken: cancellationToken);
                var lastFirst = await _supabase.GetAsync(
                    new SupabaseQuery("riders")
                        .Select("id")
                        .Filter("first_name", "ilike", $"*{nameParts[^1]}*")
                        .Filter("last_name", "ilike", $"*{nameParts[0]}*"),
                    role: role,
                    cancellationToken: cancellationToken);
                riderIds.UnionWith(Ids(firstLast.Data));
                riderIds.UnionWith(Ids(lastFirst.Data));
            }

            var teams = await _supabase.GetAsync(
                new SupabaseQuery("teams")
                    .Select("id")
                    .Or($"name.ilike.*{safe}*,code.ilike.*{safe}*"),
                role: role,
                cancellationToken: cancellationToken);
            var teamIds = Ids(teams.Data);
            if (teamIds.Count > 0)
            {
                var teamResults = await _supabase.GetAsync(
                    new SupabaseQuery("results")
                        .Select("rider_id")
                        .In("team_id", teamIds),
                    role: role,
                    cancellationToken: cancellationToken);
                riderIds.UnionWith(teamResults.Data
                    .Select(result => result.String("rider_id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!));
            }
        }

        if (!string.IsNullOrWhiteSpace(nation))
        {
            var nationRiders = await _supabase.GetAsync(
                new SupabaseQuery("riders")
                    .Select("id")
                    .Filter("nation", "eq", nation.ToUpperInvariant()),
                role: role,
                cancellationToken: cancellationToken);
            riderIds.IntersectWith(Ids(nationRiders.Data));
        }

        return riderIds.ToArray();
    }

    private static string SanitizeSearch(string value)
    {
        return new string(value
            .Where(character => character is not ('*' or ',' or '(' or ')'))
            .ToArray())
            .Trim();
    }

    public static RiderMetricsContext MetricsContextFromQuery(IQueryCollection query, QueryValidationService validator, string? forcedStatus = null)
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

    private static IReadOnlyList<string> Ids(IEnumerable<JsonObject> rows)
    {
        return rows
            .Select(row => row.String("id"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasRoundFilters(RiderMetricsContext filters)
    {
        return filters.SeasonYears.Count > 0 ||
            filters.SeasonYear is not null ||
            filters.FromDate is not null ||
            filters.ToDate is not null ||
            !string.IsNullOrWhiteSpace(filters.RoundId) ||
            filters.TrackIds.Count > 0 ||
            !string.IsNullOrWhiteSpace(filters.TrackId) ||
            filters.ConditionIds.Count > 0 ||
            filters.ConditionNames.Count > 0 ||
            filters.CountryCodes.Count > 0 ||
            filters.VenueNames.Count > 0 ||
            !string.IsNullOrWhiteSpace(filters.CountryCode) ||
            !string.IsNullOrWhiteSpace(filters.Venue) ||
            !string.IsNullOrWhiteSpace(filters.TrackName) ||
            !string.IsNullOrWhiteSpace(filters.CompetitionClassCode) ||
            filters.CompetitionClassIds.Count > 0 ||
            filters.CompetitionClassCodes.Count > 0;
    }

    private static bool HasSessionFilters(RiderMetricsContext filters)
    {
        return HasRoundFilters(filters) ||
            !string.IsNullOrWhiteSpace(filters.CategoryCode) ||
            filters.CategoryCodes.Count > 0 ||
            !string.IsNullOrWhiteSpace(filters.SessionType) ||
            filters.SessionTypes.Count > 0;
    }

    private static string? ResultSessionId(JsonObject result)
    {
        return result.String("session_id") ?? result.Object("sessions")?.String("id");
    }

    private static bool IsClassifiedResult(JsonObject result)
    {
        var status = result.String("status");
        var position = result.Int("position");
        return (result.Bool("is_classified") == true || position is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeSessionType(string sessionType)
    {
        var normalized = sessionType.Trim().ToUpperInvariant();
        return normalized switch
        {
            "FINAL" or "FINALS" => "FINAL",
            "QUALIFICATION" or "QUALIFYING" => "QUALI",
            "SEMIFINAL" or "SEMI_FINAL" or "SEMI-FINAL" => "SEMI",
            _ => normalized
        };
    }

    public static bool IsFinalResult(JsonObject result)
    {
        var sessionType = result.Object("sessions")?.String("session_type");
        return !string.IsNullOrWhiteSpace(sessionType) &&
            string.Equals(NormalizeSessionType(sessionType), "FINAL", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFinalSession(JsonObject session)
    {
        var sessionType = session.String("session_type");
        return !string.IsNullOrWhiteSpace(sessionType) &&
            string.Equals(NormalizeSessionType(sessionType), "FINAL", StringComparison.OrdinalIgnoreCase);
    }

    public static void NormalizeResultEmbeds(IReadOnlyList<JsonObject> results)
    {
        foreach (var result in results)
        {
            var pointRows = result.Array("result_points")?.OfType<JsonObject>().ToArray() ?? [];
            result["awarded_points"] = pointRows.Sum(row => row.Decimal("points_awarded") ?? 0m);
            if (pointRows.Length == 1)
            {
                result["applied_point_system_id"] = pointRows[0]["points_system_id"]?.DeepClone();
                result["applied_point_system_stage_id"] = pointRows[0]["point_system_stage_id"]?.DeepClone();
                result["applied_point_rule_id"] = pointRows[0]["point_rule_id"]?.DeepClone();
            }

            if (result.Object("sessions") is { } session)
            {
                NormalizeSessionEmbed(session);
            }
        }
    }

    public static JsonArray BuildTeamHistory(IEnumerable<JsonObject> results)
    {
        var teams = results
            .Where(result => result.Object("teams") is not null)
            .GroupBy(
                result => result.Object("teams")!.String("id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var rows = group.ToArray();
                var team = rows[0].Object("teams")!.DeepClone().AsObject();
                var dates = rows
                    .Select(ResultDate)
                    .Where(date => !string.IsNullOrWhiteSpace(date))
                    .Select(date => date!)
                    .OrderBy(date => date, StringComparer.Ordinal)
                    .ToArray();

                team["results_count"] = rows.Length;
                team["first_result_date"] = dates.FirstOrDefault();
                team["last_result_date"] = dates.LastOrDefault();
                return team;
            })
            .OrderByDescending(team => team.String("last_result_date"), StringComparer.Ordinal)
            .ThenBy(team => team.String("name"), StringComparer.OrdinalIgnoreCase)
            .Select(team => (JsonNode)team)
            .ToArray();

        return new JsonArray(teams);
    }

    public static void NormalizeSessionEmbeds(IReadOnlyList<JsonObject> sessions)
    {
        foreach (var session in sessions)
        {
            NormalizeSessionEmbed(session);
        }
    }

    public static void NormalizeSessionEmbed(JsonObject session)
    {
        var roundCategory = session.Object("round_categories");
        if (roundCategory is null)
        {
            return;
        }

        if (roundCategory.Object("rounds") is { } round)
        {
            NormalizeRoundEmbed(round);
            session["rounds"] = round.DeepClone();
        }

        if (roundCategory.Object("categories") is { } category)
        {
            session["categories"] = category.DeepClone();
        }

        if (session.Object("conditions") is null && roundCategory.Object("rounds")?.Object("conditions") is { } condition)
        {
            session["conditions"] = condition.DeepClone();
        }
    }

    public static void NormalizeRoundEmbed(JsonObject round)
    {
        var competitionClassId = round.String("competition_level_id");
        if (round.Object("competition_levels") is not { } competitionClass)
        {
            round["competition_class_id"] = competitionClassId;
            round["competition_class"] = null;
            round["competition_class_priority"] = null;
        }
        else
        {
            round["competition_class_id"] = competitionClassId ?? competitionClass.String("id");
            round["competition_class"] = competitionClass.DeepClone();
            round["competition_class_priority"] = competitionClass.Int("priority");
        }

        round.Remove("competition_level_id");
        round.Remove("competition_levels");
        round.Remove("competition_rank_id");
        round.Remove("competition_ranks");
    }

    internal static RankingDataBundle ParseRankingDataBundle(JsonNode? response)
    {
        var root = response as JsonObject ??
            (response as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ??
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "RANKING_DATA_INVALID",
                "Supabase returned an invalid ranking data package.");

        var sessions = new List<JsonObject>();
        var sessionsById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(root, "s"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var competitionClass = new JsonObject
            {
                ["id"] = CompactClone(row, 12),
                ["code"] = CompactClone(row, 13),
                ["name"] = CompactClone(row, 14),
                ["priority"] = CompactClone(row, 9)
            };
            var condition = new JsonObject
            {
                ["id"] = CompactClone(row, 15),
                ["name"] = CompactClone(row, 16)
            };
            var track = new JsonObject
            {
                ["id"] = CompactClone(row, 6),
                ["name"] = CompactClone(row, 17),
                ["venue"] = CompactClone(row, 18),
                ["country_code"] = CompactClone(row, 19),
                ["length_m"] = CompactClone(row, 20),
                ["avg_slope"] = CompactClone(row, 21)
            };
            var round = new JsonObject
            {
                ["id"] = CompactClone(row, 1),
                ["round_number"] = CompactClone(row, 11),
                ["track_id"] = CompactClone(row, 6),
                ["season_year"] = CompactClone(row, 7),
                ["start_date"] = CompactClone(row, 8),
                ["competition_level_id"] = CompactClone(row, 12),
                ["competition_class_priority"] = CompactClone(row, 9),
                ["competition_levels"] = competitionClass,
                ["conditions"] = condition,
                ["tracks"] = track
            };
            var session = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["round_id"] = CompactClone(row, 1),
                ["category_id"] = CompactClone(row, 2),
                ["name"] = CompactClone(row, 3),
                ["session_type"] = CompactClone(row, 4),
                ["session_date"] = CompactClone(row, 5),
                ["categories"] = new JsonObject
                {
                    ["id"] = CompactClone(row, 2),
                    ["code"] = CompactClone(row, 10)
                },
                ["rounds"] = round
            };
            sessions.Add(session.DeepClone().AsObject());
            sessionsById[id] = session;
        }

        var ridersById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(root, "d"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rider = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["uci_id"] = CompactClone(row, 1),
                ["first_name"] = CompactClone(row, 2),
                ["last_name"] = CompactClone(row, 3),
                ["nation"] = CompactClone(row, 4),
                ["profile_image_url"] = CompactClone(row, 5),
                ["uci_code"] = CompactClone(row, 8),
                ["birth_date"] = CompactClone(row, 9),
                ["has_injury"] = CompactClone(row, 10),
                ["gender"] = CompactClone(row, 12)
            };
            var teamId = CompactString(row, 6);
            if (!string.IsNullOrWhiteSpace(teamId))
            {
                rider["teams"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = CompactClone(row, 6),
                        ["name"] = CompactClone(row, 7),
                        ["code"] = CompactClone(row, 11)
                    }
                };
            }
            else
            {
                rider["teams"] = new JsonArray();
            }

            ridersById[id] = rider;
        }

        var teamsById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(root, "t"))
        {
            var id = CompactString(row, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            teamsById[id] = new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["name"] = CompactClone(row, 1),
                ["code"] = CompactClone(row, 2)
            };
        }

        var results = new List<JsonObject>();
        var pointsByResultId = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CompactRows(root, "r"))
        {
            var resultId = CompactString(row, 0);
            var riderId = CompactString(row, 1);
            var sessionId = CompactString(row, 2);
            if (string.IsNullOrWhiteSpace(resultId) ||
                string.IsNullOrWhiteSpace(riderId) ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            ridersById.TryGetValue(riderId, out var rider);
            sessionsById.TryGetValue(sessionId, out var session);
            var currentTeam = rider?.Array("teams")?.OfType<JsonObject>().FirstOrDefault();
            var resultTeamId = CompactString(row, 9);
            JsonObject? team;
            if (string.IsNullOrWhiteSpace(resultTeamId))
            {
                team = currentTeam;
            }
            else if (teamsById.TryGetValue(resultTeamId, out var compactTeam))
            {
                team = compactTeam;
            }
            else if (!string.IsNullOrWhiteSpace(CompactString(row, 10)) ||
                !string.IsNullOrWhiteSpace(CompactString(row, 11)))
            {
                team = new JsonObject
                {
                    ["id"] = CompactClone(row, 9),
                    ["name"] = CompactClone(row, 10),
                    ["code"] = CompactClone(row, 11)
                };
            }
            else if (teamsById.Count == 0)
            {
                team = currentTeam;
            }
            else
            {
                team = new JsonObject
                {
                    ["id"] = CompactClone(row, 9),
                    ["name"] = CompactClone(row, 10),
                    ["code"] = CompactClone(row, 11)
                };
            }
            var awardedPoints = CompactDecimal(row, 8) ?? 0m;
            results.Add(new JsonObject
            {
                ["id"] = CompactClone(row, 0),
                ["rider_id"] = CompactClone(row, 1),
                ["session_id"] = CompactClone(row, 2),
                ["race_time_ms"] = CompactClone(row, 3),
                ["top_speed"] = CompactClone(row, 4),
                ["position"] = CompactClone(row, 5),
                ["is_classified"] = CompactClone(row, 6),
                ["status"] = CompactClone(row, 7),
                ["awarded_points"] = awardedPoints,
                ["riders"] = rider?.DeepClone(),
                ["teams"] = team?.DeepClone(),
                ["sessions"] = session?.DeepClone()
            });

            pointsByResultId[resultId] = awardedPoints;
        }

        var compactFieldRows = CompactRows(root, "f").ToArray();
        IReadOnlyList<JsonObject> fieldResults = compactFieldRows.Length == 0
            ? results
            : compactFieldRows
                .Select(row =>
                {
                    var sessionId = CompactString(row, 2);
                    sessionsById.TryGetValue(sessionId ?? string.Empty, out var session);
                    return new JsonObject
                    {
                        ["id"] = CompactClone(row, 0),
                        ["rider_id"] = CompactClone(row, 1),
                        ["session_id"] = CompactClone(row, 2),
                        ["race_time_ms"] = CompactClone(row, 3),
                        ["top_speed"] = CompactClone(row, 4),
                        ["position"] = CompactClone(row, 5),
                        ["is_classified"] = CompactClone(row, 6),
                        ["status"] = CompactClone(row, 7),
                        ["sessions"] = session?.DeepClone()
                    };
                })
                .ToArray();

        var splits = CompactRows(root, "x")
            .Select(row => new JsonObject
            {
                ["result_id"] = CompactClone(row, 0),
                ["split_index"] = CompactClone(row, 1),
                ["race_time"] = CompactClone(row, 2),
                ["speed"] = CompactClone(row, 3),
                ["split_rank"] = CompactClone(row, 4),
                ["sector_rank"] = CompactClone(row, 5)
            })
            .ToArray();

        return new RankingDataBundle(
            sessions,
            results,
            fieldResults,
            splits,
            pointsByResultId);
    }

    internal static RiderDetailDataBundle ParseRiderDetailDataBundle(JsonNode? response)
    {
        var root = response as JsonObject ??
            (response as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ??
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "RIDER_DETAIL_DATA_INVALID",
                "Supabase returned an invalid rider-detail data package.");
        var data = ParseRankingDataBundle(root);
        var resultsById = data.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(
                result => result.String("id")!,
                result => result,
                StringComparer.OrdinalIgnoreCase);
        var compactSessionStatistics = CompactRows(root, "h")
            .Where(row => !string.IsNullOrWhiteSpace(CompactString(row, 0)))
            .ToDictionary(
                row => CompactString(row, 0)!,
                row => row,
                StringComparer.OrdinalIgnoreCase);
        var compactSectorStatistics = CompactRows(root, "g")
            .Where(row =>
                !string.IsNullOrWhiteSpace(CompactString(row, 0)) &&
                CompactInt(row, 1).HasValue)
            .ToDictionary(
                row => $"{CompactString(row, 0)}\u001f{CompactInt(row, 1)}",
                row => row,
                StringComparer.OrdinalIgnoreCase);
        var hasCompactStatistics = compactSessionStatistics.Count > 0 ||
            compactSectorStatistics.Count > 0;
        var sectorRowsByResultId = CompactRows(root, "q")
            .Where(row => !string.IsNullOrWhiteSpace(CompactString(row, 0)))
            .GroupBy(row => CompactString(row, 0)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => CompactInt(row, 1) ?? int.MaxValue).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var sessions = new List<PacePrecomputedSessionData>();
        foreach (var aggregateRow in CompactRows(root, "a")
            .Where(row =>
                !string.IsNullOrWhiteSpace(CompactString(row, 0)) &&
                !string.IsNullOrWhiteSpace(CompactString(row, 1))))
        {
            var resultId = CompactString(aggregateRow, 0)!;
            if (!resultsById.TryGetValue(resultId, out var result) ||
                result.Object("sessions") is not { } session ||
                string.IsNullOrWhiteSpace(result.String("rider_id")))
            {
                continue;
            }

            var sectorRows = sectorRowsByResultId.GetValueOrDefault(
                resultId,
                Array.Empty<JsonArray>());
            var sectors = sectorRows
                .Select(row => new PacePrecomputedSector(
                    CompactInt(row, 1) ?? 0,
                    CompactDouble(row, 2),
                    CompactInt(row, 3),
                    hasCompactStatistics && compactSectorStatistics.TryGetValue(
                        $"{CompactString(aggregateRow, 1)}\u001f{CompactInt(row, 1)}",
                        out var compactStatistics)
                            ? ParseFieldSummary(compactStatistics, 2)!
                            : ParseFieldSummary(row, 4)!))
                .ToArray();
            var cumulativeStatistics = sectorRows
                .Select(row =>
                    hasCompactStatistics && compactSectorStatistics.TryGetValue(
                        $"{CompactString(aggregateRow, 1)}\u001f{CompactInt(row, 1)}",
                        out var compactStatistics)
                            ? ParseFieldSummary(compactStatistics, 9)
                            : ParseFieldSummary(row, 11))
                .ToArray();
            var allSectorTimes = sectors
                .OrderBy(sector => sector.SectorIndex)
                .Select(sector => sector.TimeSeconds)
                .ToArray();
            var lastRecordedSector = Array.FindLastIndex(
                allSectorTimes,
                time => time is > 0 && double.IsFinite(time.Value));
            var sectorTimes = lastRecordedSector < 0
                ? []
                : allSectorTimes.Take(lastRecordedSector + 1).ToArray();
            var raceTimeMs = result.Int("race_time_ms");
            var status = result.String("status");
            var isClassified =
                (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
                !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);

            compactSessionStatistics.TryGetValue(
                CompactString(aggregateRow, 1)!,
                out var sessionStatistics);
            sessions.Add(new PacePrecomputedSessionData(
                SessionId: CompactString(aggregateRow, 1)!,
                CourseKey: PaceService.BuildCourseKey(result),
                CategoryId: session.String("category_id") ?? session.Object("categories")?.String("id"),
                SessionType: PaceService.ParseSessionType(
                    session.String("session_type"),
                    session.String("name")),
                Run: new PaceRiderRun(
                    RiderId: result.String("rider_id")!,
                    SectorTimes: sectorTimes,
                    TopSpeed: result.Decimal("top_speed") is { } speed
                        ? (double)speed
                        : null,
                    FinishRank: result.Int("position"),
                    FinishTimeSeconds: raceTimeMs is > 0
                        ? raceTimeMs.Value / 1000.0
                        : null,
                    IsClassified: isClassified,
                    Status: status),
                ClassifiedFieldSize: CompactInt(aggregateRow, 2) ?? 0,
                Sectors: sectors,
                SpeedStatistics: sessionStatistics is null
                    ? ParseFieldSummary(aggregateRow, 4)
                    : ParseFieldSummary(sessionStatistics, 1),
                SpeedRank: CompactInt(aggregateRow, 3),
                FinishStatistics: sessionStatistics is null
                    ? ParseFieldSummary(aggregateRow, 11)
                    : ParseFieldSummary(sessionStatistics, 8),
                CumulativeStatistics: cumulativeStatistics,
                CompetitionClassPriority:
                    session.Object("rounds")?.Int("competition_class_priority")));
        }

        return new RiderDetailDataBundle(data, sessions);
    }

    private static PaceFieldSummary? ParseFieldSummary(JsonArray row, int offset)
    {
        var count = CompactInt(row, offset) ?? 0;
        var median = CompactDouble(row, offset + 1);
        var logMedian = CompactDouble(row, offset + 2);
        var logMadScale = CompactDouble(row, offset + 3);
        var linearMadScale = CompactDouble(row, offset + 4);
        if (count <= 0 ||
            !median.HasValue ||
            !logMedian.HasValue ||
            !logMadScale.HasValue ||
            !linearMadScale.HasValue)
        {
            return null;
        }

        return new PaceFieldSummary(
            count,
            median.Value,
            logMedian.Value,
            logMadScale.Value,
            linearMadScale.Value,
            CompactDouble(row, offset + 5),
            CompactDouble(row, offset + 6));
    }

    private static IEnumerable<JsonArray> CompactRows(JsonObject root, string property) =>
        root[property] is JsonArray rows
            ? rows.OfType<JsonArray>()
            : [];

    private static JsonNode? CompactClone(JsonArray row, int index) =>
        index < row.Count ? row[index]?.DeepClone() : null;

    private static string? CompactString(JsonArray row, int index) =>
        index < row.Count ? row[index]?.ToString() : null;

    private static decimal? CompactDecimal(JsonArray row, int index) =>
        decimal.TryParse(
            CompactString(row, index),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static double? CompactDouble(JsonArray row, int index) =>
        double.TryParse(
            CompactString(row, index),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static int? CompactInt(JsonArray row, int index) =>
        int.TryParse(
            CompactString(row, index),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static long? CompactLong(JsonArray row, int index) =>
        long.TryParse(
            CompactString(row, index),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static JsonArray StringArray(IEnumerable<string> values) =>
        new(values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => (JsonNode?)JsonValue.Create(value.Trim()))
            .ToArray());

    private static JsonArray IntArray(IEnumerable<int> values) =>
        new(values
            .Distinct()
            .Select(value => (JsonNode?)JsonValue.Create(value))
            .ToArray());

    private static JsonArray LongArray(IEnumerable<string> values) =>
        new(values
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? (long?)parsed
                : null)
            .Where(value => value.HasValue)
            .Select(value => (JsonNode?)JsonValue.Create(value!.Value))
            .ToArray());

    private static JsonNode? LongNode(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? JsonValue.Create(parsed)
            : null;

    internal static string? ResultDate(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("session_date") ?? session?.Object("rounds")?.String("start_date");
    }
}

internal sealed record RiderDetailDataBundle(
    RankingDataBundle Data,
    IReadOnlyList<PacePrecomputedSessionData> PaceSessions);
