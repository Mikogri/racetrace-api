using System.Net;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ImportSourceSecurityTests
{
    [Theory]
    [InlineData("http://prod.chronorace.be/api/results/test")]
    [InlineData("https://127.0.0.1/results")]
    [InlineData("https://prod.chronorace.be.attacker.example/results")]
    [InlineData("https://prod.chronorace.be:8443/results")]
    [InlineData("https://user:password@prod.chronorace.be/results")]
    [InlineData("https://example.com/#/uci/event/2026/dh/1/res")]
    public async Task ChronoRaceRejectsUntrustedUrlsBeforeSending(string url)
    {
        var handler = new SourceHandler();
        var fetcher = new ChronoRaceJsonFetcher(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fetcher.FetchAsync(url, CancellationToken.None)
        );
        Assert.Equal("CHRONORACE_URL_INVALID", error.Code);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task OfficialChronoRaceSourceStillWorks()
    {
        var handler = new SourceHandler();
        var result = await new ChronoRaceJsonFetcher(new HttpClient(handler)).FetchAsync(
            "https://prod.chronorace.be/api/results/test",
            CancellationToken.None
        );
        Assert.NotNull(result.Json["Riders"]);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(
        "https://user:password@www.tissottiming.com/2023/mtbdhiwch/event-4/phase-2/results"
    )]
    [InlineData(
        "https://prod.server.tissottiming.com:8443/competitions/mtbdhiwch2023/events/1/phases/1/results"
    )]
    public void TissotRejectsCredentialsAndNonstandardPorts(string url)
    {
        Assert.Throws<ApiException>(() => TissotTimingJsonFetcher.ResolveApiUrl(url));
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"Riders":{},"Results":[]}"""),
                }
            );
        }
    }
}
