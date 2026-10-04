using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class SupabaseRestClientTransportTests
{
    [Theory]
    [InlineData("GET", "anon", "count=exact", false)]
    [InlineData("POST", "service", "resolution=merge-duplicates,return=representation", true)]
    [InlineData("PATCH", "service", "return=representation", true)]
    [InlineData("DELETE", "service", null, false)]
    public async Task SendsOneRequestWithUnchangedHeadersQueryAndBody(
        string method, string key, string? prefer, bool hasBody)
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "[{\"id\":7}]");
        using var http = new HttpClient(handler);

        await SendAsync(CreateClient(http), method);

        Assert.Equal(1, handler.CallCount);
        var request = Assert.IsType<RequestSnapshot>(handler.Request);
        Assert.Equal(method, request.Method);
        Assert.Equal(key, request.ApiKey);
        Assert.Equal($"Bearer {key}", request.Authorization);
        Assert.Equal(prefer, request.Prefer);
        Assert.Equal(
            "https://example.supabase.co/rest/v1/riders?" + (method == "POST"
                ? "on_conflict=id%2Cname"
                : "select=id%2Cname&id=eq.7&order=id.asc&limit=30&offset=30"),
            request.Uri);
        Assert.Equal(hasBody ? "{\"name\":\"Rider\",\"score\":null}" : null, request.Body);
        Assert.Equal(hasBody ? "application/json; charset=utf-8" : null, request.ContentType);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PreservesFailureStatusAndRawBodyWithoutParsingOrRetry(string method)
    {
        const string body = "upstream failure: not JSON\n";
        using var handler = new RecordingHandler(HttpStatusCode.Conflict, body);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<SupabaseApiException>(() => SendAsync(CreateClient(http), method));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(body, error.ResponseBody);
        Assert.Equal("Supabase request failed with status 409.", error.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AcceptsAnEmptySuccessfulResponse(string method)
    {
        using var handler = new RecordingHandler(HttpStatusCode.NoContent, "");
        using var http = new HttpClient(handler);

        await SendAsync(CreateClient(http), method);

        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PropagatesCancellationToTheTransport(string method)
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SendAsync(CreateClient(http), method, cancellation.Token));

        Assert.Equal(1, handler.CallCount);
        Assert.True(handler.RequestCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task GetPreservesRowsNullsAndTotal()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "[{\"id\":7,\"name\":null}]");
        using var http = new HttpClient(handler);

        var result = await CreateClient(http).GetAsync(CreateQuery(), count: true);

        Assert.Equal(65, result.Total);
        Assert.Equal("{\"id\":7,\"name\":null}", Assert.Single(result.Data).ToJsonString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    public async Task MutationsReturnTheUpstreamRepresentation(string method)
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "[{\"id\":7,\"name\":null}]");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var rows = method == "POST"
            ? await client.PostAsync("riders", new JsonObject(), SupabaseRole.Service)
            : await client.PatchAsync("riders", new JsonObject(), CreateQuery(), SupabaseRole.Service);

        Assert.Equal("{\"id\":7,\"name\":null}", Assert.Single(rows).ToJsonString());
        Assert.Equal("return=representation", handler.Request?.Prefer);
    }

    private static SupabaseQuery CreateQuery() => new SupabaseQuery("riders")
        .Select("id,name").Filter("id", "eq", 7).Order("id", false).Limit(30).Offset(30);

    private static SupabaseRestClient CreateClient(HttpClient http) => new(http,
        Options.Create(new SupabaseOptions
        {
            Url = "https://example.supabase.co/", AnonKey = "anon", ServiceRoleKey = "service"
        }));

    private static Task SendAsync(SupabaseRestClient client, string method, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["name"] = "Rider", ["score"] = null };
        return method switch
        {
            "GET" => client.GetAsync(CreateQuery(), count: true, cancellationToken: cancellationToken),
            "POST" => client.PostAsync("riders", payload, SupabaseRole.Service, "id,name", cancellationToken),
            "PATCH" => client.PatchAsync("riders", payload, CreateQuery(), SupabaseRole.Service, cancellationToken),
            "DELETE" => client.DeleteAsync("riders", CreateQuery(), SupabaseRole.Service, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }

    private sealed record RequestSnapshot(
        string Method, string Uri, string? ApiKey, string? Authorization,
        string? Prefer, string? Body, string? ContentType);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public CancellationToken RequestCancellation { get; private set; }
        public RequestSnapshot? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestCancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Request = new RequestSnapshot(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.GetValues("apikey").Single(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString());
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "0-0/65");
            return response;
        }
    }
}
