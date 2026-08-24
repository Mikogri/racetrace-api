using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class AdminDataService
{
    private static readonly Regex CountryCodePattern = new("^[A-Z]{2,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex TeamCodePattern = new("^[A-Z0-9]{3}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SessionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q1", "Q2", "SEMI", "FINAL", "QUALI", "PRACTICE", "OTHER"
    };

    private static readonly IReadOnlyDictionary<string, AdminResourceDefinition> Resources =
        new Dictionary<string, AdminResourceDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["tracks"] = Definition("tracks", "name", "venue", "country_code", "length_m", "avg_slope"),
            ["conditions"] = Definition("conditions", "name", "is_dry"),
            ["competition-classes"] = Definition("competition_levels", "code", "name", "priority", "description", "is_active"),
            ["categories"] = Definition("categories", "name", "min_age", "max_age", "code"),
            ["rounds"] = MappedDefinition(
                "rounds",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["competition_class_id"] = "competition_level_id"
                },
                "context_name", "track_id", "competition_class_id", "is_season_final_round", "condition_id", "season_year", "round_number", "start_date", "end_date", "status"),
            ["sessions"] = Definition("sessions", "name", "status", "round_id", "category_id", "condition_id", "session_type", "points_session_type_override", "session_date"),
            ["riders"] = Definition("riders", "uci_id", "uci_code", "last_name", "first_name", "nation", "birth_date", "is_protected", "has_injury", "profile_image_url", "profile_image_updated_at"),
            ["teams"] = Definition("teams", "name", "code"),
            ["results"] = Definition("results", "race_nr", "completed_distance", "status", "top_speed", "session_id", "rider_id", "position", "race_time_ms", "is_classified", "team_id"),
            ["time-splits"] = Definition("time_splits", "result_id", "split_index", "race_time", "speed"),
            ["points-series"] = Definition("points_series", "code", "name"),
            ["points-systems"] = MappedDefinition(
                "points_systems",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["competition_class_id"] = "competition_level_id"
                },
                "code", "name", "competition_class_id", "discipline", "category_id", "valid_from_season", "valid_to_season", "source_url", "notes"),
            ["point-system-stages"] = Definition("point_system_stages", "point_system_id", "session_type", "awards_points", "awards_points_on_season_final_round"),
            ["point-rules"] = Definition("point_rules", "point_system_stage_id", "position_from", "position_to", "points", "season_final_points"),
            ["points-system-positions"] = Definition("points_system_positions", "points_system_id", "position", "points"),
            ["result-points"] = Definition("result_points", "result_id", "points_system_id", "point_system_stage_id", "point_rule_id", "position_used", "points_awarded")
        };

    private readonly ISupabaseRestClient _supabase;

    private readonly RoundImageStorageService? _imageStorage;

    public AdminDataService(ISupabaseRestClient supabase, RoundImageStorageService? imageStorage = null)
    {
        _supabase = supabase;
        _imageStorage = imageStorage;
    }

    public JsonObject EditableResources()
    {
        var resources = new JsonObject();
        foreach (var resource in Resources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            resources[resource.Key] = new JsonArray(resource.Value.Fields
                .OrderBy(field => field, StringComparer.Ordinal)
                .Select(field => (JsonNode?)JsonValue.Create(field))
                .ToArray());
        }

        return resources;
    }

    public Task<JsonObject> UpdateRiderAsync(
        string riderId,
        JsonObject? requestedPatch,
        CancellationToken cancellationToken)
    {
        if (requestedPatch is null)
        {
            return UpdateAsync("riders", riderId, null, cancellationToken);
        }

        var normalizedPatch = new JsonObject();
        foreach (var property in requestedPatch)
        {
            var field = property.Key switch
            {
                "profileImageUrl" => "profile_image_url",
                "hasInjury" => "has_injury",
                _ => property.Key
            };
            if (field is not ("profile_image_url" or "has_injury"))
            {
                throw Invalid(
                    "ADMIN_FIELDS_NOT_EDITABLE",
                    $"Fields are not editable for this rider endpoint: {property.Key}.");
            }

            if (normalizedPatch.ContainsKey(field))
            {
                throw Invalid(
                    "DUPLICATE_ADMIN_FIELD",
                    $"Field '{field}' was provided more than once.");
            }

            normalizedPatch[field] = property.Value?.DeepClone();
        }

        return UpdateAsync(
            "riders",
            riderId,
            normalizedPatch,
            cancellationToken);
    }

    public async Task<JsonObject> UpdateAsync(
        string resourceName,
        string id,
        JsonObject? requestedPatch,
        CancellationToken cancellationToken)
    {
        var definition = ResolveResource(resourceName);
        EnsurePositiveId(id, "id");
        if (requestedPatch is null || requestedPatch.Count == 0)
        {
            throw Invalid("EMPTY_ADMIN_PATCH", "At least one editable field is required.");
        }

        var unknownFields = requestedPatch
            .Select(property => property.Key)
            .Where(field => !definition.Fields.Contains(field))
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToArray();
        if (unknownFields.Length > 0)
        {
            throw Invalid(
                "ADMIN_FIELDS_NOT_EDITABLE",
                $"Fields are not editable for '{resourceName}': {string.Join(", ", unknownFields)}.");
        }

        if (requestedPatch.Any(property => property.Value is JsonArray or JsonObject))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", "Editable fields must contain scalar values or null.");
        }

        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery(definition.Table).Select("*").Filter("id", "eq", id),
            SupabaseRole.Service,
            cancellationToken);
        if (existing is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "ADMIN_RESOURCE_NOT_FOUND", $"{resourceName} resource '{id}' was not found.");
        }

        var patch = ToDatabasePatch(definition, requestedPatch);
        NormalizePatch(resourceName, patch);
        var candidate = existing.DeepClone().AsObject();
        foreach (var property in patch)
        {
            candidate[property.Key] = property.Value?.DeepClone();
        }

        ValidateCandidate(resourceName, candidate);
        await ValidateUniqueValuesAsync(resourceName, id, candidate, cancellationToken);
        await ValidateReferencesAsync(resourceName, candidate, cancellationToken);

        JsonObject updated;
        try
        {
            updated = await PatchResourceAsync(resourceName, definition, id, existing, patch, cancellationToken);
        }
        catch (SupabaseApiException exception) when (IsMissingSessionConditionColumn(resourceName, patch, exception))
        {
            updated = await PatchSessionWithRoundConditionFallbackAsync(id, existing, patch, cancellationToken);
        }
        catch (SupabaseApiException exception) when (exception.StatusCode == StatusCodes.Status409Conflict)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "ADMIN_RESOURCE_CONFLICT", "The edited values conflict with an existing database record.");
        }

        return ToApiResource(resourceName, updated);
    }

    public async Task<JsonObject> UpdateRoundTrackAsync(
        string roundId,
        JsonObject? requestedPatch,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(roundId, "round_id");
        if (requestedPatch is null ||
            requestedPatch.Count != 1 ||
            !requestedPatch.ContainsKey("track_id"))
        {
            throw Invalid(
                "INVALID_ROUND_TRACK_PATCH",
                "The request must contain only track_id.");
        }

        var trackId = RequiredPositiveLong(requestedPatch, "track_id");
        await EnsureReferenceAsync("rounds", roundId, "round_id", cancellationToken);
        await EnsureReferenceAsync("tracks", trackId, "track_id", cancellationToken);

        var updated = await _supabase.PatchAsync(
            "rounds",
            new JsonObject
            {
                ["track_id"] = long.Parse(trackId, CultureInfo.InvariantCulture)
            },
            new SupabaseQuery("rounds").Filter("id", "eq", roundId),
            SupabaseRole.Service,
            cancellationToken);

        if (updated.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "ADMIN_RESOURCE_NOT_FOUND",
                $"rounds resource '{roundId}' was not found.");
        }

        return new JsonObject
        {
            ["roundId"] = long.Parse(roundId, CultureInfo.InvariantCulture),
            ["trackId"] = long.Parse(trackId, CultureInfo.InvariantCulture)
        };
    }

    private async Task ValidateUniqueValuesAsync(
        string resourceName,
        string editedId,
        JsonObject candidate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(resourceName, "tracks", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = RequiredText(candidate, "name").Trim();
        var duplicate = await _supabase.GetSingleAsync(
            new SupabaseQuery("tracks")
                .Select("id,name")
                .Filter("name", "ilike", name)
                .Filter("id", "neq", editedId),
            SupabaseRole.Service,
            cancellationToken);

        if (duplicate is not null &&
            !string.Equals(duplicate.String("id"), editedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "TRACK_NAME_ALREADY_EXISTS",
                $"A track named '{name}' already exists.");
        }
    }

    private async Task<JsonObject> PatchResourceAsync(
        string resourceName,
        AdminResourceDefinition definition,
        string id,
        JsonObject existing,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        if (patch.Count == 0)
        {
            return existing.DeepClone().AsObject();
        }

        var updated = await _supabase.PatchAsync(
            definition.Table,
            patch,
            new SupabaseQuery(definition.Table).Filter("id", "eq", id),
            SupabaseRole.Service,
            cancellationToken);
        return updated.FirstOrDefault()
            ?? throw new ApiException(StatusCodes.Status404NotFound, "ADMIN_RESOURCE_NOT_FOUND", $"{resourceName} resource '{id}' was not found.");
    }

    private async Task<JsonObject> PatchSessionWithRoundConditionFallbackAsync(
        string sessionId,
        JsonObject existingSession,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        var conditionId = OptionalLong(patch, "condition_id");
        patch.Remove("condition_id");

        var updatedSession = await PatchResourceAsync(
            "sessions",
            Resources["sessions"],
            sessionId,
            existingSession,
            patch,
            cancellationToken);

        if (conditionId is not null)
        {
            await _supabase.PatchAsync(
                "rounds",
                new JsonObject { ["condition_id"] = conditionId.Value },
                new SupabaseQuery("rounds").Filter("id", "eq", RequiredPositiveLong(existingSession, "round_id")),
                SupabaseRole.Service,
                cancellationToken);
            updatedSession["condition_id"] = conditionId.Value;
        }

        return updatedSession;
    }

    private static bool IsMissingSessionConditionColumn(string resourceName, JsonObject patch, SupabaseApiException exception)
    {
        return string.Equals(resourceName, "sessions", StringComparison.OrdinalIgnoreCase) &&
            patch.ContainsKey("condition_id") &&
            exception.ResponseBody.Contains("condition_id", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<JsonObject> UpsertRoundImageAsync(
        string roundId,
        ImportRoundImagePayload payload,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(roundId, "round_id");
        await EnsureReferenceAsync("rounds", roundId, "round_id", cancellationToken);
        var image = RoundImagePayloadParser.Parse(payload)
            ?? throw Invalid("ROUND_IMAGE_REQUIRED", "Round image payload is required.");
        var storagePath = _imageStorage is null
            ? null
            : await _imageStorage.UploadAsync(image, cancellationToken);
        var values = new JsonObject
        {
            ["round_id"] = roundId,
            ["image_data"] = storagePath is null ? $"\\x{Convert.ToHexString(image.Bytes)}" : null,
            ["storage_path"] = storagePath,
            ["content_type"] = "image/png",
            ["file_name"] = image.FileName?.Trim(),
            ["alt_text"] = image.AltText?.Trim(),
            ["width_px"] = image.WidthPx,
            ["height_px"] = image.HeightPx
        };

        return (await _supabase.PostAsync(
            "round_images",
            values,
            SupabaseRole.Service,
            onConflict: "round_id",
            cancellationToken: cancellationToken)).First();
    }

    public async Task<JsonObject> DeleteRoundAsync(
        string roundId,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(roundId, "round_id");
        await EnsureReferenceAsync("rounds", roundId, "round_id", cancellationToken);

        JsonNode? result;
        try
        {
            result = await _supabase.RpcAsync(
                "delete_round_cascade",
                new JsonObject
                {
                    ["target_round_id"] = long.Parse(roundId, CultureInfo.InvariantCulture)
                },
                SupabaseRole.Service,
                cancellationToken);
        }
        catch (SupabaseApiException exception) when (
            exception.StatusCode == StatusCodes.Status404NotFound &&
            exception.ResponseBody.Contains("PGRST202", StringComparison.OrdinalIgnoreCase) &&
            exception.ResponseBody.Contains("delete_round_cascade", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "ROUND_DELETE_MIGRATION_REQUIRED",
                "Round deletion is not installed in Supabase. Apply Database/20260730_delete_round_cascade.sql and reload the PostgREST schema cache.");
        }

        return result as JsonObject
            ?? throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "ROUND_DELETE_FAILED",
                "The database did not return a round deletion summary.");
    }

    private async Task ValidateReferencesAsync(string resourceName, JsonObject candidate, CancellationToken cancellationToken)
    {
        switch (resourceName.ToLowerInvariant())
        {
			case "rounds":
				await EnsureReferenceAsync("tracks", RequiredPositiveLong(candidate, "track_id"), "track_id", cancellationToken);
				if (OptionalLong(candidate, "competition_rank_id") is { } competitionRankId)
				{
					await EnsureReferenceAsync("competition_ranks", competitionRankId.ToString(CultureInfo.InvariantCulture), "competition_rank_id", cancellationToken);
				}
				await EnsureReferenceAsync("competition_levels", RequiredPositiveLong(candidate, "competition_level_id"), "competition_level_id", cancellationToken);
                await EnsureReferenceAsync("conditions", RequiredPositiveLong(candidate, "condition_id"), "condition_id", cancellationToken);
                break;
            case "sessions":
            {
                var roundId = RequiredPositiveLong(candidate, "round_id");
                var categoryId = RequiredPositiveLong(candidate, "category_id");
                await EnsureReferenceAsync("rounds", roundId, "round_id", cancellationToken);
                await EnsureReferenceAsync("categories", categoryId, "category_id", cancellationToken);
                if (OptionalLong(candidate, "condition_id") is { } conditionId)
                {
                    await EnsureReferenceAsync("conditions", conditionId.ToString(CultureInfo.InvariantCulture), "condition_id", cancellationToken);
                }
                await EnsureRoundCategoryAsync(roundId, categoryId, cancellationToken);
                break;
            }
            case "results":
                await EnsureReferenceAsync("sessions", RequiredPositiveLong(candidate, "session_id"), "session_id", cancellationToken);
                await EnsureReferenceAsync("riders", RequiredPositiveLong(candidate, "rider_id"), "rider_id", cancellationToken);
                if (OptionalLong(candidate, "team_id") is { } teamId)
                {
                    await EnsureReferenceAsync("teams", teamId.ToString(CultureInfo.InvariantCulture), "team_id", cancellationToken);
                }
                break;
            case "time-splits":
                await EnsureReferenceAsync("results", RequiredPositiveLong(candidate, "result_id"), "result_id", cancellationToken);
                break;
            case "points-systems":
                await EnsureReferenceAsync("categories", RequiredPositiveLong(candidate, "category_id"), "category_id", cancellationToken);
                await EnsureReferenceAsync("competition_levels", RequiredPositiveLong(candidate, "competition_level_id"), "competition_level_id", cancellationToken);
                break;
            case "point-system-stages":
                await EnsureReferenceAsync("points_systems", RequiredPositiveLong(candidate, "point_system_id"), "point_system_id", cancellationToken);
                break;
            case "point-rules":
                await EnsureReferenceAsync("point_system_stages", RequiredPositiveLong(candidate, "point_system_stage_id"), "point_system_stage_id", cancellationToken);
                break;
            case "points-system-positions":
                await EnsureReferenceAsync("points_systems", RequiredPositiveLong(candidate, "points_system_id"), "points_system_id", cancellationToken);
                break;
            case "result-points":
                await EnsureReferenceAsync("results", RequiredPositiveLong(candidate, "result_id"), "result_id", cancellationToken);
                await EnsureReferenceAsync("points_systems", RequiredPositiveLong(candidate, "points_system_id"), "points_system_id", cancellationToken);
                if (OptionalLong(candidate, "point_system_stage_id") is { } stageId)
                {
                    await EnsureReferenceAsync("point_system_stages", stageId.ToString(CultureInfo.InvariantCulture), "point_system_stage_id", cancellationToken);
                }
                if (OptionalLong(candidate, "point_rule_id") is { } ruleId)
                {
                    await EnsureReferenceAsync("point_rules", ruleId.ToString(CultureInfo.InvariantCulture), "point_rule_id", cancellationToken);
                }
                break;
        }
    }

    private async Task EnsureRoundCategoryAsync(string roundId, string categoryId, CancellationToken cancellationToken)
    {
        var existing = await _supabase.GetSingleAsync(
            new SupabaseQuery("round_categories")
                .Select("round_id,category_id")
                .Filter("round_id", "eq", roundId)
                .Filter("category_id", "eq", categoryId),
            SupabaseRole.Service,
            cancellationToken);
        if (existing is not null)
        {
            return;
        }

        await _supabase.PostAsync(
            "round_categories",
            new JsonObject { ["round_id"] = roundId, ["category_id"] = categoryId },
            SupabaseRole.Service,
            cancellationToken: cancellationToken);
    }

    private async Task EnsureReferenceAsync(string table, string id, string field, CancellationToken cancellationToken)
    {
        var referenced = await _supabase.GetSingleAsync(
            new SupabaseQuery(table).Select("id").Filter("id", "eq", id),
            SupabaseRole.Service,
            cancellationToken);
        if (referenced is null)
        {
            throw Invalid("ADMIN_REFERENCE_NOT_FOUND", $"Referenced {field} '{id}' was not found.");
        }
    }

    private static void NormalizePatch(string resourceName, JsonObject patch)
    {
        var trimFields = resourceName.ToLowerInvariant() switch
        {
            "tracks" => new[] { "name", "venue", "country_code" },
            "conditions" => new[] { "name" },
            "competition-classes" => new[] { "name", "code", "description" },
            "categories" => new[] { "name", "code" },
            "rounds" => new[] { "context_name", "start_date", "end_date", "status" },
            "sessions" => new[] { "name", "status", "session_type", "points_session_type_override", "session_date" },
            "riders" => new[] { "uci_code", "last_name", "first_name", "nation", "birth_date", "profile_image_url", "profile_image_updated_at" },
            "teams" => new[] { "name", "code" },
            "results" => new[] { "status" },
            "points-series" => new[] { "code", "name" },
            "points-systems" => new[] { "code", "name", "discipline", "source_url", "notes" },
            "point-system-stages" => new[] { "session_type" },
            _ => []
        };

        foreach (var field in trimFields)
        {
            if (TryString(patch, field, out var value))
            {
                patch[field] = value.Trim();
            }
        }

        foreach (var field in new[] { "country_code", "nation", "code", "uci_code" })
        {
            if (TryString(patch, field, out var value))
            {
                patch[field] = value.Trim().ToUpperInvariant();
            }
        }

        if (TryString(patch, "session_type", out var sessionType))
        {
            patch["session_type"] = DomainQueryService.NormalizeSessionType(sessionType);
        }

        if (TryString(patch, "points_session_type_override", out var pointsSessionTypeOverride))
        {
            patch["points_session_type_override"] = DomainQueryService.NormalizeSessionType(pointsSessionTypeOverride);
        }

        if (resourceName is "rounds" && TryString(patch, "status", out var roundStatus))
        {
            patch["status"] = roundStatus.Trim().ToLowerInvariant();
        }

        if (resourceName is "competition-classes" or "points-systems" && TryString(patch, "code", out var lowerCode))
        {
            patch["code"] = lowerCode.Trim().ToLowerInvariant();
        }
    }

    private static void ValidateCandidate(string resourceName, JsonObject candidate)
    {
        switch (resourceName.ToLowerInvariant())
        {
            case "tracks":
                RequiredText(candidate, "name");
                RequiredText(candidate, "venue");
                ValidateCountryCode(candidate, "country_code");
                ValidateOptionalLong(candidate, "length_m", value => value > 0, "must be greater than zero");
                ValidateOptionalDecimal(candidate, "avg_slope", value => value is >= -100 and <= 100, "must be between -100 and 100");
                break;
            case "conditions":
                RequiredText(candidate, "name");
                ValidateOptionalBoolean(candidate, "is_dry");
                break;
            case "competition-classes":
                RequiredText(candidate, "code");
                RequiredText(candidate, "name");
                RequiredPositiveLong(candidate, "priority");
                OptionalNonBlankText(candidate, "description");
                RequiredBoolean(candidate, "is_active");
                break;
            case "categories":
            {
                RequiredText(candidate, "name");
                OptionalNonBlankText(candidate, "code");
                ValidateOptionalLong(candidate, "min_age", value => value >= 0, "must be non-negative");
                ValidateOptionalLong(candidate, "max_age", value => value >= 0, "must be non-negative");
                var minAge = OptionalLong(candidate, "min_age");
                var maxAge = OptionalLong(candidate, "max_age");
                if (minAge is not null && maxAge is not null && minAge > maxAge)
                {
                    throw Invalid("INVALID_ADMIN_PAYLOAD", "min_age must not be greater than max_age.");
                }
                break;
            }
			case "rounds":
				RequiredText(candidate, "context_name");
				RequiredPositiveLong(candidate, "track_id");
				ValidateOptionalLong(candidate, "competition_rank_id", value => value > 0, "must be greater than zero");
				RequiredPositiveLong(candidate, "competition_level_id");
                RequiredBoolean(candidate, "is_season_final_round");
                RequiredPositiveLong(candidate, "condition_id");
                ValidateOptionalLong(candidate, "season_year", value => value is >= 1990 and <= 2100, "must be between 1990 and 2100");
                ValidateOptionalLong(candidate, "round_number", value => value > 0, "must be greater than zero");
                ValidateOptionalDateTime(candidate, "start_date");
                ValidateOptionalDateTime(candidate, "end_date");
                ValidateDateOrder(candidate, "start_date", "end_date");
                var roundStatus = RequiredText(candidate, "status");
				if (roundStatus is not ("scheduled" or "confirmed" or "live" or "completed" or "cancelled"))
				{
					throw Invalid("INVALID_ADMIN_PAYLOAD", "round status must be scheduled, confirmed, live, completed, or cancelled.");
                }
                break;
            case "sessions":
            {
                RequiredText(candidate, "name");
                RequiredText(candidate, "status");
                RequiredPositiveLong(candidate, "round_id");
                RequiredPositiveLong(candidate, "category_id");
                ValidateOptionalLong(candidate, "condition_id", value => value > 0, "must be greater than zero");
                var sessionType = RequiredText(candidate, "session_type");
                if (!SessionTypes.Contains(sessionType))
                {
                    throw Invalid("INVALID_SESSION_TYPE", $"session_type must be one of: {string.Join(", ", SessionTypes.OrderBy(value => value, StringComparer.Ordinal))}.");
                }
                var pointsSessionTypeOverride = OptionalText(candidate, "points_session_type_override");
                if (pointsSessionTypeOverride is not null && !SessionTypes.Contains(pointsSessionTypeOverride))
                {
                    throw Invalid("INVALID_SESSION_TYPE", $"points_session_type_override must be one of: {string.Join(", ", SessionTypes.OrderBy(value => value, StringComparer.Ordinal))}.");
                }
                ValidateOptionalDate(candidate, "session_date");
                break;
            }
            case "riders":
                RequiredPositiveLong(candidate, "uci_id");
                OptionalNonBlankText(candidate, "uci_code");
                OptionalNonBlankText(candidate, "last_name");
                OptionalNonBlankText(candidate, "first_name");
                ValidateCountryCode(candidate, "nation");
                ValidateOptionalDate(candidate, "birth_date");
                RequiredBoolean(candidate, "is_protected");
                RequiredBoolean(candidate, "has_injury");
                ValidateOptionalDateTime(candidate, "profile_image_updated_at");
                if (OptionalText(candidate, "profile_image_url") is { } profileImageUrl &&
                    (!Uri.TryCreate(profileImageUrl, UriKind.Absolute, out var profileUri) || profileUri.Scheme != Uri.UriSchemeHttps || profileImageUrl.Length > 2048))
                {
                    throw Invalid("INVALID_ADMIN_PAYLOAD", "profile_image_url must be an HTTPS URL no longer than 2048 characters.");
                }
                break;
            case "teams":
            {
                RequiredText(candidate, "name");
                var code = RequiredText(candidate, "code");
                if (!TeamCodePattern.IsMatch(code))
                {
                    throw Invalid("INVALID_ADMIN_PAYLOAD", "code must contain exactly three uppercase ASCII letters or digits.");
                }
                break;
            }
            case "results":
                RequiredPositiveLong(candidate, "race_nr");
                RequiredPositiveLong(candidate, "session_id");
                RequiredPositiveLong(candidate, "rider_id");
                RequiredText(candidate, "status");
                ValidateOptionalLong(candidate, "completed_distance", value => value >= 0, "must be non-negative");
                ValidateOptionalLong(candidate, "position", value => value > 0, "must be greater than zero");
                ValidateOptionalLong(candidate, "race_time_ms", value => value >= 0, "must be non-negative");
                ValidateOptionalDecimal(candidate, "top_speed", value => value >= 0, "must be non-negative");
                ValidateOptionalBoolean(candidate, "is_classified", required: true);
                ValidateOptionalLong(candidate, "team_id", value => value > 0, "must be greater than zero");
                break;
            case "time-splits":
                RequiredPositiveLong(candidate, "result_id");
                ValidateRequiredLong(candidate, "split_index", value => value >= 0, "must be non-negative");
                ValidateRequiredLong(candidate, "race_time", value => value >= 0, "must be non-negative");
                ValidateOptionalDecimal(candidate, "speed", value => value >= 0, "must be non-negative");
                break;
            case "points-series":
                RequiredText(candidate, "code");
                RequiredText(candidate, "name");
                break;
            case "points-systems":
            {
                RequiredText(candidate, "code");
                RequiredText(candidate, "name");
                RequiredPositiveLong(candidate, "competition_level_id");
                RequiredText(candidate, "discipline");
                RequiredPositiveLong(candidate, "category_id");
                ValidateRequiredLong(candidate, "valid_from_season", value => value is >= 1990 and <= 2100, "must be between 1990 and 2100");
                ValidateRequiredLong(candidate, "valid_to_season", value => value is >= 1990 and <= 2100, "must be between 1990 and 2100");
                if (OptionalLong(candidate, "valid_from_season")!.Value > OptionalLong(candidate, "valid_to_season")!.Value)
                {
                    throw Invalid("INVALID_ADMIN_PAYLOAD", "valid_from_season must not be greater than valid_to_season.");
                }
                break;
            }
            case "point-system-stages":
                RequiredPositiveLong(candidate, "point_system_id");
                RequiredText(candidate, "session_type");
                RequiredBoolean(candidate, "awards_points");
                RequiredBoolean(candidate, "awards_points_on_season_final_round");
                break;
            case "point-rules":
            {
                RequiredPositiveLong(candidate, "point_system_stage_id");
                var from = RequiredPositiveLong(candidate, "position_from");
                var to = OptionalLong(candidate, "position_to");
                if (to is not null && to < long.Parse(from, CultureInfo.InvariantCulture))
                {
                    throw Invalid("INVALID_ADMIN_PAYLOAD", "position_to must be null or at least position_from.");
                }
                ValidateRequiredLong(candidate, "points", value => value >= 0, "must be non-negative");
                break;
            }
            case "points-system-positions":
                RequiredPositiveLong(candidate, "points_system_id");
                RequiredPositiveLong(candidate, "position");
                ValidateRequiredDecimal(candidate, "points", value => value >= 0, "must be non-negative");
                break;
            case "result-points":
                RequiredPositiveLong(candidate, "result_id");
                RequiredPositiveLong(candidate, "points_system_id");
                ValidateOptionalLong(candidate, "position_used", value => value > 0, "must be greater than zero");
                ValidateOptionalLong(candidate, "point_system_stage_id", value => value > 0, "must be greater than zero");
                ValidateOptionalLong(candidate, "point_rule_id", value => value > 0, "must be greater than zero");
                ValidateRequiredDecimal(candidate, "points_awarded", value => value >= 0, "must be non-negative");
                break;
        }
    }

    private static AdminResourceDefinition ResolveResource(string resourceName)
    {
        if (!Resources.TryGetValue(resourceName, out var definition))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "ADMIN_RESOURCE_NOT_EDITABLE", $"Admin resource '{resourceName}' is not editable.");
        }

        return definition;
    }

    private static AdminResourceDefinition Definition(string table, params string[] fields)
    {
        return new AdminResourceDefinition(
            table,
            new HashSet<string>(fields, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static AdminResourceDefinition MappedDefinition(
        string table,
        IReadOnlyDictionary<string, string> fieldMap,
        params string[] fields)
    {
        return new AdminResourceDefinition(
            table,
            new HashSet<string>(fields, StringComparer.Ordinal),
            fieldMap);
    }

    private static JsonObject ToDatabasePatch(
        AdminResourceDefinition definition,
        JsonObject requestedPatch)
    {
        var patch = new JsonObject();
        foreach (var property in requestedPatch)
        {
            var databaseField = definition.FieldMap.TryGetValue(property.Key, out var mapped)
                ? mapped
                : property.Key;
            patch[databaseField] = property.Value?.DeepClone();
        }

        return patch;
    }

    private static JsonObject ToApiResource(string resourceName, JsonObject source)
    {
        var response = source.DeepClone().AsObject();
        if (resourceName is "rounds" or "points-systems")
        {
            response["competition_class_id"] = response["competition_level_id"]?.DeepClone();
            response.Remove("competition_level_id");
            response.Remove("competition_rank_id");
        }

        return response;
    }

    private static void ValidateCountryCode(JsonObject candidate, string field)
    {
        var value = OptionalText(candidate, field);
        if (value is not null && !CountryCodePattern.IsMatch(value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must contain two or three uppercase ASCII letters.");
        }
    }

    private static void ValidateDateOrder(JsonObject candidate, string firstField, string secondField)
    {
        var first = OptionalDateTime(candidate, firstField);
        var second = OptionalDateTime(candidate, secondField);
        if (first is not null && second is not null && first > second)
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{firstField} must not be later than {secondField}.");
        }
    }

    private static void ValidateOptionalDate(JsonObject candidate, string field)
    {
        _ = OptionalDate(candidate, field);
    }

    private static void ValidateOptionalDateTime(JsonObject candidate, string field)
    {
        _ = OptionalDateTime(candidate, field);
    }

    private static DateOnly? OptionalDate(JsonObject candidate, string field)
    {
        var value = OptionalText(candidate, field);
        if (value is null)
        {
            return null;
        }

        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be a valid date in YYYY-MM-DD format.");
        }

        return date;
    }

    private static DateTimeOffset? OptionalDateTime(JsonObject candidate, string field)
    {
        var value = OptionalText(candidate, field);
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be a valid ISO 8601 date and time.");
        }

        return dateTime;
    }

    private static string RequiredText(JsonObject candidate, string field)
    {
        var value = OptionalText(candidate, field);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} is required and must not be blank.");
        }

        return value;
    }

    private static void OptionalNonBlankText(JsonObject candidate, string field)
    {
        if (candidate.TryGetPropertyValue(field, out var node) && node is not null && string.IsNullOrWhiteSpace(OptionalText(candidate, field)))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must not be blank.");
        }
    }

    private static string? OptionalText(JsonObject candidate, string field)
    {
        if (!candidate.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be a string or null.");
        }

        return node.GetValue<string>().Trim();
    }

    private static bool TryString(JsonObject candidate, string field, out string value)
    {
        value = string.Empty;
        return candidate.TryGetPropertyValue(field, out var node) &&
            node is not null &&
            node.GetValueKind() == JsonValueKind.String &&
            (value = node.GetValue<string>()) is not null;
    }

    private static string RequiredPositiveLong(JsonObject candidate, string field)
    {
        var value = OptionalLong(candidate, field);
        if (value is null or <= 0)
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} is required and must be greater than zero.");
        }

        return value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static long? OptionalLong(JsonObject candidate, string field)
    {
        if (!candidate.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be an integer or null.");
    }

    private static decimal? OptionalDecimal(JsonObject candidate, string field)
    {
        if (!candidate.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue;
        }

        if (decimal.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be a number or null.");
    }

    private static void ValidateOptionalLong(JsonObject candidate, string field, Func<long, bool> predicate, string message)
    {
        if (OptionalLong(candidate, field) is { } value && !predicate(value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} {message}.");
        }
    }

    private static void ValidateRequiredLong(JsonObject candidate, string field, Func<long, bool> predicate, string message)
    {
        var value = OptionalLong(candidate, field);
        if (value is null || !predicate(value.Value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} is required and {message}.");
        }
    }

    private static void ValidateOptionalDecimal(JsonObject candidate, string field, Func<decimal, bool> predicate, string message)
    {
        if (OptionalDecimal(candidate, field) is { } value && !predicate(value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} {message}.");
        }
    }

    private static void ValidateRequiredDecimal(JsonObject candidate, string field, Func<decimal, bool> predicate, string message)
    {
        var value = OptionalDecimal(candidate, field);
        if (value is null || !predicate(value.Value))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} is required and {message}.");
        }
    }

    private static void ValidateOptionalBoolean(JsonObject candidate, string field, bool required = false)
    {
        if (!candidate.TryGetPropertyValue(field, out var node) || node is null)
        {
            if (required)
            {
                throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} is required and must be a boolean.");
            }

            return;
        }

        if (node is not JsonValue value || !value.TryGetValue<bool>(out _))
        {
            throw Invalid("INVALID_ADMIN_PAYLOAD", $"{field} must be a boolean or null.");
        }
    }

    private static void RequiredBoolean(JsonObject candidate, string field)
    {
        ValidateOptionalBoolean(candidate, field, required: true);
    }

    private static void EnsurePositiveId(string id, string field)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw Invalid("INVALID_ADMIN_ID", $"{field} must be a positive integer.");
        }
    }

    private static ApiException Invalid(string code, string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, code, message);
    }

    private sealed record AdminResourceDefinition(
        string Table,
        HashSet<string> Fields,
        IReadOnlyDictionary<string, string> FieldMap);
}
