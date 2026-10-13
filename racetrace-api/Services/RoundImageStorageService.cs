using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

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

    internal async Task<string> UploadAsync(PreparedRoundImage image, CancellationToken cancellationToken, string pathPrefix = "images")
    {
        EnsureConfigured(requireServiceKey: true);
        var hash = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant();
        var storagePath = $"{pathPrefix}/{hash}.webp";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.Url.TrimEnd('/')}/storage/v1/object/{BucketName}/{storagePath}");
        request.Headers.TryAddWithoutValidation("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");
        request.Headers.TryAddWithoutValidation("cache-control", "31536000");
        request.Content = new ByteArrayContent(image.Bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(RoundImagePayloadParser.ContentType);

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

    internal async Task<byte[]> DownloadAsync(string storagePath, CancellationToken cancellationToken)
    {
        EnsureConfigured(requireServiceKey: false);
        using var response = await _http.GetAsync(PublicUrl(storagePath), cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > RoundImagePayloadParser.MaximumImageBytes)
            throw new ApiException(StatusCodes.Status400BadRequest, "ROUND_IMAGE_TOO_LARGE", "Stored round image exceeds the import size limit.");
        return bytes;
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
