using System.Net;
using System.Text;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class TissotTimingJsonFetcherTests
{
    [Fact]
    public async Task ConvertsPublicResultsPageToApiAndReturnsWorldChampionshipJson()
    {
        var handler = new CapturingHandler("""
            {
              "event": { "number": 4, "name": "Women Junior", "discipline": "DOWNHILL" },
              "results": []
            }
            """);
        var fetcher = new TissotTimingJsonFetcher(new HttpClient(handler));

        var result = await fetcher.FetchAsync(
            "https://www.tissottiming.com/2023/mtbdhiwch/event-4/phase-2/results",
            CancellationToken.None);

        Assert.Equal(
            "https://prod.server.tissottiming.com/competitions/mtbdhiwch2023/events/4/phases/2/results",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal(handler.RequestUri?.AbsoluteUri, result.ResolvedUrl);
        Assert.Equal("Women Junior", result.Json["event"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void RejectsNonTissotHost()
    {
        var exception = Assert.Throws<ApiException>(() =>
            TissotTimingJsonFetcher.ResolveApiUrl(
                "https://example.com/2023/mtbdhiwch/event-4/phase-2/results"));

        Assert.Equal("TISSOT_RESULT_URL_INVALID", exception.Code);
    }

    [Fact]
    public void AcceptsOfficialResultsApiUrl()
    {
        const string url =
            "https://prod.server.tissottiming.com/competitions/mtbdhiwch2023/events/1/phases/1/results";

        Assert.Equal(url, TissotTimingJsonFetcher.ResolveApiUrl(url));
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
