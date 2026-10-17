# RaceTrace API

Backend RaceTrace udostępnia wyniki zawodów downhill, ranking, profile zawodników, porównania, analitykę rund i prognozy. ASP.NET Core na .NET 10 pobiera dane przez Supabase/PostgREST, a obliczenia sportowe wykonuje w C#. Aplikacja React znajduje się w sąsiednim repozytorium `racetrace-web-app`.

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
