using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed partial class RiderProfileImageImportService
{
    private const long MaximumFileBytes = 25 * 1024 * 1024;
    private const int LookupBatchSize = 100;

    private readonly ISupabaseRestClient _supabase;

    public RiderProfileImageImportService(ISupabaseRestClient supabase)
    {
        _supabase = supabase;
    }

    public async Task<RiderProfileImageImportResponse> ImportAsync(
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "PROFILE_IMAGE_FILES_REQUIRED",
                "At least one UCI MTB World Series JSON file is required.");
        }

        var warnings = new List<string>();
        var candidatesByUciId = new Dictionary<string, ImageCandidate>(StringComparer.OrdinalIgnoreCase);
        var unmatchedSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesProcessed = 0;
        var imagesFound = 0;
        var invalidUrls = 0;

        foreach (var file in files)
        {
            if (file.Length <= 0 || file.Length > MaximumFileBytes)
            {
                warnings.Add($"File '{file.FileName}' was skipped because its size is invalid or exceeds 25 MB.");
                continue;
            }

            JsonObject document;
            try
            {
                await using var stream = file.OpenReadStream();
                document = (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)) as JsonObject
                    ?? throw new JsonException("The JSON root must be an object.");
            }
            catch (JsonException exception)
            {
                warnings.Add($"File '{file.FileName}' was skipped because it is not valid JSON: {exception.Message}");
                continue;
            }

            filesProcessed++;
            var sourceResults = document.Array("results") ?? new JsonArray();
            foreach (var source in sourceResults.OfType<JsonObject>())
            {
                var rider = source.Object("rider");
                if (rider is null)
                {
                    continue;
                }

                var rawFrontUrls = FrontImageCandidates(rider)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .OrderByDescending(IsPreferredMediumVariant)
                    .ToArray();
                if (rawFrontUrls.Length == 0)
                {
                    continue;
                }

                var imageUrl = rawFrontUrls
                    .Select(value => TryNormalizeFrontImageUrl(value, out var normalized) ? normalized : null)
                    .FirstOrDefault(value => value is not null);
                if (imageUrl is null)
                {
                    invalidUrls++;
                    continue;
                }

                imagesFound++;
                var sourceKey = rider.String("id") ?? rider.String("slug") ?? source.String("riderName") ?? $"row-{imagesFound}";
                var uciId = NormalizeUciId(
                    rider.String("uciid") ??
                    rider.String("uciId") ??
                    source.String("riderUciId")) ?? ExtractUciId(imageUrl);
                if (uciId is null)
                {
                    unmatchedSourceKeys.Add(sourceKey);
                    continue;
                }

                candidatesByUciId[uciId] = new ImageCandidate(uciId, imageUrl, sourceKey);
            }
        }

        var existingByUciId = await FindExistingRidersAsync(candidatesByUciId.Keys, cancellationToken);
        var updated = 0;
        var unchanged = 0;
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var candidate in candidatesByUciId.Values)
        {
            if (!existingByUciId.TryGetValue(candidate.UciId, out var rider))
            {
                unmatchedSourceKeys.Add(candidate.SourceKey);
                continue;
            }

            if (string.Equals(rider.String("profile_image_url"), candidate.Url, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            await _supabase.PatchAsync(
                "riders",
                new JsonObject
                {
                    ["profile_image_url"] = candidate.Url,
                    ["profile_image_updated_at"] = updatedAt
                },
                new SupabaseQuery("riders").Filter("id", "eq", rider.String("id")),
                SupabaseRole.Service,
                cancellationToken);
            updated++;
        }

        return new RiderProfileImageImportResponse(
            filesProcessed,
            imagesFound,
            updated,
            unchanged,
            unmatchedSourceKeys.Count,
            invalidUrls,
            warnings);
    }

    private async Task<IReadOnlyDictionary<string, JsonObject>> FindExistingRidersAsync(
        IEnumerable<string> uciIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in uciIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(LookupBatchSize))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("riders")
                    .Select("id,uci_id,profile_image_url,profile_image_updated_at")
                    .In("uci_id", batch),
                role: SupabaseRole.Service,
                cancellationToken: cancellationToken);
            foreach (var rider in response.Data)
            {
                var uciId = rider.String("uci_id");
                if (!string.IsNullOrWhiteSpace(uciId))
                {
                    result[uciId] = rider;
                }
            }
        }

        return result;
    }

    private static IEnumerable<string?> FrontImageCandidates(JsonObject rider)
    {
        yield return rider.String("promoImageFront");
        yield return rider.String("worldChampionPromoImageFront");
        yield return rider.String("nationalChampionPromoImageFront");
    }

    internal static bool TryNormalizeFrontImageUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            value.Length > 2048 ||
            !uri.AbsolutePath.Contains("_F_", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsPreferredMediumVariant(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith("newMediumThumb.png", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ExtractUciId(string value)
    {
        var match = UciIdPattern().Match(value);
        return match.Success ? match.Value : null;
    }

    private static string? NormalizeUciId(string? value)
    {
        var normalized = value?.Trim();
        return normalized is not null && UciIdOnlyPattern().IsMatch(normalized)
            ? normalized
            : null;
    }

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex UciIdPattern();

    [GeneratedRegex(@"^\d{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex UciIdOnlyPattern();

    private sealed record ImageCandidate(string UciId, string Url, string SourceKey);
}
