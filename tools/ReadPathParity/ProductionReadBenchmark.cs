using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

internal static class ProductionReadBenchmark
{
    internal static async Task Run(IConfiguration configuration, string[] args)
    {
        var output = args.Contains("--output") ? args[Array.IndexOf(args, "--output") + 1] : "artifacts/production-read-path";
        Directory.CreateDirectory(output);
        var secret = Environment.GetEnvironmentVariable("API_READ_SECRET") ?? configuration["ApiAccess:ReadSecret"];
        var samples = new List<object>();
        var routes = new[]
        {
            ("rounds", "/api/v1/rounds?season=2026&pageSize=100"),
            ("ranking", "/api/v1/ranking?season=2026&category=elite_men&pageSize=5&sortBy=rating"),
            ("rider", "/api/v1/riders/18/overview?season=2026&category=elite_men"),
            ("compare", "/api/v1/compare?season=2026&category=elite_men&riderIds=18,183")
        };
        foreach (var direct in new[] { false, true })
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false
            }) { Timeout = TimeSpan.FromSeconds(45) };
            if (direct && !string.IsNullOrWhiteSpace(secret)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            var origin = direct ? "https://api.racetracemtb.com" : "https://racetracemtb.com";
            foreach (var (name, path) in routes)
            for (var sample = 0; sample < 3; sample++)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    using var response = await client.GetAsync(origin + path, HttpCompletionOption.ResponseHeadersRead);
                    var headersMs = watch.Elapsed.TotalMilliseconds;
                    var body = await response.Content.ReadAsByteArrayAsync();
                    var totalMs = watch.Elapsed.TotalMilliseconds;
                    samples.Add(new { direct, name, sample, status = (int)response.StatusCode, headersMs, totalMs,
                        decodedBytes = body.Length, serverTiming = response.Headers.TryGetValues("Server-Timing", out var timings) ? string.Join(',', timings) : null });
                    Console.WriteLine($"{(direct ? "direct" : "cloudflare")}/{name}/{sample}: {(int)response.StatusCode}, {totalMs:F1} ms");
                    // Invalid/missing local credentials do not prove a production availability problem.
                    if (direct && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable) break;
                }
                catch (HttpRequestException error) { samples.Add(new { direct, name, sample, error = error.GetType().Name }); }
                catch (TaskCanceledException) { samples.Add(new { direct, name, sample, error = "timeout" }); }
                await File.WriteAllTextAsync(Path.Combine(output, "http.json"), JsonSerializer.Serialize(new
                {
                    measuredAt = DateTimeOffset.UtcNow, scope = "Production GET only; uncontrolled caches. Header/body/network timing, not isolated SQL or browser rendering. Local secrets are never saved.", samples
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "http.json"), JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow,
            scope = "Production GET only; uncontrolled caches. 401 direct samples mean unavailable credentials, not application latency. No SQL/browser timing inferred.",
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
