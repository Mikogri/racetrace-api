using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace UCI_Stats_API.PodiumBacktest;

// Credentials are sent only to Supabase; neither headers nor URLs are persisted.
internal sealed class ReadOnlyCacheHandler(string directory, bool offline) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get &&
            !(request.Method == HttpMethod.Post && path.EndsWith("/rpc/get_ranking_data", StringComparison.Ordinal)))
            throw new InvalidOperationException("Backtest permits GET and the read-only get_ranking_data RPC only.");

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Method} {request.RequestUri} {body}")));
        var file = Path.Combine(directory, key + ".json");
        if (File.Exists(file))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(await File.ReadAllTextAsync(file, cancellationToken), Encoding.UTF8, "application/json") };
        if (offline) throw new InvalidOperationException($"Offline cache missing: {key} ({path}).");

        using var response = await base.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Historical data request failed: {(int)response.StatusCode} ({path}).");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, content, cancellationToken);
        return new HttpResponseMessage(response.StatusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
