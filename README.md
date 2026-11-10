# RaceTrace API

Backend RaceTrace udostępnia wyniki zawodów downhill, ranking, profile zawodników, porównania, analitykę rund i prognozy. ASP.NET Core na .NET 10 pobiera dane przez Supabase/PostgREST, a obliczenia sportowe wykonuje w C#. Aplikacja React znajduje się w sąsiednim repozytorium `racetrace-web-app`.

## Code formatting

C# source uses CSharpier, pinned in `dotnet-tools.json`, with default formatting rules. From the repository root:

```sh
dotnet tool restore
dotnet csharpier format .
dotnet csharpier check .
```

Install the recommended VS Code extension to format C# on save. Generated files, dependencies, build outputs and existing migrations are excluded in `.csharpierignore`. CI checks formatting without changing files.

## Uruchomienie

Z katalogu repozytorium:

```powershell
dotnet restore racetrace-api.slnx
dotnet user-secrets set "Supabase:Url" "<url>" --project racetrace-api
dotnet user-secrets set "Supabase:AnonKey" "<key>" --project racetrace-api
dotnet user-secrets set "ApiAccess:ReadSecret" "<read-secret>" --project racetrace-api
dotnet run --project racetrace-api --launch-profile http
```

Profil HTTP używa portu 5017. Operacje administracyjne wymagają osobnego `Admin:ApiSecret` i uprawnień serwerowych do Supabase. Sekretów nie zapisujemy w repozytorium. Uruchomienie hosta może uruchomić skonfigurowane zadania w tle; do pracy bez synchronizacji cen ustaw `Fantasy__ScheduledSyncEnabled=false`. Migracje bazy nie wykonują się automatycznie.

W `Production` użyj losowego sekretu odczytu długości co najmniej 32 znaków oraz innego, równie długiego sekretu administratora, jeśli potrzebujesz administracji. Niepoprawna konfiguracja zatrzymuje start. Publiczne `GET /health/live` sprawdza proces bez połączenia z bazą. Limity ruchu i ich konfigurację opisuje [API.md](docs/API.md).

Przed publicznym wdrożeniem pozostają: produkcyjne proxy z HTTPS i właściwą obsługą zaufanych adresów klienta, osobna ochrona administracji, audyt rzeczywistych grantów/RLS/RPC/Storage, backup i test odtworzenia, trwałość Fantasy oraz alerty. Vite nie zastępuje produkcyjnej bramy. Samo przejście testów lokalnych nie potwierdza bezpieczeństwa hostingu lub zdalnej bazy.

## Railway — GitHub deployment

Connect this repository and branch `main`, with the repository root as service root. `railway.json` selects the Dockerfile and `/health/live` deployment health check. Generate a Railway HTTPS domain; a purchased domain is not required. The start script listens on Railway's `PORT` (8080 locally). Use one replica initially; in-memory cache and rate limits are per process.

Set service variables in Railway, not in Git:

| Variable | Purpose |
| --- | --- |
| `API_READ_SECRET` | Random secret, at least 32 characters; same server-only value in Cloudflare Pages |
| `Supabase__Url` | Existing Supabase project HTTPS URL |
| `Supabase__AnonKey` | Existing read/Data API key |
| `Gateway__Enabled` | `true` for the authenticated Cloudflare gateway |
| `Supabase__ServiceRoleKey` | Server-only write key, only when configured jobs/admin operations require it |
| `ADMIN_API_SECRET` | Separate random admin secret, at least 32 characters; omit to keep administration unavailable |

The image sets `ASPNETCORE_ENVIRONMENT=Production` and `Fantasy__DataDirectory=/data/fantasy`. Mount a **persistent Railway volume at `/data`** before enabling Fantasy synchronization. The startup script prepares that fixed directory, then drops to `pwuser`. Existing local Fantasy snapshots are not automatically copied; transfer verified snapshots or run the existing authorized synchronization/preparation workflow. Migrations are never applied during container startup. Do not enable distributed coordination or cache-coherence flags without their required database migrations.

The official Playwright .NET image includes browsers/native dependencies and must match the `Microsoft.Playwright` package version (currently 1.63.0). It is larger than a minimal ASP.NET image, deliberately preserving the existing Pinkbike browser fallback and image import capabilities. Railway needs sufficient memory for Chromium; this configuration is not a guarantee that Pinkbike will accept requests from a datacenter IP. Check real imports after deployment. Set `Fantasy__ScheduledSyncEnabled=false` if synchronization should remain disabled during initial smoke tests.

`Gateway__Enabled` accepts `X-RaceTrace-Client-IP` and HTTPS only when the request carries the correct read bearer credential. The Pages Function obtains that address from Cloudflare's `CF-Connecting-IP`; arbitrary `X-Forwarded-For` headers are not trusted. This prevents all visitors sharing a single proxy-IP quota. Never expose the read key in browser code; admin access stays outside the Pages gateway. Railway terminates TLS; API traffic between its proxy and the container uses HTTP.

GitHub Actions runs Release tests, publish and a Linux Docker build without production secrets. Deployments are performed by Railway's GitHub integration, not that workflow; protect `main` with required CI checks. After deployment verify `/health/live`, an authorized read through Cloudflare, direct unauthenticated API rejection, images, Fantasy persistence after redeploy and provider logs. The health check is liveness, not a test of Supabase availability. Backups, live database permissions and provider abuse protection still require deployment-specific verification.

```powershell
dotnet build racetrace-api.slnx -c Release
dotnet test racetrace-api.Tests -c Release
```

## Dokumentacja

- [Architektura](docs/ARCHITECTURE.md) — moduły API i frontendu oraz przepływ żądania.
- [API](docs/API.md) — autoryzacja, endpointy, JSON i generowanie kontraktów.
- [Algorytmy](docs/ALGORITHMS.md) — Pace, Consistency, ResultScore, Rating i incydenty.
- [Dane i cache](docs/DATA_PIPELINE.md) — importy, pakiety wejściowe, rewizje i invalidacja.
- [Round Analytics](docs/ROUND_ANALYTICS.md) — definicje analityki rund.
- [Predykcje](docs/PREDICTIONS.md) — Podium Predictor, Fantasy i ograniczenia modeli.
- [Wydajność](docs/PERFORMANCE.md) — monitoring, diagnostyka i testowanie zgodności.

[Archiwum](docs/archive/) zawiera wybrane wyniki dawnych pomiarów i eksperymentów. Nie jest specyfikacją bieżącej implementacji.
