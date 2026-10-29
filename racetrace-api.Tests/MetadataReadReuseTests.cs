using System.Net;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class MetadataReadReuseTests
{
    [Theory]
    [InlineData(SupabaseRole.Anon, 1)]
    [InlineData(SupabaseRole.Service, 2)]
    public async Task IdenticalPublicLookupsShareOneReadButPrivilegedLookupsStayFresh(SupabaseRole role, int expected)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new SupabaseRestClient(http, Options.Create(new SupabaseOptions
            { Url = "https://test.invalid", AnonKey = "test", ServiceRoleKey = "test" }));
        var queries = new DomainQueryService(client);
        var ids = await Task.WhenAll(queries.FindCategoryIdByCodeAsync("EM", role), queries.FindCategoryIdByCodeAsync("EM", role));
        Assert.All(ids, id => Assert.Equal("1", id));
        Assert.Equal(expected, handler.Calls);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            return new(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":1,\"code\":\"EM\"}]") };
        }
    }
}
