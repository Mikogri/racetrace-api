using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class RoundImageQueries(ISupabaseRestClient supabase, RoundImageStorageService? imageStorage)
{
    private const int RoundImageBatchSize = 100;
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly RoundImageStorageService? _imageStorage = imageStorage;

    public async Task<RoundImageFile?> GetRoundImageAsync(
        long roundId,
        CancellationToken cancellationToken)
    {
        var image = await _supabase.GetSingleAsync(
            new SupabaseQuery("round_images")
                .Select("round_id,image_data,storage_path,content_type,file_name,alt_text")
                .Filter("round_id", "eq", roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken: cancellationToken);

        if (image?.String("storage_path") is { } roundStoragePath && !string.IsNullOrWhiteSpace(roundStoragePath))
        {
            return new RoundImageFile(
                null,
                image.String("content_type") ?? "image/png",
                image.String("file_name"),
                image.String("alt_text"),
                StoragePublicUrl(roundStoragePath));
        }

        var rawImageData = image?.String("image_data");
        if (!string.IsNullOrWhiteSpace(rawImageData))
        {
            return new RoundImageFile(
                DecodeImageData(rawImageData),
                image?.String("content_type") ?? "image/png",
                image?.String("file_name"),
                image?.String("alt_text"));
        }

        var round = await _supabase.GetSingleAsync(
            new SupabaseQuery("rounds")
                .Select("track_id,tracks(venue_image_id,venue_images(storage_path,content_type,file_name,alt_text))")
                .Filter("id", "eq", roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken: cancellationToken);
        var venueImage = round?.Object("tracks")?.Object("venue_images");
        var venueStoragePath = venueImage?.String("storage_path");
        return string.IsNullOrWhiteSpace(venueStoragePath)
            ? null
            : new RoundImageFile(
                null,
                venueImage?.String("content_type") ?? "image/png",
                venueImage?.String("file_name"),
                venueImage?.String("alt_text"),
                StoragePublicUrl(venueStoragePath));
    }

    public async Task AttachRoundImageMetadataAsync(
        IReadOnlyList<JsonObject> rounds,
        CancellationToken cancellationToken)
    {
        var roundIds = rounds
            .Select(round => round.String("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var imagesByRoundId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in roundIds.Chunk(RoundImageBatchSize))
        {
            var response = await _supabase.GetAsync(
                new SupabaseQuery("round_images")
                    .Select("round_id")
                    .In("round_id", batch),
                cancellationToken: cancellationToken);

            foreach (var image in response.Data)
            {
                var roundId = image.String("round_id");
                if (!string.IsNullOrWhiteSpace(roundId))
                {
                    imagesByRoundId[roundId] = image;
                }
            }
        }

        foreach (var round in rounds)
        {
            var roundId = round.String("id");
            var image = !string.IsNullOrWhiteSpace(roundId) && imagesByRoundId.TryGetValue(roundId, out var value)
                ? value
                : null;

            var venueImageId = round.Object("tracks")?.String("venue_image_id");
            if (image is null && string.IsNullOrWhiteSpace(venueImageId))
            {
                round["has_round_image"] = false;
                round["round_image_id"] = null;
                round["round_image_url"] = null;
                continue;
            }

            round["has_round_image"] = true;
            round["round_image_id"] = image?["round_id"]?.DeepClone();
            round["round_image_url"] = $"/api/v1/rounds/{roundId}/image";
        }
    }

    private string StoragePublicUrl(string storagePath) =>
        _imageStorage?.PublicUrl(storagePath) ??
        throw new ApiException(
            StatusCodes.Status503ServiceUnavailable,
            "SUPABASE_STORAGE_NOT_CONFIGURED",
            "Supabase Storage is not configured for round images.");

    private static byte[] DecodeImageData(string rawImageData)
    {
        try
        {
            return rawImageData.StartsWith("\\x", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromHexString(rawImageData[2..])
                : Convert.FromBase64String(rawImageData);
        }
        catch (FormatException)
        {
            throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "ROUND_IMAGE_INVALID_DATA",
                "Round image data returned by the database is invalid.");
        }
    }
}
