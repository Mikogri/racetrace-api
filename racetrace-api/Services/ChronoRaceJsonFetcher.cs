using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public sealed class ChronoRaceJsonFetcher
{
    private static readonly HashSet<string> KnownRouteDisciplines = new(StringComparer.OrdinalIgnoreCase)
    {
        "dh",
        "bmx",
        "bmxfs",
        "edr",
        "track",
        "trials",
        "xco",
        "xcc"
    };

    private readonly HttpClient _http;

    public ChronoRaceJsonFetcher(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("racetrace-api/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ChronoRaceJsonFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "CHRONORACE_URL_REQUIRED", "chronorace_url is required.");
        }

        var attempted = new List<string>();
        var errors = new List<string>();
        foreach (var candidate in BuildCandidateUrls(url.Trim()))
        {
            if (!attempted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                attempted.Add(candidate);
            }
        }

        foreach (var candidate in attempted)
        {
            try
            {
                using var response = await _http.GetAsync(candidate, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{candidate} returned {(int)response.StatusCode}.");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = ParseChronoRaceJson(body);
                if (json is null)
                {
                    errors.Add($"{candidate} did not return ChronoRace result JSON.");
                    continue;
                }

                return new ChronoRaceJsonFetchResult(json, candidate, errors);
            }
            catch (JsonException)
            {
                errors.Add($"{candidate} did not return valid JSON.");
            }
            catch (HttpRequestException exception)
            {
                errors.Add($"{candidate} request failed: {exception.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{candidate} request timed out.");
            }
        }

        throw new ApiException(
            StatusCodes.Status400BadRequest,
            "CHRONORACE_JSON_NOT_FOUND",
            $"Could not resolve ChronoRace JSON from '{url}'. Tried: {string.Join(", ", attempted)}");
    }

    public static bool IsChronoRaceResultJson(JsonObject json)
    {
        return json.Object("Riders") is not null && json.Array("Results") is not null;
    }

    private static JsonObject? ParseChronoRaceJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var json = JsonNode.Parse(body) as JsonObject;
        return json is not null && IsChronoRaceResultJson(json) ? json : null;
    }

    private static IEnumerable<string> BuildCandidateUrls(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            yield return url;
            yield break;
        }

        var parsedRoute = TryParseUciResultRoute(uri);
        if (parsedRoute is not null)
        {
            var context = Uri.EscapeDataString(parsedRoute.Value.Context);
            var discipline = Uri.EscapeDataString(parsedRoute.Value.Discipline.ToLowerInvariant());
            var key = Uri.EscapeDataString(parsedRoute.Value.Key);
            yield return $"https://prod.chronorace.be/api/results/generic/uci/{context}/{discipline}?key={key}";
        }

        yield return url;
    }

    private static UciResultRoute? TryParseUciResultRoute(Uri uri)
    {
        var route = uri.Fragment;
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        route = route.TrimStart('#');
        var queryIndex = route.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            route = route[..queryIndex];
        }

        var segments = route
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var eventIndex = Array.FindIndex(segments, segment => string.Equals(segment, "event", StringComparison.OrdinalIgnoreCase));
        if (eventIndex < 0 || eventIndex + 1 >= segments.Length)
        {
            return null;
        }

        var context = segments[eventIndex + 1];
        for (var index = eventIndex + 2; index + 1 < segments.Length; index++)
        {
            if (KnownRouteDisciplines.Contains(segments[index]))
            {
                return new UciResultRoute(context, segments[index], segments[index + 1]);
            }
        }

        return null;
    }

    private readonly record struct UciResultRoute(string Context, string Discipline, string Key);
}

public sealed record ChronoRaceJsonFetchResult(
    JsonObject Json,
    string ResolvedUrl,
    IReadOnlyList<string> Warnings);
