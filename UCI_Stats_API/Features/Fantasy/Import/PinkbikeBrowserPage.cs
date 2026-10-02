using System.Net;
using Microsoft.Playwright;

namespace UCI_Stats_API.Features.Fantasy.Import;

/// <summary>Isolated headless browser; no personal profile, stealth plugins or challenge solving.</summary>
public class PinkbikeBrowserPage
{
    public virtual async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 10_000 });
            cancellationToken.ThrowIfCancellationRequested();
            var page = await browser.NewPageAsync(new() { AcceptDownloads = false });
            var response = await page.GotoAsync(PinkbikeFantasyParser.SourceUrl,
                new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 }).WaitAsync(cancellationToken);
            if (response is null || !response.Ok)
                throw new HttpRequestException("Pinkbike browser navigation failed.", null,
                    response is null ? null : (HttpStatusCode)response.Status);
            var title = await page.TitleAsync().WaitAsync(cancellationToken);
            if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Pinkbike requires browser verification.", null, HttpStatusCode.Forbidden);
            // The existing parser validates embedded JSON, season, prices and catalogue completeness.
            return await page.ContentAsync().WaitAsync(cancellationToken);
        }
        catch (PlaywrightException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("Could not load the Pinkbike catalogue in Chromium. Check browser installation and connectivity.", exception);
        }
    }
}
