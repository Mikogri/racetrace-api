using Microsoft.AspNetCore.Http;
using UCI_Stats_API.Infrastructure;

namespace UCI_Stats_API.Tests;

public sealed class ApiAccessMiddlewareTests
{
    [Theory]
    [InlineData("/api/v1/ranking")]
    [InlineData("/api/v1/rounds/12/image")]
    [InlineData("/api/admin/import/results/session")]
    [InlineData("/openapi/v1.json")]
    public void EveryApiPathRequiresAuthorization(string path)
    {
        var request = Request(path);

        Assert.True(ApiAccessMiddleware.RequiresAuthorization(request));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/favicon.ico")]
    [InlineData("/application/api-notes")]
    public void NonApiPathsAreNotCoveredByApiTokenMiddleware(string path)
    {
        var request = Request(path);

        Assert.False(ApiAccessMiddleware.RequiresAuthorization(request));
    }

    private static HttpRequest Request(string path)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        return request;
    }
}
