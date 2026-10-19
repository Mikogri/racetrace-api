using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public sealed partial class ChronoRaceLiveProxyService
{
    private readonly HttpClient _http;

    public ChronoRaceLiveProxyService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RaceTrace-Live/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<JsonNode?> GetCatalogAsync(string context, CancellationToken cancellationToken) =>
        GetAsync(context, null, cancellationToken);

    public Task<JsonNode?> GetResultsAsync(string context, string key, CancellationToken cancellationToken) =>
        GetAsync(context, key, cancellationToken);

    private async Task<JsonNode?> GetAsync(string context, string? key, CancellationToken cancellationToken)
    {
        if (!SafePart().IsMatch(context) || (key is not null && !SafePart().IsMatch(key)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_CHRONORACE_CONTEXT", "Invalid ChronoRace context or session key.");
        }

        var encodedContext = Uri.EscapeDataString(context);
        var url = key is null
            ? $"https://prod.chronorace.be/api/results/uci/dh/cms/{encodedContext}"
            : $"https://prod.chronorace.be/api/results/generic/uci/{encodedContext}/dh/{Uri.EscapeDataString(key)}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(StatusCodes.Status502BadGateway, "CHRONORACE_UNAVAILABLE", $"ChronoRace returned {(int)response.StatusCode}.");
        }

        var content = await ExternalJsonResponse.ReadAsync(response, cancellationToken);
        return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePart();
}
