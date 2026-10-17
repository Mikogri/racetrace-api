using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Metadata;
using RaceTrace.Api.Infrastructure.Supabase;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class DomainQueryService
{
    private const int ResultsPageSize = 1000;
    private const int MaximumConcurrentResultPageQueries = 3;
    private const int ResultFilterBatchSize = 300;

    private readonly ISupabaseRestClient _supabase;
    private readonly DomainMetadataQueries _metadataQueries;
    private readonly RiderTeamHistoryQueries _teamHistory;
    private readonly IMemoryCache? _packageCache;
    private readonly DataRevisionService? _revisions;
    private readonly Dictionary<JsonObject, IReadOnlyList<PacePrecomputedSessionData>> _preparedPackages = new(ReferenceEqualityComparer.Instance);

    public const string ResultSelect = "*,riders(*),teams(*),result_points(points_awarded,points_system_id,point_system_stage_id,point_rule_id),sessions(*,conditions(*),round_categories(*,rounds(*,tracks(*),competition_levels(*),conditions(*)),categories(*)))";

    public const string RankingResultSelect =
        "id,rider_id,session_id,race_time_ms,top_speed,position,is_classified,status," +
        "riders(*),teams(*)," +
        "sessions(id,round_id,category_id,name,session_type,session_date," +
        "round_categories(round_id,category_id," +
        "rounds(id,track_id,competition_level_id,is_season_final_round,season_year,start_date,competition_levels(id,code,name,priority)),categories(id,code)))";

    public const string SessionSelect = "*,round_categories(*,rounds(*,tracks(*),competition_levels(*),conditions(*)),categories(*))";

    private readonly RaceTrace.Api.Configuration.ReadOptimizationOptions _readOptions;
    private readonly ILogger<DomainQueryService>? _logger;

    public DomainQueryService(ISupabaseRestClient supabase, DisplayMetadataService? metadata = null, IMemoryCache? packageCache = null,
        Microsoft.Extensions.Options.IOptions<RaceTrace.Api.Configuration.ReadOptimizationOptions>? readOptions = null,
        ILogger<DomainQueryService>? logger = null, DataRevisionService? revisions = null)
    {
        _supabase = supabase;
        _metadataQueries = new DomainMetadataQueries(supabase, metadata ?? new DisplayMetadataService(supabase));
        _teamHistory = new RiderTeamHistoryQueries(supabase);
        _packageCache = packageCache;
        _readOptions = readOptions?.Value ?? new();
        _logger = logger;
        _revisions = revisions;
    }

    public Task<RiderMetricsContext> ParseMetricsContextAsync(
        IQueryCollection query,
        QueryValidationService validator,
        CancellationToken cancellationToken,
        string? forcedStatus = null) =>
        MetricsQueryParser.ParseAsync(query, validator, this, cancellationToken, forcedStatus);

    public Task<string> ResolveCategoryCodeAsync(string value, CancellationToken cancellationToken)
        => _metadataQueries.ResolveCategoryCodeAsync(value, cancellationToken);

    public Task<string> ResolveSessionTypeCodeAsync(string value, CancellationToken cancellationToken)
        => _metadataQueries.ResolveSessionTypeCodeAsync(value, cancellationToken);

    public Task<string?> FindCategoryIdByCodeAsync(string? categoryCode, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        => _metadataQueries.FindCategoryIdByCodeAsync(categoryCode, role, cancellationToken);

    internal Task<string?> FindLegacyCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        => _metadataQueries.FindLegacyCompetitionClassIdByCodeAsync(code, role, cancellationToken);

    public Task<string?> FindCompetitionClassIdByCodeAsync(string? code, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        => _metadataQueries.FindCompetitionClassIdByCodeAsync(code, role, cancellationToken);

    public async Task<JsonObject?> FindByIdAsync(string table, string id, string select = "*", SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        return await _supabase.GetSingleAsync(
            new SupabaseQuery(table)
                .Select(select)
                .Filter("id", "eq", id),
            role,
            cancellationToken);
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
        return RestrictToCategories(ParseRankingDataBundle(response), filters);
    }

    internal async Task<RiderDetailDataBundle?> GetRankingPreaggregatesAsync(
        IReadOnlyList<JsonObject> sessions,
        string? riderQuery = null,
        string? nation = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default,
        bool includeSplits = true)
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

        var packages = new Dictionary<string, JsonObject>();
        var packageKind = includeSplits ? "inputs-full" : "inputs-metrics";
        var revision = _revisions is { Enabled: true } ? await _revisions.GetAsync(cancellationToken) : null;
        string PackageKey(string id) => $"ranking-package:{packageKind}:{role}:{id}" +
            (revision is null ? "" : ":" + revision.ForSessions([id]));
        foreach (var id in sessionIds)
        {
            if (_packageCache?.TryGetValue<JsonObject>(PackageKey(id), out var package) == true)
            {
                ApiTelemetry.Cache("memory", "hit");
                packages[id] = package!;
            }
            else ApiTelemetry.Cache("memory", _packageCache is null ? "bypass" : "miss");
        }
        var missingIds = sessionIds.Where(id => !packages.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            var response = await _supabase.RpcAsync(
                "get_ranking_input_packages",
                new JsonObject { ["p_session_ids"] = LongArray(missingIds), ["p_include_splits"] = includeSplits }, role, cancellationToken);
            foreach (var row in (response as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (row.String("session_id") is not { } id) continue;
                packages[id] = row;
                _packageCache?.Set(PackageKey(id), row, TimeSpan.FromSeconds(30));
            }
        }
        // Keep RPC ordering; the parser reads cached rows without creating a merged JSON copy.
        var cacheRows = packages.Values.OrderBy(row => PublicDtoMapping.Long(row.String("session_id"))).ToArray();
        if (cacheRows.Length == 0)
        {
            ApiTelemetry.Cache("preaggregation", "unavailable");
            ApiTelemetry.Cache("preaggregation", "fallback");
            return null;
        }

        var cachedSessionIds = cacheRows
            .Select(row => row.String("session_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sessionIds.Any(id => !cachedSessionIds.Contains(id)))
        {
            ApiTelemetry.Cache("preaggregation", "unavailable");
            ApiTelemetry.Cache("preaggregation", "fallback");
            return null;
        }

        ApiTelemetry.Cache("preaggregation", "hit");

        if (_readOptions.TypedRanking && !includeSplits)
        {
            var payloads = cacheRows.Select(row => row["payload"]).OfType<JsonObject>().ToArray();
            var read = CompactMetricsPackageParser.ParseRankingReadData(payloads);
            var pace = new List<PacePrecomputedSessionData>();
            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Packages are immutable and revision-specific. Scoped reuse never outlives the request.
                lock (_preparedPackages)
                {
                    if (!_preparedPackages.TryGetValue(payload, out var prepared))
                    {
                        prepared = CompactMetricsPackageParser.ParsePreparedRankingRead([payload]).PaceSessions;
                        _preparedPackages[payload] = prepared;
                    }
                    pace.AddRange(prepared);
                }
            }
            read = read with { Results = read.Results.Where(r => MatchesRankingRider(r.Rider, riderQuery, nation)).ToArray() };
            var selectedRiderIds = read.Results.Select(r => r.String("rider_id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new RiderDetailDataBundle(read.ToLegacy(), pace.Where(p => selectedRiderIds.Contains(p.Run.RiderId)).ToArray(), read);
        }

        var parsed = CompactMetricsPackageParser.ParseRiderDetailDataBundle(cacheRows
            .Select(row => row["payload"])
            .OfType<JsonObject>().ToArray());
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

    internal static bool MatchesRankingRider(JsonObject? rider, string? riderQuery, string? nation)
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

        var inputPayload = payload.DeepClone().AsObject();
        inputPayload["p_use_preaggregation"] = _readOptions.PreparedRiderDetails && string.IsNullOrWhiteSpace(filters.Status);
        try
        {
            var input = await _supabase.RpcAsync("get_rider_input_data", inputPayload, role, cancellationToken);
            if (input is JsonObject envelope && envelope.Bool("complete") == true &&
                envelope["payload"] is JsonObject data && CompactMetricsPackageParser.HasCompleteInputMeasurements(data))
            {
                ApiTelemetry.Cache("preaggregation", envelope.Bool("cached") == true ? "hit" : "bypass");
                return RestrictToCategories(ParseRiderDetailDataBundle(data), filters);
            }
            _logger?.LogWarning("Incomplete rider input package; using legacy read.");
        }
        catch (SupabaseApiException ex) when (ex.StatusCode == 404)
        {
            _logger?.LogWarning("Rider input RPC unavailable; using legacy read.");
        }
        ApiTelemetry.Cache("preaggregation", "fallback");

        var response = await _supabase.RpcAsync(
            "get_rider_detail_data",
            payload,
            role,
            cancellationToken);
        return RestrictToCategories(ParseRiderDetailDataBundle(response), filters);
    }

    internal async Task<JsonObject[]> GetPointsSummaryAsync(
        RiderMetricsContext filters, IReadOnlyList<JsonObject> sessions, CancellationToken cancellationToken)
    {
        var ids = sessions.Select(session => session.String("id")).OfType<string>().Distinct().ToArray();
        if (ids.Length == 0) return [];
        JsonNode? response = string.IsNullOrWhiteSpace(filters.Status)
            ? await _supabase.RpcAsync("get_ranking_points_summary",
                new JsonObject { ["p_session_ids"] = LongArray(ids) }, SupabaseRole.Anon, cancellationToken)
            : null;
        if (response is null || response is JsonObject cached && cached.Bool("complete") == false)
            response = await _supabase.RpcAsync("get_ranking_points_raw_summary",
                BuildMetricsRpcPayload(filters, ids),
                SupabaseRole.Anon, cancellationToken);
        if (response is not JsonObject package || package.Array("rows") is not { } rows)
            throw new ApiException(502, "POINTS_DATA_INVALID", "Supabase returned an invalid points summary.");
        if (package.Bool("complete") != true)
            throw new ApiException(502, "POINTS_DATA_INVALID", "Supabase returned an incomplete points summary.");
        return rows.OfType<JsonObject>().Select(row => new JsonObject
        {
            ["rider_id"] = row.String("rider_id"),
            ["rider_name"] = row.Object("rider")?.DisplayName(),
            ["points"] = row.Decimal("points") ?? 0m
        }).ToArray();
    }

    internal static RiderDetailDataBundle RestrictToCategories(
        RiderDetailDataBundle bundle,
        RiderMetricsContext filters)
    {
        if (bundle.ReadData is { } read)
        {
            var categories = RequestedCategoryCodes(filters);
            if (categories.Count == 0) return bundle;
            var sessions = read.Sessions.Where(s => categories.Contains(CanonicalCategoryCode(s.Object("categories")?.String("code")))).ToArray();
            var ids = sessions.Select(s => s.String("id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            read = read with { Sessions = sessions, Results = read.Results.Where(r => ids.Contains(r.String("session_id"))).ToArray(),
                FieldResults = read.FieldResults.Where(r => ids.Contains(r.String("session_id"))).ToArray() };
            return new RiderDetailDataBundle(read.ToLegacy(),bundle.PaceSessions.Where(p => ids.Contains(p.SessionId)).ToArray(),read);
        }
        var data = RestrictToCategories(bundle.Data, filters);
        if (ReferenceEquals(data, bundle.Data))
        {
            return bundle;
        }

        var sessionIds = data.Sessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RiderDetailDataBundle(
            data,
            bundle.PaceSessions
                .Where(session => sessionIds.Contains(session.SessionId))
                .ToArray());
    }

    internal static RankingDataBundle RestrictToCategories(
        RankingDataBundle bundle,
        RiderMetricsContext filters)
    {
        var requestedCategories = RequestedCategoryCodes(filters);
        if (requestedCategories.Count == 0)
        {
            return bundle;
        }

        var sessions = bundle.Sessions
            .Where(session => requestedCategories.Contains(CanonicalCategoryCode(
                session.Object("categories")?.String("code") ??
                session.Object("round_categories")?.Object("categories")?.String("code"))))
            .ToArray();
        var sessionIds = sessions
            .Select(session => session.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = bundle.Results
            .Where(result => sessionIds.Contains(ResultSessionId(result) ?? string.Empty))
            .ToArray();
        var fieldResults = bundle.FieldResults
            .Where(result => sessionIds.Contains(ResultSessionId(result) ?? string.Empty))
            .ToArray();
        var fieldResultIds = fieldResults
            .Concat(results)
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resultIds = results
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new RankingDataBundle(
            sessions,
            results,
            fieldResults,
            bundle.Splits
                .Where(split => fieldResultIds.Contains(split.String("result_id") ?? string.Empty))
                .ToArray(),
            bundle.PointsByResultId
                .Where(pair => resultIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> RequestedCategoryCodes(RiderMetricsContext filters)
    {
        var values = filters.CategoryCodes.Count > 0
            ? filters.CategoryCodes
            : string.IsNullOrWhiteSpace(filters.CategoryCode) ? [] : [filters.CategoryCode];
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(CanonicalCategoryCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CanonicalCategoryCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return PublicApiCodes.ToDatabaseCategory(value);
        }
        catch (ArgumentException)
        {
            return value.Trim().ToUpperInvariant();
        }
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

    public Task<IReadOnlyList<JsonObject>> GetRiderTeamHistoryAsync(
        IEnumerable<string> riderIds,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        SupabaseRole role = SupabaseRole.Anon,
        CancellationToken cancellationToken = default)
        => _teamHistory.GetRiderTeamHistoryAsync(riderIds, fromDate, toDate, role, cancellationToken);

    public Task DecorateMissingResultTeamsFromHistoryAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
        => _teamHistory.DecorateMissingResultTeamsFromHistoryAsync(results, cancellationToken);

    public Task DecorateRankingRiderTeamsAsync(
        IReadOnlyList<JsonObject> results,
        CancellationToken cancellationToken = default)
        => _teamHistory.DecorateRankingRiderTeamsAsync(results, cancellationToken);

    internal static JsonObject? FindTeamPeriod(
        IEnumerable<JsonObject> history,
        string riderId,
        DateOnly effectiveDate)
        => RiderTeamHistoryQueries.FindTeamPeriod(history, riderId, effectiveDate);

    internal static JsonObject? FindTeamPeriodOrNearest(
        IEnumerable<JsonObject> history,
        string riderId,
        DateOnly effectiveDate)
        => RiderTeamHistoryQueries.FindTeamPeriodOrNearest(history, riderId, effectiveDate);

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
            if (teamIds.Length > 0)
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

    public static RiderMetricsContext MetricsContextFromQuery(
        IQueryCollection query, QueryValidationService validator, string? forcedStatus = null) =>
        MetricsQueryParser.Parse(query, validator, forcedStatus);

    private static string[] Ids(IEnumerable<JsonObject> rows)
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
        => BuildTeamHistory(results.Select(RankingReadResult.FromLegacy));

    internal static JsonArray BuildTeamHistory(IEnumerable<RankingReadResult> results)
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

    internal static RankingDataBundle ParseRankingDataBundle(JsonNode? response) =>
        CompactMetricsPackageParser.ParseRankingDataBundle(response);

    internal static RiderDetailDataBundle ParseRiderDetailDataBundle(JsonNode? response) =>
        CompactMetricsPackageParser.ParseRiderDetailDataBundle(response);

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

    private static JsonValue? LongNode(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? JsonValue.Create(parsed)
            : null;

    internal static string? ResultDate(JsonObject result)
        => ResultDate(RankingReadResult.FromLegacy(result));

    private static string? ResultDate(RankingReadResult result)
    {
        var session = result.Object("sessions");
        return session?.String("session_date") ?? session?.Object("rounds")?.String("start_date");
    }
}

internal sealed record RiderDetailDataBundle(
    RankingDataBundle Data,
    IReadOnlyList<PacePrecomputedSessionData> PaceSessions,
    RankingReadData? ReadData = null);
