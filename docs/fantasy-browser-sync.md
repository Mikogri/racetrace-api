# Fantasy browser price synchronization

Automatic and admin-triggered synchronization first download the official Pinkbike athlete page over HTTP. If downloading or initial catalogue validation fails, they retry in an isolated headless Chromium browser. HTTP has at most 10 seconds (and at most one third of the shared sync budget), leaving time for the fallback. Cancellation of the whole operation never starts a fallback. Mapping/storage failures do not retry acquisition.

Both paths use the existing parser, validation and identity mapping. Successful snapshots record `http` or `headless-browser`; failure of both paths preserves the last successful snapshot, without pretending it is freshly imported.

The existing `Fantasy:ScheduledSyncEnabled` opt-in and six-hour synchronization schedule are unchanged. Ordinary page reads never launch a browser. No personal browser profile, CAPTCHA solver or stealth settings are used. HTTP 403 / verification pages are failures, not valid catalogues.

## Deployment

After building the API, install the browser for the account running the service:

```powershell
dotnet build UCI_Stats_API/UCI_Stats_API.csproj -c Release
pwsh UCI_Stats_API/bin/Release/net10.0/playwright.ps1 install chromium
```

On Linux install required system dependencies as part of deployment (`install --with-deps chromium`). Repeat installation after upgrading Playwright; published binaries alone do not include Chromium. Ensure the service account can access the browser installation. See https://playwright.dev/dotnet/docs/browsers.

Browser automation does not guarantee access when Pinkbike blocks automation. Such failures remain visible in Fantasy warnings and server logs. The authenticated saved-HTML import remains available. Do not relabel an old snapshot as a new price import.

## Manual import

Save the public Pinkbike athlete catalogue as HTML with its embedded athlete JSON, then send the file body to `POST /api/admin/fantasy/sync-html` with `Content-Type: text/html` and the existing administrator authentication. The size limit is 8 MB. This is an explicit admin operation, never an automatic fallback to a local file.

The endpoint uses the current UTC year and associates the snapshot with the next eligible round. It validates the catalogue/season/prices, rejects an unexpected rider-count drop greater than 25%, maps rider identities and saves a versioned snapshot tagged `admin-saved-public-html`. Invalid input leaves previous data intact. A shared lock prevents simultaneous automatic and manual imports.

Unlike `/sync?prepare=true`, `/sync-html` does not rebuild recommendations immediately. To rebuild after import, call authenticated `POST /api/admin/fantasy/prepare`. This endpoint expects the athlete-page HTML, not a CSV or screenshot. Validation checks structure and values, not cryptographic provenance: administrators must supply a fresh, unmodified official page.
