using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public sealed partial class TissotTimingJsonFetcher
{
    private const string PageHost = "www.tissottiming.com";
    private const string ApiHost = "prod.server.tissottiming.com";
    private readonly HttpClient _http;

    public TissotTimingJsonFetcher(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UCI-Stats-API/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<TissotTimingJsonFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var apiUrl = ResolveApiUrl(url);

        try
        {
            using var response = await _http.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "TISSOT_RESULT_REQUEST_FAILED",
                    $"Tissot Timing returned {(int)response.StatusCode} for '{apiUrl}'.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonNode.Parse(body) as JsonObject;
            if (json is null || !UciWorldChampionshipJsonAdapter.IsResultJson(json))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "TISSOT_RESULT_JSON_INVALID",
                    $"'{apiUrl}' did not return a supported World Championship result document.");
            }

            return new TissotTimingJsonFetchResult(json, apiUrl);
        }
        catch (JsonException)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TISSOT_RESULT_JSON_INVALID",
                $"'{apiUrl}' did not return valid JSON.");
        }
        catch (HttpRequestException exception)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TISSOT_RESULT_REQUEST_FAILED",
                $"Tissot Timing request failed: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "TISSOT_RESULT_REQUEST_TIMEOUT",
                "Tissot Timing request timed out.");
        }
    }

    public static string ResolveApiUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidUrl();
        }

        if (string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            var apiMatch = ApiPathPattern().Match(uri.AbsolutePath);
            if (!apiMatch.Success)
            {
                throw InvalidUrl();
            }

            return $"https://{ApiHost}{apiMatch.Value.TrimEnd('/')}";
        }

        if (!string.Equals(uri.Host, PageHost, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidUrl();
        }

        var pageMatch = PagePathPattern().Match(uri.AbsolutePath);
        if (!pageMatch.Success)
        {
            throw InvalidUrl();
        }

        var year = pageMatch.Groups["year"].Value;
        var competition = pageMatch.Groups["competition"].Value.ToLowerInvariant();
        var eventNumber = pageMatch.Groups["event"].Value;
        var phaseNumber = pageMatch.Groups["phase"].Value;

        return $"https://{ApiHost}/competitions/{competition}{year}/events/{eventNumber}/phases/{phaseNumber}/results";
    }

    private static ApiException InvalidUrl() =>
        new(
            StatusCodes.Status400BadRequest,
            "TISSOT_RESULT_URL_INVALID",
            "result_url must be a Tissot Timing results page or results API URL.");

    [GeneratedRegex(
        "^/(?<year>\\d{4})/(?<competition>[a-z0-9_-]+)/event-(?<event>\\d+)/phase-(?<phase>\\d+)/results/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PagePathPattern();

    [GeneratedRegex(
        "^/competitions/[a-z0-9_-]+/events/\\d+/phases/\\d+/results/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiPathPattern();
}

public sealed record TissotTimingJsonFetchResult(JsonObject Json, string ResolvedUrl);
