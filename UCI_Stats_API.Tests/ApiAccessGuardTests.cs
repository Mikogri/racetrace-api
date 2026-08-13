using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class ApiAccessGuardTests
{
    [Fact]
    public void ValidReadBearerSecretIsAccepted()
    {
        var guard = Guard("read-secret", "admin-secret");
        var request = Request("Bearer read-secret");

        guard.EnsureAuthorized(request);
    }

    [Fact]
    public void ValidAdminBearerSecretIsAcceptedForReadAccess()
    {
        var guard = Guard("read-secret", "admin-secret");
        var request = Request("Bearer admin-secret");

        guard.EnsureAuthorized(request);
    }

    [Fact]
    public void MissingBearerSecretIsRejected()
    {
        var guard = Guard("read-secret", "admin-secret");
        var request = new DefaultHttpContext().Request;

        var exception = Assert.Throws<ApiException>(() =>
            guard.EnsureAuthorized(request));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal("UNAUTHORIZED", exception.Code);
    }

    [Theory]
    [InlineData("Basic read-secret")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong-secret")]
    public void InvalidAuthorizationHeaderIsRejected(string header)
    {
        var guard = Guard("read-secret", "admin-secret");
        var request = Request(header);

        var exception = Assert.Throws<ApiException>(() =>
            guard.EnsureAuthorized(request));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal("UNAUTHORIZED", exception.Code);
    }

    [Fact]
    public void MissingServerSecretsReturnsServiceUnavailable()
    {
        var guard = Guard(readSecret: null, adminSecret: null);
        var request = Request("Bearer any-token");

        var exception = Assert.Throws<ApiException>(() =>
            guard.EnsureAuthorized(request));

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            exception.StatusCode);
        Assert.Equal("API_ACCESS_SECRET_MISSING", exception.Code);
    }

    private static ApiAccessGuard Guard(
        string? readSecret,
        string? adminSecret)
    {
        return new ApiAccessGuard(
            Options.Create(new ApiAccessOptions { ReadSecret = readSecret }),
            Options.Create(new AdminOptions { ApiSecret = adminSecret }));
    }

    private static HttpRequest Request(string authorization)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = authorization;
        return request;
    }
}
