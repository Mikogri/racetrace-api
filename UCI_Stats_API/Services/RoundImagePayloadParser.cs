using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

internal sealed record PreparedRoundImage(
    byte[] Bytes,
    string? FileName,
    string? AltText,
    int? WidthPx,
    int? HeightPx);

internal static class RoundImagePayloadParser
{
    internal const int MaximumImageBytes = 10 * 1024 * 1024;

    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47,
        0x0D, 0x0A, 0x1A, 0x0A
    ];

    public static PreparedRoundImage? Parse(ImportRoundImagePayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(payload.ImageBase64))
        {
            throw Invalid("ROUND_IMAGE_REQUIRED", "image.image_base64 is required when image is provided.");
        }

        ValidateMetadata(payload);
        var base64 = ExtractBase64(payload.ImageBase64.Trim());
        var maximumEncodedLength = ((MaximumImageBytes + 2L) / 3L) * 4L;
        if (base64.Length > maximumEncodedLength)
        {
            throw Invalid("ROUND_IMAGE_TOO_LARGE", $"Round image must not exceed {MaximumImageBytes / 1024 / 1024} MB.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw Invalid("ROUND_IMAGE_INVALID_BASE64", "image.image_base64 must contain valid Base64 data.");
        }

        if (bytes.Length > MaximumImageBytes)
        {
            throw Invalid("ROUND_IMAGE_TOO_LARGE", $"Round image must not exceed {MaximumImageBytes / 1024 / 1024} MB.");
        }

        if (bytes.Length < PngSignature.Length ||
            !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw Invalid("ROUND_IMAGE_NOT_PNG", "Round image must be a PNG file.");
        }

        return new PreparedRoundImage(
            bytes,
            payload.FileName,
            payload.AltText,
            payload.WidthPx,
            payload.HeightPx);
    }

    private static string ExtractBase64(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0 ||
            !string.Equals(
                value[..commaIndex],
                "data:image/png;base64",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("ROUND_IMAGE_NOT_PNG", "Only data:image/png;base64 image data URLs are supported.");
        }

        return value[(commaIndex + 1)..];
    }

    private static void ValidateMetadata(ImportRoundImagePayload payload)
    {
        if (payload.FileName is not null && string.IsNullOrWhiteSpace(payload.FileName))
        {
            throw Invalid("ROUND_IMAGE_FILE_NAME_INVALID", "image.file_name must not be blank.");
        }

        if (payload.AltText is not null && string.IsNullOrWhiteSpace(payload.AltText))
        {
            throw Invalid("ROUND_IMAGE_ALT_TEXT_INVALID", "image.alt_text must not be blank.");
        }

        if (payload.WidthPx is <= 0)
        {
            throw Invalid("ROUND_IMAGE_WIDTH_INVALID", "image.width_px must be greater than zero.");
        }

        if (payload.HeightPx is <= 0)
        {
            throw Invalid("ROUND_IMAGE_HEIGHT_INVALID", "image.height_px must be greater than zero.");
        }
    }

    private static ApiException Invalid(string code, string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, code, message);
    }
}
