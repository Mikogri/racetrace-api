# Fantasy browser price synchronization

Automatic and admin-triggered synchronization first download the official Pinkbike athlete page over HTTP. If downloading or initial catalogue validation fails, they retry in an isolated headless Chromium browser. HTTP has at most 10 seconds (and at most one third of the shared sync budget), leaving time for the fallback. Cancellation of the whole operation never starts a fallback. Mapping/storage failures do not retry acquisition.

Both paths use the existing parser, validation and identity mapping. Successful snapshots record `http` or `headless-browser`; failure of both paths preserves the last successful snapshot, without pretending it is freshly imported.

The existing `Fantasy:ScheduledSyncEnabled` opt-in and six-hour synchronization schedule are unchanged. Ordinary page reads never launch a browser. No personal browser profile, CAPTCHA solver or stealth settings are used. HTTP 403 / verification pages are failures, not valid catalogues.

## Deployment

After building the API, install the browser for the account running the service:

```powershell
dotnet build racetrace-api/racetrace-api.csproj -c Release
pwsh racetrace-api/bin/Release/net10.0/playwright.ps1 install chromium
```

On Linux install required system dependencies as part of deployment (`install --with-deps chromium`). Repeat installation after upgrading Playwright; published binaries alone do not include Chromium. Ensure the service account can access the browser installation. See https://playwright.dev/dotnet/docs/browsers.

Browser automation does not guarantee access when Pinkbike blocks automation. Such failures remain visible in Fantasy warnings and server logs. The authenticated saved-HTML import remains available. Do not relabel an old snapshot as a new price import.

## Manual import

Save the public Pinkbike athlete catalogue as HTML with its embedded athlete JSON, then send the file body to `POST /api/admin/fantasy/sync-html` with `Content-Type: text/html` and the existing administrator authentication. The size limit is 8 MB. This is an explicit admin operation, never an automatic fallback to a local file.

The endpoint uses the current UTC year and associates the snapshot with the next eligible round. It validates the catalogue/season/prices, rejects an unexpected rider-count drop greater than 25%, maps rider identities and saves a versioned snapshot tagged `admin-saved-public-html`. Invalid input leaves previous data intact. A shared lock prevents simultaneous automatic and manual imports.

Unlike `/sync?prepare=true`, `/sync-html` does not rebuild recommendations immediately. To rebuild after import, call authenticated `POST /api/admin/fantasy/prepare`. This endpoint expects the athlete-page HTML, not a CSV or screenshot. Validation checks structure and values, not cryptographic provenance: administrators must supply a fresh, unmodified official page.

## Multiple API instances

Apply `supabase/migrations/20261010210124_fantasy_job_coordination.sql` explicitly, then enable `Fantasy:DistributedCoordinationEnabled` on **all** workers and readers. The default remains `false`, preserving existing single-instance filesystem storage. API startup never applies migrations.

Enabled mode uses service-role-only RPCs and shared PostgreSQL snapshots. Prices are serialized per season; preparation per round. UTC-aligned schedule IDs identify the same cycle across instances. A short acquisition transaction grants a monotonically increasing fencing token; heartbeats renew the default two-minute lease every 30 seconds. External HTTP/browser requests and calculations happen outside transactions. Lease verification, publication and successful completion share one transaction. Expired owners cannot publish, renew or fail a successor's run.

Completed cycles return their original result instead of fetching again. The three admin endpoints also accept an optional `Idempotency-Key` header (1–180 printable characters); reuse it to retry the same operation, and use a new key for a new import. Manual jobs use the same lease keys as scheduled jobs. Conflicts return 409; cancellation/lease loss preserves the last successful snapshot. Scheduled failures retry on the existing live-check interval, not in a tight loop. HTTP acquisition may still repeat after crashes; this is not an exactly-once guarantee for external requests.

Existing local history is **not automatically uploaded**. Before switching traffic, prepare a shared snapshot on a canary and verify reads; retain local files for rollback and explicitly migrate historical prices if historical salary backtests are required. Do not mix local/distributed storage modes behind one load balancer. Rolling back the flag resumes the retained local files, not newer database snapshots. Old prepared forecasts without a dependency fingerprint retain the previous validation rules only while both coherence and distributed flags are disabled; they are never assigned new provenance. Enabling either flag requires preparing them again; source race results do not need reimporting.

New prepared forecasts retain their dependency fingerprint, effective configuration hash (including fitted stage calibrations), actual computation timestamp, model version, latest calibration observation date, prediction timestamp and simulation seed/count. `/current` and `/recommendation` expose these once under `calculation` only for a valid versioned forecast. `FantasyModelConfiguration` holds the unchanged historical/current simulation counts and calibration search parameters. Static model-training dates are not fabricated.

`racetrace_jobs.leases` exposes expiry, heartbeat, status and last success; `racetrace_jobs.runs` retains attempts and completed results. These are private server-side tables. Job metrics use the bounded names `fantasy_sync` and `fantasy_prepare`; no IDs or scraped payloads are metric labels. The run ledger is retained for durable idempotency; deleting completed rows removes that guarantee for their run IDs.

## Local PostgreSQL checks

Use an isolated local PostgreSQL database named `racetrace_test_<name>`, with `anon`, `authenticated`, and `service_role` roles provisioned. Do not use production. The Python scripts reject remote hosts and non-test database names. `RACETRACE_TEST_PSQL` can point to a portable `psql.exe`; otherwise `psql` must be on PATH.

```powershell
$env:PGHOST = '127.0.0.1'
$env:PGPORT = '5432'
$env:PGUSER = 'postgres'
$env:PGDATABASE = 'racetrace_test_jobs'
python tools/test_fantasy_jobs.py

# Separate empty database; this script creates a representative source schema and applies the revision migration.
$env:PGDATABASE = 'racetrace_test_revisions'
python tools/test_calculation_revisions.py
```

The job check uses eight independent PostgreSQL sessions and covers takeover, heartbeat, stale-owner rejection, idempotent publication, completed-cycle suppression, failure preservation and historical cutoff reads. The revision check covers transactional/scoped invalidation, rollback, old/new parent relationships, metadata, cascade deletes and RLS. Application orchestration and existing Fantasy regressions run with `dotnet test racetrace-api.Tests`.

Revision atomicity follows PostgreSQL transaction boundaries. Existing multi-request REST imports are not one transaction: intermediate committed writes can be visible until the logical import finishes. Revision checks reject calculations spanning different observed revisions, but do not turn that importer into an atomic batch.
