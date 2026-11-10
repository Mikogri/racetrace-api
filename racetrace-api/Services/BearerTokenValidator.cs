using System.Security.Cryptography;
using System.Text;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Services;

internal static class BearerTokenValidator
{
    public static string ReadToken(HttpRequest request, string missingTokenMessage)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "UNAUTHORIZED",
                missingTokenMessage
            );
        }

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0)
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "UNAUTHORIZED",
                missingTokenMessage
            );
        }

        return token;
    }

    public static bool Matches(string token, string? expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSecret))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(expectedSecret);
        var actual = Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
