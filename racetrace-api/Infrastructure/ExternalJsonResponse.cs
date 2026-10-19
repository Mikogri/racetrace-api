using System.Text;

namespace RaceTrace.Api.Infrastructure;

internal static class ExternalJsonResponse
{
    internal const int MaximumBytes = 16 * 1024 * 1024;

    internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumBytes) throw TooLarge();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (body.Length + count > MaximumBytes) throw TooLarge();
            body.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length).TrimStart('\uFEFF');
    }

    private static ApiException TooLarge() => new(StatusCodes.Status502BadGateway,
        "UPSTREAM_RESPONSE_TOO_LARGE", "The timing provider response exceeds the size limit.");
}
