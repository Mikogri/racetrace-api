using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;

namespace UCI_Stats_API.Infrastructure;

public sealed class SupabaseRestClient : ISupabaseRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly SupabaseOptions _options;

    public SupabaseRestClient(HttpClient httpClient, IOptions<SupabaseOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRestUri(query.Table, query.Parameters));
        AddAuthHeaders(request, role);

        if (count)
        {
            request.Headers.TryAddWithoutValidation("Prefer", "count=exact");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, content);

        var total = ParseTotal(response);
        return new SupabaseQueryResult(ParseArray(content), total);
    }

    public async Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
    {
        query.Limit(1);
        var result = await GetAsync(query, role: role, cancellationToken: cancellationToken);
        return result.Data.FirstOrDefault();
    }

    public async Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(onConflict))
        {
            parameters.Add(new("on_conflict", onConflict));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRestUri(table, parameters));
        AddAuthHeaders(request, role);
        request.Headers.TryAddWithoutValidation("Prefer", string.IsNullOrWhiteSpace(onConflict)
            ? "return=representation"
            : "resolution=merge-duplicates,return=representation");
        request.Content = JsonContent(payload);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, content);
        return ParseArray(content);
    }

    public async Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, BuildRestUri(table, query.Parameters));
        AddAuthHeaders(request, role);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        request.Content = JsonContent(payload);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, content);
        return ParseArray(content);
    }

    public async Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildRestUri(table, query.Parameters));
        AddAuthHeaders(request, role);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, content);
    }

    public async Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRpcUri(functionName));
        AddAuthHeaders(request, role);
        request.Content = JsonContent(payload);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, errorContent);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
            response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonNode.ParseAsync(content, cancellationToken: cancellationToken);
    }

    private Uri BuildRestUri(string table, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        EnsureConfigured();

        var builder = new StringBuilder($"{_options.Url.TrimEnd('/')}/rest/v1/{Uri.EscapeDataString(table)}");
        AppendQuery(builder, parameters);
        return new Uri(builder.ToString());
    }

    private Uri BuildRpcUri(string functionName)
    {
        EnsureConfigured();
        return new Uri($"{_options.Url.TrimEnd('/')}/rest/v1/rpc/{Uri.EscapeDataString(functionName)}");
    }

    private static void AppendQuery(StringBuilder builder, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        builder.Append('?');
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(parameters[i].Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameters[i].Value));
        }
    }

    private void AddAuthHeaders(HttpRequestMessage request, SupabaseRole role)
    {
        var key = role == SupabaseRole.Service ? _options.ServiceRoleKey : _options.AnonKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "SUPABASE_KEY_MISSING", role == SupabaseRole.Service
                ? "Supabase service role key is not configured."
                : "Supabase anon key is not configured.");
        }

        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "SUPABASE_URL_MISSING", "Supabase URL is not configured.");
        }
    }

    private static StringContent JsonContent(JsonNode payload)
    {
        return new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string content)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseApiException((int)response.StatusCode, content);
        }
    }

    private static int? ParseTotal(HttpResponseMessage response)
    {
        if (!response.Content.Headers.TryGetValues("Content-Range", out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        var slashIndex = value?.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex is null or < 0)
        {
            return null;
        }

        return int.TryParse(value![(slashIndex.Value + 1)..], out var total) ? total : null;
    }

    private static IReadOnlyList<JsonObject> ParseArray(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var node = JsonNode.Parse(content);
        if (node is not JsonArray array)
        {
            return node is JsonObject jsonObject ? [jsonObject] : [];
        }

        return array.OfType<JsonObject>().ToArray();
    }
}
