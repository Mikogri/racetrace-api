using static RaceTrace.Api.Features.Imports.ChronoRace.ChronoRacePayloadMapper;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class ChronoRaceImportService
{
    private static readonly HashSet<string> PlannedRoundStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
		"scheduled",
		"confirmed",
		"live",
		"cancelled"
    };

    private readonly ISupabaseRestClient _supabase;
    private readonly RaceTrace.Api.Features.Imports.ChronoRace.ChronoRaceBatchStore _batches;
    private readonly DomainQueryService _domainQueries;
    private readonly IPointAwardService _pointAwards;
    private readonly RoundImageStorageService? _imageStorage;

    public ChronoRaceImportService(
        ISupabaseRestClient supabase,
        DomainQueryService domainQueries,
        IPointAwardService pointAwards,
        RoundImageStorageService? imageStorage = null)
    {
        _supabase = supabase;
        _batches = new(supabase);
        _domainQueries = domainQueries;
        _pointAwards = pointAwards;
        _imageStorage = imageStorage;
    }

    public async Task<PlannedRoundCreateResponse> CreatePlannedRoundAsync(
        PlannedRoundCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePlannedRound(request);

        var track = request.TrackId is > 0
            ? await _domainQueries.FindByIdAsync(
                "tracks",
                request.TrackId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "*",
                SupabaseRole.Service,
                cancellationToken)
            : await FindOrCreateTrackAsync(request.Track!, cancellationToken);
        if (track is null)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TRACK_NOT_FOUND",
                $"Track id '{request.TrackId}' was not found.");
        }

        var status = request.Status.Trim().ToLowerInvariant();
        var competitionClassPayload = new ImportCompetitionClassPayload(
            request.CompetitionClassCode,
            request.CompetitionClassName,
            request.CompetitionClassPriority);
        var internalContext = string.IsNullOrWhiteSpace(request.ContextName)
            ? BuildPlannedRoundContext(request.SeasonYear, request.RoundNumber, track.String("id")!)
            : request.ContextName.Trim();
        var roundPayload = new ImportRoundPayload(
            internalContext,
            request.SeasonYear,
            request.RoundNumber,
            request.StartDate,
            request.EndDate,
            request.CompetitionClassId,
            request.CompetitionClassCode,
            request.IsSeasonFinalRound,
            status);
        var competitionClass = await ResolveCompetitionClassAsync(
            roundPayload,
            competitionClassPayload,
            cancellationToken);
        var legacyCompetitionClass = await FindOrCreateLegacyCompetitionClassAsync(
            competitionClassPayload,
            cancellationToken);
        var condition = await FindOrCreateConditionAsync(
            new ImportConditionPayload(request.ConditionId, null, null),
            cancellationToken);

        await EnsurePlannedRoundDoesNotExistAsync(
            request,
            track.String("id")!,
            competitionClass.Id,
            cancellationToken);

        var round = await FindOrCreateRoundAsync(
            roundPayload,
            track.String("id")!,
            legacyCompetitionClass.String("id")!,
            condition.String("id")!,
            competitionClass.Id,
            cancellationToken);

        var categoryCodes = request.CategoryCodes
            .Select(PublicApiCodes.ToDatabaseCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var categoryCode in categoryCodes)
        {
            var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(
                categoryCode,
                SupabaseRole.Service,
                cancellationToken);
            if (categoryId is null)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "CATEGORY_NOT_FOUND",
                    $"Category '{categoryCode}' was not found.");
            }

            await EnsureRoundCategoryAsync(round.String("id")!, categoryId, cancellationToken);
        }

        var image = RoundImagePayloadParser.Parse(request.Image);
        if (image is not null)
        {
            await UpsertRoundImageAsync(round.String("id")!, image, cancellationToken);
        }

        return new PlannedRoundCreateResponse(
            round.String("id")!,
            status,
            track.String("id")!,
            categoryCodes,
            image is not null);
    }

    public async Task<ChronoRaceImportResponse> ImportSessionAsync(ChronoRaceImportRequest request, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (request.CompetitionClass is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "COMPETITION_CLASS_REQUIRED", "competition_class is required.");
        }

        if (request.Condition is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CONDITION_REQUIRED", "condition is required by the current database schema.");
        }

        var competitionClass = await ResolveCompetitionClassAsync(
            request.Round,
            request.CompetitionClass,
            cancellationToken);
        var normalizedResultJson = ImportResultJsonNormalizer.Normalize(
            request.ChronoraceJson,
            competitionClass.Code,
            request.CategoryCode);
        if (normalizedResultJson.Format == ImportResultJsonNormalizer.UciWorldChampionshipFormat)
        {
            warnings.Add("UCI World Championship result JSON was normalized before import.");
        }

        var track = await FindOrCreateTrackAsync(request.Track, cancellationToken);
        var legacyCompetitionClass = await FindOrCreateLegacyCompetitionClassAsync(
            request.CompetitionClass,
            cancellationToken);
        var condition = await FindOrCreateConditionAsync(request.Condition, cancellationToken);
        var categoryId = await _domainQueries.FindCategoryIdByCodeAsync(request.CategoryCode, SupabaseRole.Service, cancellationToken);
        if (categoryId is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CATEGORY_NOT_FOUND", $"Category '{request.CategoryCode}' was not found.");
        }

        var round = await FindOrCreateRoundAsync(
            request.Round,
            track.String("id")!,
            legacyCompetitionClass.String("id")!,
            condition.String("id")!,
            competitionClass.Id,
            cancellationToken);
        await EnsureRoundCategoryAsync(round.String("id")!, categoryId, cancellationToken);
        var session = await FindOrCreateSessionAsync(request.Session, round.String("id")!, categoryId, condition.String("id"), cancellationToken);

        var ridersDictionary = normalizedResultJson.Json.Object("Riders") ?? new JsonObject();
        var resultsArray = normalizedResultJson.Json.Array("Results") ?? new JsonArray();
        var riderMap = await UpsertRidersAsync(
            ridersDictionary,
            GenderFromCategory(request.CategoryCode),
            cancellationToken);
        var teamIdsByCode = await UpsertTeamsAsync(ridersDictionary, warnings, cancellationToken);
        var importedRiderIds = ImportedResultRiderIds(resultsArray, ridersDictionary, riderMap, warnings);
        var effectiveDate = request.Session.SessionDate ??
            (request.Round.StartDate is { } startDate
                ? DateOnly.FromDateTime(startDate.UtcDateTime)
                : (DateOnly?)null);
        var fallbackTeamIds = await ResolveFallbackTeamIdsAsync(
            importedRiderIds,
            effectiveDate,
            cancellationToken);
        var staleResultsDeleted = await DeleteStaleSessionResultsAsync(session.String("id")!, importedRiderIds, cancellationToken);
        if (staleResultsDeleted > 0)
        {
            warnings.Add($"Deleted {staleResultsDeleted} stale result(s) from this session before importing current ChronoRace results.");
        }

        var importedResults = await UpsertResultsAndSplitsAsync(
            session.String("id")!,
            resultsArray,
            ridersDictionary,
            riderMap.RiderIdsByUciId,
            teamIdsByCode,
            fallbackTeamIds,
            warnings,
            cancellationToken);
        if (importedResults.RiderIdsWithExplicitTeams.Count > 0)
        {
            await RefreshRiderTeamHistoryAsync(
                importedResults.RiderIdsWithExplicitTeams,
                cancellationToken);
        }

        if (importedResults.FinishedResultsWithoutSplits > 0)
        {
            warnings.Add(
                $"The result source returned no time splits for {importedResults.FinishedResultsWithoutSplits} finished result(s). " +
                "Existing time splits were preserved.");
        }

        var pointsRecalculated = await TryRecalculatePointsAsync(session.String("id")!, warnings, cancellationToken);

        await RefreshRankingPreaggregateAsync(
            session.String("id")!,
            warnings,
            cancellationToken);

        return new ChronoRaceImportResponse(
            round.String("id")!,
            session.String("id")!,
            riderMap.Created,
            riderMap.Updated,
            importedResults.ResultsUpserted,
            importedResults.SplitsUpserted,
            pointsRecalculated,
            warnings);
    }

    internal async Task<JsonObject> FindOrCreateTrackAsync(ImportTrackPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TRACK_NAME_REQUIRED",
                "track.name is required.");
        }

        if (string.IsNullOrWhiteSpace(payload.Venue))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TRACK_VENUE_REQUIRED",
                "track.venue is required.");
        }

        var normalizedName = payload.Name.Trim();
        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery("tracks")
                .Select("*")
                .Filter("name", "ilike", normalizedName),
            SupabaseRole.Service,
            cancellationToken);

        var values = new JsonObject
        {
            ["name"] = normalizedName,
            ["venue"] = payload.Venue.Trim()
        };

        // Missing optional metadata from an import must not erase data already
        // stored for a track. Explicit clearing remains available in the editor.
        if (existing is null || !string.IsNullOrWhiteSpace(payload.CountryCode))
        {
            values["country_code"] = payload.CountryCode?.Trim().ToUpperInvariant();
        }

        if (existing is null || payload.LengthM is not null)
        {
            values["length_m"] = payload.LengthM;
        }

        if (existing is null || payload.AvgSlope is not null)
        {
            values["avg_slope"] = payload.AvgSlope;
        }

        return existing is null
            ? (await _supabase.PostAsync("tracks", values, SupabaseRole.Service, cancellationToken: cancellationToken)).First()
            : (await _supabase.PatchAsync("tracks", values, new SupabaseQuery("tracks").Filter("id", "eq", existing.String("id")), SupabaseRole.Service, cancellationToken)).First();
    }

    internal async Task<JsonObject> FindOrCreateLegacyCompetitionClassAsync(
        ImportCompetitionClassPayload payload,
        CancellationToken cancellationToken)
    {
        var normalizedCode = payload.Code.Trim().ToLowerInvariant();
        var existing = await FindLegacyCompetitionClassByCodeAsync(normalizedCode, cancellationToken);
        var values = new JsonObject
        {
            ["code"] = normalizedCode,
            ["name"] = payload.Name.Trim(),
            ["value"] = payload.Priority
        };

        if (existing is not null)
        {
            return (await _supabase.PatchAsync(
                "competition_ranks",
                values,
                new SupabaseQuery("competition_ranks").Filter("id", "eq", existing.String("id")),
                SupabaseRole.Service,
                cancellationToken)).First();
        }

        try
        {
            return (await _supabase.PostAsync(
                "competition_ranks",
                values,
                SupabaseRole.Service,
                cancellationToken: cancellationToken)).First();
        }
        catch (SupabaseApiException exception) when (exception.StatusCode == StatusCodes.Status409Conflict)
        {
            existing = await FindLegacyCompetitionClassByCodeAsync(normalizedCode, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return (await _supabase.PatchAsync(
                "competition_ranks",
                values,
                new SupabaseQuery("competition_ranks").Filter("id", "eq", existing.String("id")),
                SupabaseRole.Service,
                cancellationToken)).First();
        }
    }

    private async Task<JsonObject?> FindLegacyCompetitionClassByCodeAsync(
        string normalizedCode,
        CancellationToken cancellationToken)
    {
        var result = await _supabase.GetAsync(
            new SupabaseQuery("competition_ranks").Select("*"),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);

        return result.Data.FirstOrDefault(row =>
            string.Equals(
                row.String("code")?.Trim(),
                normalizedCode,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonObject> FindOrCreateConditionAsync(ImportConditionPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Id is not null)
        {
            var byId = await _domainQueries.FindByIdAsync("conditions", payload.Id.Value.ToString(), role: SupabaseRole.Service, cancellationToken: cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        var existing = string.IsNullOrWhiteSpace(payload.Name)
            ? null
            : await _supabase.GetSingleAsync(new SupabaseQuery("conditions").Select("*").Filter("name", "eq", payload.Name), SupabaseRole.Service, cancellationToken);
        if (existing is null && string.IsNullOrWhiteSpace(payload.Name))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CONDITION_NOT_FOUND", "condition.id was not found and condition.name was not provided.");
        }

        var values = new JsonObject { ["name"] = payload.Name, ["is_dry"] = payload.IsDry };
        return existing is null
            ? (await _supabase.PostAsync("conditions", values, SupabaseRole.Service, cancellationToken: cancellationToken)).First()
            : (await _supabase.PatchAsync("conditions", values, new SupabaseQuery("conditions").Filter("id", "eq", existing.String("id")), SupabaseRole.Service, cancellationToken)).First();
    }

    private async Task<ResolvedCompetitionClass> ResolveCompetitionClassAsync(
        ImportRoundPayload payload,
        ImportCompetitionClassPayload competitionClass,
        CancellationToken cancellationToken)
    {
        var declaredCode = competitionClass.Code.Trim().ToLowerInvariant();
        if (payload.CompetitionClassId is > 0)
        {
            var byId = await _domainQueries.FindByIdAsync(
                "competition_levels",
                payload.CompetitionClassId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "id,code,is_active",
                SupabaseRole.Service,
                cancellationToken);
            if (byId is not null)
            {
                EnsureMatchingCompetitionClassCode(declaredCode, byId.String("code"));
                return new ResolvedCompetitionClass(byId.String("id")!, byId.String("code")!);
            }

            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "COMPETITION_CLASS_NOT_FOUND",
                $"Competition class id '{payload.CompetitionClassId}' was not found.");
        }

        var code = string.IsNullOrWhiteSpace(payload.CompetitionClassCode)
            ? declaredCode
            : payload.CompetitionClassCode.Trim().ToLowerInvariant();
        EnsureMatchingCompetitionClassCode(declaredCode, code);
        var byCode = await _domainQueries.FindCompetitionClassIdByCodeAsync(
            code,
            SupabaseRole.Service,
            cancellationToken);
        return byCode is not null
            ? new ResolvedCompetitionClass(byCode, code)
            : throw new ApiException(
                StatusCodes.Status400BadRequest,
                "COMPETITION_CLASS_NOT_FOUND",
                $"Competition class '{code}' was not found.");
    }

    private static void EnsureMatchingCompetitionClassCode(
        string declaredCode,
        string? resolvedCode)
    {
        if (string.IsNullOrWhiteSpace(declaredCode) ||
            string.Equals(declaredCode, resolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ApiException(
            StatusCodes.Status400BadRequest,
            "COMPETITION_CLASS_MISMATCH",
            "round.competition_class_code must match competition_class.code.");
    }

    private async Task<JsonObject> FindOrCreateRoundAsync(
        ImportRoundPayload payload,
        string trackId,
        string legacyCompetitionClassId,
        string conditionId,
        string competitionClassId,
        CancellationToken cancellationToken)
    {
        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery("rounds")
                .Select("*")
                .Filter("context_name", "eq", payload.ContextName),
            SupabaseRole.Service,
            cancellationToken);
        var values = new JsonObject
        {
            ["context_name"] = payload.ContextName,
            ["season_year"] = payload.SeasonYear,
            ["round_number"] = payload.RoundNumber,
            ["start_date"] = payload.StartDate?.ToString("O"),
            ["end_date"] = payload.EndDate?.ToString("O"),
            ["track_id"] = trackId,
            ["competition_rank_id"] = legacyCompetitionClassId,
            ["competition_level_id"] = competitionClassId,
            ["is_season_final_round"] = payload.IsSeasonFinalRound,
            ["condition_id"] = conditionId
        };
        if (!string.IsNullOrWhiteSpace(payload.Status))
        {
            values["status"] = payload.Status.Trim().ToLowerInvariant();
        }

        return await SaveRoundAsync(existing, values, cancellationToken);
    }

    private async Task<JsonObject> SaveRoundAsync(
        JsonObject? existing,
        JsonObject values,
        CancellationToken cancellationToken)
    {
        try
        {
            return existing is null
                ? (await _supabase.PostAsync("rounds", values, SupabaseRole.Service, cancellationToken: cancellationToken)).First()
                : (await _supabase.PatchAsync(
                    "rounds",
                    values,
                    new SupabaseQuery("rounds").Filter("id", "eq", existing.String("id")),
                    SupabaseRole.Service,
                    cancellationToken)).First();
        }
        catch (SupabaseApiException exception) when (
            values.String("status") is { } status &&
            exception.ResponseBody.Contains("status", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(status, "scheduled", StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(
                    StatusCodes.Status503ServiceUnavailable,
                    "ROUND_STATUS_SCHEMA_REQUIRED",
					"Apply the round status database migration before using confirmed, live, or cancelled.");
            }

            values.Remove("status");
            return existing is null
                ? (await _supabase.PostAsync("rounds", values, SupabaseRole.Service, cancellationToken: cancellationToken)).First()
                : (await _supabase.PatchAsync(
                    "rounds",
                    values,
                    new SupabaseQuery("rounds").Filter("id", "eq", existing.String("id")),
                    SupabaseRole.Service,
                    cancellationToken)).First();
        }
    }

    private async Task EnsurePlannedRoundDoesNotExistAsync(
        PlannedRoundCreateRequest request,
        string trackId,
        string competitionClassId,
        CancellationToken cancellationToken)
    {
        var query = new SupabaseQuery("rounds")
            .Select("id")
            .Filter("season_year", "eq", request.SeasonYear)
            .Filter("track_id", "eq", trackId)
            .Filter("competition_level_id", "eq", competitionClassId);
        if (request.RoundNumber is not null)
        {
            query.Filter("round_number", "eq", request.RoundNumber.Value);
        }

        var existing = await _supabase.GetSingleAsync(query, SupabaseRole.Service, cancellationToken);
        if (existing is not null)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "PLANNED_ROUND_ALREADY_EXISTS",
                "A round with the same season, track, competition class and round number already exists.");
        }
    }

    private async Task UpsertRoundImageAsync(
        string roundId,
        PreparedRoundImage image,
        CancellationToken cancellationToken)
    {
        var storagePath = _imageStorage is null
            ? null
            : await _imageStorage.UploadAsync(image, cancellationToken);
        await _supabase.PostAsync(
            "round_images",
            new JsonObject
            {
                ["round_id"] = roundId,
                ["image_data"] = storagePath is null ? $"\\x{Convert.ToHexString(image.Bytes)}" : null,
                ["storage_path"] = storagePath,
                ["content_type"] = RoundImagePayloadParser.ContentType,
                ["file_name"] = image.FileName,
                ["alt_text"] = image.AltText,
                ["width_px"] = image.WidthPx,
                ["height_px"] = image.HeightPx
            },
            SupabaseRole.Service,
            onConflict: "round_id",
            cancellationToken: cancellationToken);
    }

    private static void ValidatePlannedRound(PlannedRoundCreateRequest request)
    {
        if (request.SeasonYear is < 1990 or > 2100)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SEASON", "season_year must be between 1990 and 2100.");
        }

        if (request.RoundNumber is <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_ROUND_NUMBER", "round_number must be greater than zero.");
        }

        if (request.EndDate < request.StartDate)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_ROUND_DATES", "end_date cannot be before start_date.");
        }

        if (request.StartDate.Year != request.SeasonYear || request.EndDate.Year != request.SeasonYear)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "ROUND_SEASON_MISMATCH", "Round dates must belong to season_year.");
        }

        if (!PlannedRoundStatuses.Contains(request.Status?.Trim() ?? string.Empty))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_ROUND_STATUS",
				"status must be scheduled, confirmed, live, or cancelled.");
        }

        if (request.CompetitionClassId <= 0 ||
            string.IsNullOrWhiteSpace(request.CompetitionClassCode) ||
            string.IsNullOrWhiteSpace(request.CompetitionClassName))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "COMPETITION_CLASS_REQUIRED", "A competition class is required.");
        }

        if (request.ConditionId <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CONDITION_REQUIRED", "A condition is required.");
        }

        if (request.TrackId is not > 0 && request.Track is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "TRACK_REQUIRED", "Choose an existing track or provide a new track.");
        }

        if (request.CategoryCodes is null || request.CategoryCodes.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CATEGORIES_REQUIRED", "At least one category is required.");
        }
    }

    private static string BuildPlannedRoundContext(int seasonYear, int? roundNumber, string trackId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"planned_{seasonYear}_{roundNumber ?? 0}_{trackId}_{suffix}";
    }

    private async Task EnsureRoundCategoryAsync(string roundId, string categoryId, CancellationToken cancellationToken)
    {
        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery("round_categories")
                .Select("*")
                .Filter("round_id", "eq", roundId)
                .Filter("category_id", "eq", categoryId),
            SupabaseRole.Service,
            cancellationToken);

        if (existing is null)
        {
            await _supabase.PostAsync("round_categories", new JsonObject { ["round_id"] = roundId, ["category_id"] = categoryId }, SupabaseRole.Service, cancellationToken: cancellationToken);
        }
    }

    private async Task<JsonObject> FindOrCreateSessionAsync(ImportSessionPayload payload, string roundId, string categoryId, string? conditionId, CancellationToken cancellationToken)
    {
        var sessionType = DomainQueryService.NormalizeSessionType(payload.SessionType);
        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery("sessions")
                .Select("*")
                .Filter("round_id", "eq", roundId)
                .Filter("category_id", "eq", categoryId)
                .Filter("session_type", "eq", sessionType)
                .Filter("name", "eq", payload.Name),
            SupabaseRole.Service,
            cancellationToken);
        var values = BuildSessionPayload(payload, roundId, categoryId, conditionId, sessionType);

        return await SaveSessionAsync(existing, values, cancellationToken);
    }

	private async Task<JsonObject> SaveSessionAsync(JsonObject? existing, JsonObject values, CancellationToken cancellationToken)
	{
		while (true)
		{
			try
			{
				return existing is null
					? (await _supabase.PostAsync("sessions", values, SupabaseRole.Service, cancellationToken: cancellationToken)).First()
					: (await _supabase.PatchAsync("sessions", values, new SupabaseQuery("sessions").Filter("id", "eq", existing.String("id")), SupabaseRole.Service, cancellationToken)).First();
			}
			catch (SupabaseApiException exception) when (
				RemoveMissingOptionalSessionColumn(values, exception, "chronorace_url"))
			{
				// Live-source metadata remains optional for databases created before live timing support.
			}
		}
	}

	private static bool RemoveMissingOptionalSessionColumn(
		JsonObject values,
		SupabaseApiException exception,
		string column)
	{
		return values.ContainsKey(column) &&
			exception.ResponseBody.Contains(column, StringComparison.OrdinalIgnoreCase) &&
			(exception.ResponseBody.Contains("schema cache", StringComparison.OrdinalIgnoreCase) ||
			 exception.ResponseBody.Contains("PGRST204", StringComparison.OrdinalIgnoreCase)) &&
			values.Remove(column);
	}

    private async Task<RiderImportMap> UpsertRidersAsync(
        JsonObject riders,
        string? gender,
        CancellationToken cancellationToken)
    {
        var sourceByUciId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var riderNode in riders.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var uciId = riderNode.String("UciRiderId");
            if (!string.IsNullOrWhiteSpace(uciId))
            {
                sourceByUciId[uciId] = riderNode;
            }
        }

        if (sourceByUciId.Count == 0)
        {
            return new RiderImportMap(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Created: 0,
                Updated: 0);
        }

        var existingRows = await _batches.GetRowsByValuesAsync(
            "riders",
            "*",
            "uci_id",
            sourceByUciId.Keys,
            cancellationToken);
        var existingByUciId = IndexUniqueRows(
            existingRows,
            "uci_id",
            "DUPLICATE_RIDER_UCI_ID");
        var payloads = sourceByUciId
            .Select(pair => BuildRiderPayload(
                pair.Value,
                existingByUciId.GetValueOrDefault(pair.Key),
                gender))
            .ToArray();
        var savedRows = await _batches.PostRowsAsync(
            "riders",
            payloads,
            onConflict: "uci_id",
            cancellationToken);
        var savedByUciId = IndexUniqueRows(
            savedRows,
            "uci_id",
            "BULK_RIDER_RESPONSE_DUPLICATE");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uciId in sourceByUciId.Keys)
        {
            if (!savedByUciId.TryGetValue(uciId, out var saved) ||
                string.IsNullOrWhiteSpace(saved.String("id")))
            {
                throw IncompleteBulkResponse("riders", "uci_id", uciId);
            }

            map[uciId] = saved.String("id")!;
        }

        var updated = sourceByUciId.Keys.Count(existingByUciId.ContainsKey);
        return new RiderImportMap(map, sourceByUciId.Count - updated, updated);
    }

    private static HashSet<string> ImportedResultRiderIds(JsonArray results, JsonObject riders, RiderImportMap riderMap, List<string> warnings)
    {
        var importedRiderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results.OfType<JsonObject>())
        {
            var technicalRaceNr = result.String("RaceNr");
            if (technicalRaceNr is null || !riders.TryGetPropertyValue(technicalRaceNr, out var riderNode) || riderNode is not JsonObject chronoRider)
            {
                continue;
            }

            var uciId = chronoRider.String("UciRiderId");
            if (!string.IsNullOrWhiteSpace(uciId) && riderMap.RiderIdsByUciId.TryGetValue(uciId, out var riderId))
            {
                importedRiderIds.Add(riderId);
            }
        }

        if (importedRiderIds.Count == 0 && results.Count > 0)
        {
            warnings.Add("No importable riders were resolved from ChronoRace results; stale session cleanup was skipped.");
        }

        return importedRiderIds;
    }

    private async Task<int> DeleteStaleSessionResultsAsync(string sessionId, HashSet<string> importedRiderIds, CancellationToken cancellationToken)
    {
        if (importedRiderIds.Count == 0)
        {
            return 0;
        }

        var existing = await _supabase.GetAsync(
            new SupabaseQuery("results")
                .Select("id,rider_id")
                .Filter("session_id", "eq", sessionId),
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);

        var staleResultIds = existing.Data
            .Where(result => result.String("rider_id") is { } riderId && !importedRiderIds.Contains(riderId))
            .Select(result => result.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        if (staleResultIds.Length == 0)
        {
            return 0;
        }

        await _batches.DeleteRowsByValuesAsync(
            "time_splits",
            "result_id",
            staleResultIds,
            cancellationToken);
        await _batches.DeleteRowsByValuesAsync(
            "results",
            "id",
            staleResultIds,
            cancellationToken);

        return staleResultIds.Length;
    }

    private async Task<IReadOnlyDictionary<string, string>> UpsertTeamsAsync(
        JsonObject riders,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var teams = new Dictionary<string, ChronoRaceTeam>(StringComparer.OrdinalIgnoreCase);
        var invalidCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rider in riders.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var team = ParseChronoRaceTeam(rider);
            if (team is not null)
            {
                if (!teams.TryGetValue(team.Value.Code, out var current) ||
                    string.Equals(current.Name, current.Code, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(team.Value.Name, team.Value.Code, StringComparison.OrdinalIgnoreCase))
                {
                    teams[team.Value.Code] = team.Value;
                }

                continue;
            }

            var rawCode = FirstNonBlank(rider.String("UciTeamCode"), rider.String("UciTeamId"));
            if (!string.IsNullOrWhiteSpace(rawCode))
            {
                invalidCodes.Add(rawCode.Trim());
            }
        }

        foreach (var invalidCode in invalidCodes)
        {
            warnings.Add($"Team code '{invalidCode}' was skipped because teams.code must contain exactly three uppercase letters or digits.");
        }

        if (teams.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var existingRows = await _batches.GetRowsByValuesAsync(
            "teams",
            "*",
            "code",
            teams.Keys,
            cancellationToken);
        var existingByCode = IndexUniqueRows(
            existingRows,
            "code",
            "DUPLICATE_TEAM_CODE");
        var payloads = teams.Values
            .Select(team =>
            {
                var existing = existingByCode.GetValueOrDefault(team.Code);
                return new JsonObject
                {
                    ["code"] = team.Code,
                    ["name"] = string.Equals(team.Name, team.Code, StringComparison.OrdinalIgnoreCase)
                        ? existing?.String("name") ?? team.Code
                        : team.Name
                };
            })
            .ToArray();
        var savedRows = await _batches.PostRowsAsync(
            "teams",
            payloads,
            onConflict: "code",
            cancellationToken);
        var savedByCode = IndexUniqueRows(
            savedRows,
            "code",
            "BULK_TEAM_RESPONSE_DUPLICATE");

        var teamIdsByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in teams.Keys)
        {
            if (!savedByCode.TryGetValue(code, out var saved) ||
                string.IsNullOrWhiteSpace(saved.String("id")))
            {
                throw IncompleteBulkResponse("teams", "code", code);
            }

            teamIdsByCode[code] = saved.String("id")!;
        }

        return teamIdsByCode;
    }

    internal static ChronoRaceTeam? ParseChronoRaceTeam(JsonObject rider)
    {
        var rawCode = FirstNonBlank(rider.String("UciTeamCode"), rider.String("UciTeamId"));
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return null;
        }

        var code = rawCode.Trim().ToUpperInvariant();
        if (code.Length != 3 ||
            !code.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character)))
        {
            return null;
        }

        var name = rider.String("UciTeamName")?.Trim();
        return new ChronoRaceTeam(code, string.IsNullOrWhiteSpace(name) ? code : name);
    }

    private async Task<ResultBatchImport> UpsertResultsAndSplitsAsync(
        string sessionId,
        JsonArray results,
        JsonObject riders,
        IReadOnlyDictionary<string, string> riderIdsByUciId,
        IReadOnlyDictionary<string, string> teamIdsByCode,
        IReadOnlyDictionary<string, string> fallbackTeamIdsByRiderId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var pendingByRiderId = new Dictionary<string, PendingResultImport>(StringComparer.OrdinalIgnoreCase);
        var riderIdsWithExplicitTeams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results.OfType<JsonObject>())
        {
            var technicalRaceNr = result.String("RaceNr");
            if (technicalRaceNr is null ||
                !riders.TryGetPropertyValue(technicalRaceNr, out var riderNode) ||
                riderNode is not JsonObject chronoRider)
            {
                warnings.Add($"Result with RaceNr '{technicalRaceNr}' has no matching rider entry.");
                continue;
            }

            var uciId = chronoRider.String("UciRiderId");
            if (string.IsNullOrWhiteSpace(uciId) ||
                !riderIdsByUciId.TryGetValue(uciId, out var riderId))
            {
                warnings.Add($"Result with RaceNr '{technicalRaceNr}' has no UCI rider id.");
                continue;
            }

            var team = ParseChronoRaceTeam(chronoRider);
            var teamId = team is not null &&
                teamIdsByCode.TryGetValue(team.Value.Code, out var resolvedTeamId)
                    ? resolvedTeamId
                    : fallbackTeamIdsByRiderId.GetValueOrDefault(riderId);
            var values = BuildResultPayload(
                sessionId,
                riderId,
                teamId,
                result,
                chronoRider,
                NormalizePosition(result.Int("Position")),
                IsClassified(result));
            if (!string.IsNullOrWhiteSpace(teamId))
            {
                riderIdsWithExplicitTeams.Add(riderId);
            }

            if (pendingByRiderId.ContainsKey(riderId))
            {
                warnings.Add(
                    $"ChronoRace returned more than one result for rider UCI ID '{uciId}'. " +
                    "The last result was imported.");
            }

            pendingByRiderId[riderId] = new PendingResultImport(
                riderId,
                result,
                result.Array("Times") ?? new JsonArray(),
                values);
        }

        if (pendingByRiderId.Count == 0)
        {
            return new ResultBatchImport(0, 0, 0, []);
        }

        var savedRows = await _batches.PostRowsAsync(
            "results",
            pendingByRiderId.Values.Select(pending => pending.Payload).ToArray(),
            onConflict: "session_id,rider_id",
            cancellationToken);
        var savedByRiderId = IndexUniqueRows(
            savedRows,
            "rider_id",
            "BULK_RESULT_RESPONSE_DUPLICATE");
        var splitRows = new List<JsonObject>();
        var resultIdsWithSplits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finishedResultsWithoutSplits = 0;

        foreach (var pending in pendingByRiderId.Values)
        {
            if (!savedByRiderId.TryGetValue(pending.RiderId, out var saved) ||
                string.IsNullOrWhiteSpace(saved.String("id")))
            {
                throw IncompleteBulkResponse("results", "rider_id", pending.RiderId);
            }

            var resultId = saved.String("id")!;
            var times = TimingSplitsWithoutFinish(
                pending.Times,
                pending.Source.Int("RaceTime"));
            if (times.Length == 0)
            {
                if (IsFinishedResultWithoutSplits(pending.Source, pending.Times))
                {
                    finishedResultsWithoutSplits++;
                }

                continue;
            }

            resultIdsWithSplits.Add(resultId);
            splitRows.AddRange(times.Select((time, index) => new JsonObject
            {
                ["result_id"] = resultId,
                ["split_index"] = time.Int("SplitIndex") ?? index + 1,
                ["race_time"] = time.Int("RaceTime"),
                ["speed"] = time.Decimal("Speed")
            }));
        }

        if (resultIdsWithSplits.Count > 0)
        {
            await _batches.DeleteRowsByValuesAsync(
                "time_splits",
                "result_id",
                resultIdsWithSplits,
                cancellationToken);
            await _batches.PostRowsAsync(
                "time_splits",
                splitRows,
                onConflict: null,
                cancellationToken);
        }

        return new ResultBatchImport(
            pendingByRiderId.Count,
            splitRows.Count,
            finishedResultsWithoutSplits,
            riderIdsWithExplicitTeams.ToArray());
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveFallbackTeamIdsAsync(
        HashSet<string> riderIds,
        DateOnly? effectiveDate,
        CancellationToken cancellationToken)
    {
        if (!effectiveDate.HasValue || riderIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var history = await _domainQueries.GetRiderTeamHistoryAsync(
            riderIds,
            role: SupabaseRole.Service,
            cancellationToken: cancellationToken);
        return riderIds
            .Select(riderId => new
            {
                RiderId = riderId,
                TeamId = DomainQueryService.FindTeamPeriodOrNearest(
                    history,
                    riderId,
                    effectiveDate.Value)?.String("team_id")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.TeamId))
            .ToDictionary(
                item => item.RiderId,
                item => item.TeamId!,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshRiderTeamHistoryAsync(
        IReadOnlyCollection<string> riderIds,
        CancellationToken cancellationToken)
    {
        var ids = riderIds
            .Select(id => long.TryParse(id, out var parsed) ? parsed : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => (JsonNode?)JsonValue.Create(id!.Value))
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await _supabase.RpcAsync(
            "refresh_rider_team_history",
            new JsonObject
            {
                ["p_rider_ids"] = new JsonArray(ids)
            },
            SupabaseRole.Service,
            cancellationToken);
    }

    private async Task RefreshRankingPreaggregateAsync(
        string sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(sessionId, out var parsedSessionId))
        {
            warnings.Add("Ranking preaggregation was skipped because the session id is invalid.");
            return;
        }

        try
        {
            if (!await new RankingPreaggregationService(_supabase).RefreshAsync(sessionId, cancellationToken))
                warnings.Add("Prepared metrics were not saved because the source session changed. Reads will use the compatibility path.");
        }
        catch (SupabaseApiException)
        {
            warnings.Add(
                "Could not refresh ranking preaggregation for this session. " +
                "Import finished successfully and ranking will use the compatibility path.");
        }
    }

    internal async Task<int> ReplaceTimeSplitsAsync(
        string resultId,
        JsonArray times,
        CancellationToken cancellationToken,
        int? finishTimeMs = null)
    {
        var rows = TimingSplitsWithoutFinish(times, finishTimeMs)
            .Select((time, index) => new JsonObject
            {
                ["result_id"] = resultId,
                ["split_index"] = time.Int("SplitIndex") ?? index + 1,
                ["race_time"] = time.Int("RaceTime"),
                ["speed"] = time.Decimal("Speed")
            })
            .ToArray();

        if (rows.Length == 0)
        {
            return 0;
        }

        await _supabase.DeleteAsync("time_splits", new SupabaseQuery("time_splits").Filter("result_id", "eq", resultId), SupabaseRole.Service, cancellationToken);
        await _supabase.PostAsync("time_splits", new JsonArray(rows), SupabaseRole.Service, cancellationToken: cancellationToken);
        return rows.Length;
    }

    private static Dictionary<string, JsonObject> IndexUniqueRows(
        IEnumerable<JsonObject> rows,
        string keyColumn,
        string duplicateCode)
    {
        var indexed = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.String(keyColumn);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!indexed.TryAdd(key, row))
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    duplicateCode,
                    $"More than one '{row.String(keyColumn)}' row was returned for {keyColumn} '{key}'.");
            }
        }

        return indexed;
    }

    private static ApiException IncompleteBulkResponse(
        string table,
        string keyColumn,
        string key)
    {
        return new ApiException(
            StatusCodes.Status502BadGateway,
            "INCOMPLETE_SUPABASE_BULK_RESPONSE",
            $"Supabase bulk upsert for '{table}' did not return a row with {keyColumn} '{key}'.");
    }

    private async Task<bool> TryRecalculatePointsAsync(string sessionId, List<string> warnings, CancellationToken cancellationToken)
    {
        var calculation = await _pointAwards.RecalculateSessionAsync(sessionId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(calculation.Warning))
        {
            warnings.Add(calculation.Warning);
        }

        return calculation.PointSystemApplied;
    }

    private sealed record RiderImportMap(IReadOnlyDictionary<string, string> RiderIdsByUciId, int Created, int Updated);

    private sealed record PendingResultImport(
        string RiderId,
        JsonObject Source,
        JsonArray Times,
        JsonObject Payload);

    private sealed record ResultBatchImport(
        int ResultsUpserted,
        int SplitsUpserted,
        int FinishedResultsWithoutSplits,
        IReadOnlyList<string> RiderIdsWithExplicitTeams);

    private sealed record ResolvedCompetitionClass(string Id, string Code);

    internal readonly record struct ChronoRaceTeam(string Code, string Name);

    internal static JsonObject BuildSessionPayload(ImportSessionPayload payload, string roundId,
        string categoryId, string? conditionId, string sessionType) =>
        Features.Imports.ChronoRace.ChronoRacePayloadMapper.BuildSessionPayload(payload, roundId, categoryId, conditionId, sessionType);

    internal static JsonObject BuildResultPayload(string sessionId, string riderId, string? teamId,
        JsonObject result, JsonObject chronoRider, int? position, bool isClassified) =>
        Features.Imports.ChronoRace.ChronoRacePayloadMapper.BuildResultPayload(sessionId, riderId, teamId, result, chronoRider, position, isClassified);

    internal static JsonObject[] TimingSplitsWithoutFinish(JsonArray times, int? finishTimeMs) =>
        Features.Imports.ChronoRace.ChronoRacePayloadMapper.TimingSplitsWithoutFinish(times, finishTimeMs);

    internal static string? GenderFromCategory(string? categoryCode) =>
        Features.Imports.ChronoRace.ChronoRacePayloadMapper.GenderFromCategory(categoryCode);
}
