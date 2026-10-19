using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class GatewayRequestTests
{
    [Theory]
    [InlineData("Bearer read-secret", "203.0.113.25", true)]
    [InlineData("Bearer wrong-secret", "203.0.113.25", false)]
    [InlineData("", "203.0.113.25", false)]
    [InlineData("Bearer read-secret", "203.0.113.25, 1.2.3.4", false)]
    [InlineData("Bearer read-secret", "", false)]
    public async Task OnlyAuthenticatedGatewayCanSupplyClientAddressAndHttps(string authorization, string address, bool trusted)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Headers.Authorization = authorization;
        context.Request.Headers["X-RaceTrace-Client-IP"] = address;
        var invoked = false;
        var middleware = new GatewayRequestMiddleware(_ => { invoked = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, Options.Create(new ApiAccessOptions { ReadSecret = "read-secret" }));
        Assert.True(invoked);
        Assert.Equal(trusted ? IPAddress.Parse(address) : IPAddress.Loopback, context.Connection.RemoteIpAddress);
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
    }
}
