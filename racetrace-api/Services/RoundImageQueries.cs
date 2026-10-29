using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class RoundImageQueries(ISupabaseRestClient supabase, RoundImageStorageService? imageStorage)
{
    internal void AttachEmbeddedImageMetadata(JsonObject round)
    {
        var image = round.Object("round_images") ?? round.Array("round_images")?.OfType<JsonObject>().FirstOrDefault();
        var track = round.Object("tracks");
        var path = image?.String("storage_path") ?? track?.Object("venue_images")?.String("storage_path");
        var exists = image is not null || !string.IsNullOrWhiteSpace(track?.String("venue_image_id"));
        round["round_image_url"] = !string.IsNullOrWhiteSpace(path) && _imageStorage is not null
            ? StoragePublicUrl(path)
            : exists ? $"/api/v1/rounds/{round.String("id")}/image" : null;
    }

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
