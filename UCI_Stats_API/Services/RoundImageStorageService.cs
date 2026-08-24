using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;

namespace UCI_Stats_API.Services;

public sealed class RoundImageStorageService
{
    public const string BucketName = "round-images";

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    public RoundImageStorageService(HttpClient http, IOptions<SupabaseOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    internal async Task<string> UploadAsync(PreparedRoundImage image, CancellationToken cancellationToken)
    {
        EnsureConfigured(requireServiceKey: true);
        var hash = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant();
        var storagePath = $"images/{hash}.png";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.Url.TrimEnd('/')}/storage/v1/object/{BucketName}/{storagePath}");
        request.Headers.TryAddWithoutValidation("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");
        request.Headers.TryAddWithoutValidation("cache-control", "31536000");
        request.Content = new ByteArrayContent(image.Bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "ROUND_IMAGE_STORAGE_UPLOAD_FAILED",
                $"Supabase Storage rejected the round image with status {(int)response.StatusCode}: {body}");
        }

        return storagePath;
    }

    public string PublicUrl(string storagePath)
    {
        EnsureConfigured(requireServiceKey: false);
        var encodedPath = string.Join('/', storagePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"{_options.Url.TrimEnd('/')}/storage/v1/object/public/{BucketName}/{encodedPath}";
    }

    private void EnsureConfigured(bool requireServiceKey)
    {
        if (string.IsNullOrWhiteSpace(_options.Url) ||
            requireServiceKey && string.IsNullOrWhiteSpace(_options.ServiceRoleKey))
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "SUPABASE_STORAGE_NOT_CONFIGURED",
                "Supabase Storage URL or server-side service role key is not configured.");
        }
    }
}
