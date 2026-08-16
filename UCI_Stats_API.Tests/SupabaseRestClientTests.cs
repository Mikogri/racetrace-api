using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;

namespace UCI_Stats_API.Tests;

public sealed class SupabaseRestClientTests
{
    [Fact]
    public async Task RpcAsyncAcceptsNoContentResponse()
    {
        using var http = new HttpClient(new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NoContent)));
        var client = new SupabaseRestClient(
            http,
            Options.Create(new SupabaseOptions
            {
                Url = "https://example.supabase.co",
                AnonKey = "anon",
                ServiceRoleKey = "service"
            }));

        var result = await client.RpcAsync(
            "refresh_data",
            new JsonObject(),
            SupabaseRole.Service);

        Assert.Null(result);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
