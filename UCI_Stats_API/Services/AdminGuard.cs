using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;

namespace UCI_Stats_API.Services;

public sealed class AdminGuard
{
    private readonly AdminOptions _options;
    private readonly ApiAccessOptions _apiAccess;

    public AdminGuard(
        IOptions<AdminOptions> options,
        IOptions<ApiAccessOptions> apiAccess)
    {
        _options = options.Value;
        _apiAccess = apiAccess.Value;
    }

    public void EnsureAuthorized(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "ADMIN_SECRET_MISSING", "Admin API secret is not configured.");
        }

        var token = BearerTokenValidator.ReadToken(
            request,
            "Missing admin authorization bearer token.");
        if (BearerTokenValidator.Matches(token, _options.ApiSecret))
        {
            return;
        }

        if (BearerTokenValidator.Matches(token, _apiAccess.ReadSecret))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Administrator authorization is required.");
        }

        throw new ApiException(
            StatusCodes.Status401Unauthorized,
            "UNAUTHORIZED",
            "Invalid admin authorization bearer token.");
    }
}
