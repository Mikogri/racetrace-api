using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

public sealed class ApiAccessGuard
{
    private readonly ApiAccessOptions _apiAccess;
    private readonly AdminOptions _admin;

    public ApiAccessGuard(
        IOptions<ApiAccessOptions> apiAccess,
        IOptions<AdminOptions> admin)
    {
        _apiAccess = apiAccess.Value;
        _admin = admin.Value;
    }

    public void EnsureAuthorized(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_apiAccess.ReadSecret) &&
            string.IsNullOrWhiteSpace(_admin.ApiSecret))
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "API_ACCESS_SECRET_MISSING",
                "API access secret is not configured.");
        }

        var token = BearerTokenValidator.ReadToken(
            request,
            "Missing API authorization bearer token.");
        if (!BearerTokenValidator.Matches(token, _apiAccess.ReadSecret) &&
            !BearerTokenValidator.Matches(token, _admin.ApiSecret))
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "UNAUTHORIZED",
                "Invalid API authorization bearer token.");
        }
    }
}
