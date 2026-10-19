using System.Net;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Infrastructure;

// Enabled only for the server-side Cloudflare gateway, which owns the read credential.
internal sealed class GatewayRequestMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IOptions<ApiAccessOptions> options)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            BearerTokenValidator.Matches(authorization[7..].Trim(), options.Value.ReadSecret) &&
            IPAddress.TryParse(context.Request.Headers["X-RaceTrace-Client-IP"].ToString(), out var address))
        {
            context.Connection.RemoteIpAddress = address;
            context.Request.Scheme = "https";
        }
        return next(context);
    }
}
