using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

/// <summary>Converts a bounded batch of legacy PNG rows; rerunning skips completed rows.</summary>
public sealed class RoundImageBackfillService(ISupabaseRestClient supabase, RoundImageStorageService storage)
{
    public async Task<JsonObject> ConvertBatchAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10)
            throw new ApiException(StatusCodes.Status400BadRequest, "ROUND_IMAGE_BATCH_LIMIT_INVALID", "Limit must be between 1 and 10.");

        var roundRows = (await supabase.GetAsync(new SupabaseQuery("round_images")
            .Select("round_id,image_data,storage_path,file_name,alt_text")
            .Filter("content_type", "eq", "image/png")
            .Order("round_id", false)
            .Limit(limit), role: SupabaseRole.Service, cancellationToken: cancellationToken)).Data;
        var venueRows = (await supabase.GetAsync(new SupabaseQuery("venue_images")
            .Select("id,storage_path,file_name,alt_text")
            .Filter("content_type", "eq", "image/png")
            .Order("id", false)
            .Limit(limit), role: SupabaseRole.Service, cancellationToken: cancellationToken)).Data;

        foreach (var row in roundRows)
        {
            var source = row.String("storage_path");
            var png = source is null
                ? DecodeInlineImage(row.String("image_data"))
                : await storage.DownloadAsync(source, cancellationToken);
            var webp = RoundImagePayloadParser.ConvertPng(png, row.String("file_name"), row.String("alt_text"));
            var path = await storage.UploadAsync(webp, cancellationToken);
            var updated = await supabase.PatchAsync("round_images", new JsonObject
            {
                ["image_data"] = null,
                ["storage_path"] = path,
                ["content_type"] = RoundImagePayloadParser.ContentType,
                ["file_name"] = webp.FileName,
                ["width_px"] = webp.WidthPx,
                ["height_px"] = webp.HeightPx
            }, new SupabaseQuery("round_images").Filter("round_id", "eq", row.String("round_id")),
                SupabaseRole.Service, cancellationToken);
            if (updated.Count != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "ROUND_IMAGE_BACKFILL_CONFLICT", "Round image changed during conversion.");
        }

        foreach (var row in venueRows)
        {
            var id = row.String("id")!;
            var source = row.String("storage_path")!;
            var png = await storage.DownloadAsync(source, cancellationToken);
            var webp = RoundImagePayloadParser.ConvertPng(png, row.String("file_name"), row.String("alt_text"));
            var path = await storage.UploadAsync(webp, cancellationToken, $"venues/{id}");
            var updated = await supabase.PatchAsync("venue_images", new JsonObject
            {
                ["storage_path"] = path,
                ["content_type"] = RoundImagePayloadParser.ContentType,
                ["file_name"] = webp.FileName
            }, new SupabaseQuery("venue_images").Filter("id", "eq", id),
                SupabaseRole.Service, cancellationToken);
            if (updated.Count != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "ROUND_IMAGE_BACKFILL_CONFLICT", "Venue image changed during conversion.");
        }

        return new JsonObject
        {
            ["round_images_converted"] = roundRows.Count,
            ["venue_images_converted"] = venueRows.Count,
            ["more_may_remain"] = roundRows.Count == limit || venueRows.Count == limit
        };
    }

    private static byte[] DecodeInlineImage(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new ApiException(StatusCodes.Status500InternalServerError, "ROUND_IMAGE_INVALID_DATA", "Stored round image is empty.");
        try
        {
            return data.StartsWith("\\x", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromHexString(data[2..])
                : Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            throw new ApiException(StatusCodes.Status500InternalServerError, "ROUND_IMAGE_INVALID_DATA", "Stored round image is invalid.");
        }
    }
}
