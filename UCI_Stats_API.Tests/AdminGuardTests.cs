using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class AdminGuardTests
{
    [Fact]
    public void ValidBearerSecretIsAccepted()
    {
        var guard = Guard("correct-secret");
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer correct-secret";

        guard.EnsureAuthorized(request);
    }

    [Fact]
    public void MissingBearerSecretIsRejected()
    {
        var guard = Guard("correct-secret");
        var request = new DefaultHttpContext().Request;

        var exception = Assert.Throws<ApiException>(() => guard.EnsureAuthorized(request));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal("UNAUTHORIZED", exception.Code);
    }

    [Fact]
    public void InvalidBearerSecretIsRejected()
    {
        var guard = Guard("correct-secret");
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer wrong-secret";

        var exception = Assert.Throws<ApiException>(() => guard.EnsureAuthorized(request));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal("UNAUTHORIZED", exception.Code);
    }

    [Fact]
    public void ValidReadSecretCannotAccessAdminEndpoint()
    {
        var guard = Guard("admin-secret", "read-secret");
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer read-secret";

        var exception = Assert.Throws<ApiException>(() => guard.EnsureAuthorized(request));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        Assert.Equal("FORBIDDEN", exception.Code);
    }

    private static AdminGuard Guard(
        string secret,
        string readSecret = "read-secret")
    {
        return new AdminGuard(
            Options.Create(new AdminOptions { ApiSecret = secret }),
            Options.Create(new ApiAccessOptions { ReadSecret = readSecret }));
    }
}
