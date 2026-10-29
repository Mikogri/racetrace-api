using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class FilterOptionsTests
{
    [Fact]
    public async Task ReadsOneAnonymousRpcWithOnlyDictionaryFields()
    {
        using var handler = new Handler("""
            {"seasons":[2026,2025],"tracks":[{"id":7,"name":"Track","venue":"Venue","countryCode":"POL"}],"conditions":[{"id":2,"name":"Wet"}],"competitionClasses":[{"id":9,"name":"World Cup"}]}
            """);
        var result = await Service(handler).GetAsync(default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://example.supabase.co/rest/v1/rpc/get_filter_options", handler.Url);
        Assert.Equal("anon", handler.Key);
        Assert.Equal(new[] { 2026, 2025 }, result.Seasons);
        Assert.Equal("POL", Assert.Single(result.Tracks).CountryCode);
        Assert.Equal("Wet", Assert.Single(result.Conditions).Name);
        Assert.Equal("World Cup", Assert.Single(result.CompetitionClasses).Name);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"seasons\":[],\"tracks\":null,\"conditions\":[],\"competitionClasses\":[]}")]
    public async Task RejectsMissingDictionariesRatherThanReturningFalseSuccess(string payload)
    {
        using var handler = new Handler(payload);
        await Assert.ThrowsAsync<JsonException>(() => Service(handler).GetAsync(default));
    }

    [Fact]
    public async Task DoesNotHideUpstreamFailure()
    {
        using var handler = new Handler("unavailable", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<SupabaseApiException>(() => Service(handler).GetAsync(default));
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var handler = new Handler("{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(handler).GetAsync(cancellation.Token));
    }

    private static FilterOptionsService Service(Handler handler) => new(new SupabaseRestClient(new HttpClient(handler),
        Options.Create(new SupabaseOptions { Url = "https://example.supabase.co", AnonKey = "anon", ServiceRoleKey = "service" })));

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Key { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Key = request.Headers.GetValues("apikey").Single();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
