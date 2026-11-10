using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

internal sealed class AdminRoundOperations(
    ISupabaseRestClient supabase,
    RoundImageStorageService? imageStorage
)
{
    private readonly ISupabaseRestClient _supabase = supabase;
    private readonly RoundImageStorageService? _imageStorage = imageStorage;

    internal async Task<JsonObject> UpsertRoundImageAsync(
        string roundId,
        ImportRoundImagePayload payload,
        CancellationToken cancellationToken
    )
    {
        var image =
            RoundImagePayloadParser.Parse(payload)
            ?? throw new ApiException(
                StatusCodes.Status400BadRequest,
                "ROUND_IMAGE_REQUIRED",
                "Round image payload is required."
            );
        var storagePath = _imageStorage is null
            ? null
            : await _imageStorage.UploadAsync(image, cancellationToken);
        var values = new JsonObject
        {
            ["round_id"] = roundId,
            ["image_data"] = storagePath is null ? $"\\x{Convert.ToHexString(image.Bytes)}" : null,
            ["storage_path"] = storagePath,
            ["content_type"] = RoundImagePayloadParser.ContentType,
            ["file_name"] = image.FileName?.Trim(),
            ["alt_text"] = image.AltText?.Trim(),
            ["width_px"] = image.WidthPx,
            ["height_px"] = image.HeightPx,
        };

        return (
            await _supabase.PostAsync(
                "round_images",
                values,
                SupabaseRole.Service,
                onConflict: "round_id",
                cancellationToken: cancellationToken
            )
        ).First();
    }

    internal async Task<JsonObject> DeleteRoundAsync(
        string roundId,
        CancellationToken cancellationToken
    )
    {
        JsonNode? result;
        try
        {
            result = await _supabase.RpcAsync(
                "delete_round_cascade",
                new JsonObject
                {
                    ["target_round_id"] = long.Parse(roundId, CultureInfo.InvariantCulture),
                },
                SupabaseRole.Service,
                cancellationToken
            );
        }
        catch (SupabaseApiException exception)
            when (exception.StatusCode == StatusCodes.Status404NotFound
                && exception.ResponseBody.Contains("PGRST202", StringComparison.OrdinalIgnoreCase)
                && exception.ResponseBody.Contains(
                    "delete_round_cascade",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "ROUND_DELETE_MIGRATION_REQUIRED",
                "Round deletion is not installed in Supabase. Apply Database/20260730_delete_round_cascade.sql and reload the PostgREST schema cache."
            );
        }

        return result as JsonObject
            ?? throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "ROUND_DELETE_FAILED",
                "The database did not return a round deletion summary."
            );
    }
}
