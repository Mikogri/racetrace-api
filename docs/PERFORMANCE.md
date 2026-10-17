# Wydajność i diagnostyka

Ładowanie rankingu obejmuje wybór sesji, pobranie pakietów lub surowych danych, parsowanie, przygotowanie pola, kalkulatory, projekcję odpowiedzi i transport do przeglądarki. Detale ridera używają tej samej logiki metryk, lecz innego zakresu danych. Sama liczba requestów nie identyfikuje wąskiego gardła, a cache hit nie mierzy kosztu pełnego przeliczenia.

Główne optymalizacje to zwarte pakiety wejściowe, rozdzielenie danych wyświetlania od wejść rankingu, typowany parser, przygotowanie sesji raz dla wielu riderów, współdzielenie odczytów i snapshotów oraz SingleFlight. Kalkulatory używają m.in. wyszukiwania binarnego rang, przygotowanych sum narastających i ponownego użycia detekcji anomalii. Nie oznacza to przeniesienia wzorów do bazy. Zasady reuse i TTL opisuje [DATA_PIPELINE.md](DATA_PIPELINE.md).

## Co faktycznie mierzymy

Supabase duration to czas fizycznego HTTP wraz z odbiorem body, nie sam czas wykonywania SQL. Obejmuje sieć, PostgREST, bazę i transfer. Sam SQL wymaga oddzielnego pomiaru po stronie PostgreSQL; nie wolno odejmowaniem dwóch niedopasowanych liczników ogłaszać dokładnego czasu bazy.

W żądaniach udostępniających `Server-Timing` należy sprawdzać rzeczywiście zwrócone etapy. Ich czas może nakładać się na inne pomiary, szczególnie w operacjach równoległych. Request Details jest tylko prezentacją diagnostyki, może być ukryte flagą i nie stanowi osobnego pomiaru SQL. Łączny czas przeglądarki zawiera dodatkowo transmisję i obsługę odpowiedzi; render React mierzy się osobno.

Rozmiar JSON po dekompresji, bajty body zapisane przez serwer i skompresowany transfer sieciowy to różne wartości. Kompresja HTTP jest zarejestrowana w hoście, a klient Supabase obsługuje gzip/deflate/Brotli. Faktyczne użycie i zysk zależą od negocjacji, źródła i payloadu; nie wynika z tego gwarantowane przyspieszenie każdego requestu.

## OpenTelemetry

API używa `System.Diagnostics.Metrics`, `ActivitySource`, strukturalnych logów i OpenTelemetry. Domyślnie `Telemetry:Enabled=true`, `Detailed=true`, `LogRequests=false`, `TraceSampleRatio=0.1`, a `OtlpEndpoint` jest pusty. Zmienne środowiskowe używają separatora `__`, np. `Telemetry__LogRequests=true`.

Metryki nie są samplowane. Sampling 10% dotyczy root traces i respektuje rodzica. Brak eksportera nie wysyła telemetrii na zewnętrzny serwer; kolektor jest opcjonalny i nie jest dołączony do aplikacji. Jego niedostępność nie powinna blokować startu ani odpowiedzi. Nie umieszczamy sekretów w adresie eksportera. Wymiary metryk nie zawierają rider ID, pełnych URL, query, kluczy cache ani payloadów.

`racetrace.http.*` mierzy żądania, czas pipeline, 5xx, timeouty i body przed kompresją. `racetrace.supabase.*` mierzy fizyczne próby, czas odbioru i bajty po automatycznej dekompresji. Paginacja i fallback RPC nie są retry; obecnie transport nie ma automatycznej polityki ponawiania, więc licznik transportowych retry jest zerowy.

`racetrace.operation.duration` rozróżnia m.in. pace, consistency, rating, podium, round_analytics i preaggregation. Zakres `rating` obejmuje partię metryk i jej zależności, nie tylko pojedynczy wzór Rating. Zagnieżdżonych zakresów nie sumujemy do czasu endpointu. `preaggregation_rebuild` mierzy odświeżanie, a osobne `preaggregation` zachowane przygotowanie legacy.

`racetrace.cache.requests` rozróżnia hit, miss, bypass i fallback. `racetrace.singleflight.*` mierzy nowe/współdzielone wykonania, oczekujących i czas oczekiwania. Współdzielenie nie jest trafieniem cache i działa tylko przy nakładających się operacjach w jednym procesie. `racetrace.job.*` opisuje przebiegi, lease conflict/loss, retry i ostatni sukces zadań. Gauge sukcesu resetuje restart procesu; trwała historia jest w bazie, jeśli używany jest odpowiedni tryb koordynacji.

Middleware telemetrii pozostaje po kompresji, ale przed obsługą wyjątków i Output Cache, aby mierzyć nieskompresowane body i końcowy zmapowany status, także dla cache hit. Histogramy mają granice od 1 ms do 120 s. P50/P95/P99 wymagają agregacji rozkładu w backendzie obserwowalności, nie średniej z percentyli instancji. Lokalny `dotnet-counters` nie zastępuje długoterminowego pomiaru produkcyjnego.

```powershell
dotnet tool install --global dotnet-counters
dotnet-counters ps
dotnet-counters monitor --process-id <pid> --counters RaceTrace.Api
dotnet test racetrace-api.Tests --filter FullyQualifiedName~PerformanceTelemetryTests
```

## Porównanie poprawności i czasu

Najpierw zapisujemy baseline przed zmianą. Porównujemy te same dane, filtry, kategorię, sortowanie i wersję źródła. Oddzielamy zimny proces, zimny cache aplikacji, reuse pakietów i Output Cache. Wykonujemy kilka powtórzeń i podajemy medianę oraz rozrzut, nie najlepszy pojedynczy wynik. Zmiana źródła w trakcie testu unieważnia porównanie, zamiast dowodzić regresji wzoru.

`tools/CalculatorParity` ładuje dwie zachowane assembly niezależnie i porównuje pełne serializowane wyniki na stałych fixture'ach z dokładną równością. Nie przebudowuje wejść ani nie pobiera bazy:

```powershell
dotnet run --project tools/CalculatorParity -- <baseline-api.dll> <current-api.dll>
```

Zestaw obejmuje pola Pace, incydenty, częściowe wyniki, agregaty, Consistency, granice Rating i deterministyczne podium. To reprodukowalny test regresji, nie wyczerpujący dowód dla wszystkich możliwych zawodów.

`tools/ReadPathParity` porównuje odpowiedzi publicznych usług na zapisanych odczytach, z pakietami i fallbackiem. Z repozytorium API:

```powershell
dotnet run --project tools/ReadPathParity -c Release -- --parity --capture --api-path <baseline-api-directory> --output <snapshot-directory>
dotnet run --project tools/ReadPathParity -c Release -- --parity --prepared --typed --api-path <current-api-directory> --output <snapshot-directory>
```

`--cases rank-all,rank-2026,bruni-all,asa` ogranicza przypadki. `--repeat 3 --preagg-only` mierzy powtarzane świeże odczyty aplikacji z przygotowanym źródłem. `--fixtures` zapisuje wejścia w ignorowanym katalogu; `--replay` odtwarza je bez sieci i bez fallbacku dla brakującej fixture. Niezależne buildy nie mogą dzielić katalogu artefaktów; narzędzie obsługuje `-p:Baseline=true -p:ApiProject=<csproj>` i `--artifacts-path` dla starej implementacji.

Normalne odczyty probe są ograniczone do GET i `get_*` RPC. `--prepare` jest jawnym zapisem service role i nie należy go używać do pomiaru tylko do odczytu. Starszy `--stages` ma znaną niejednoznaczność refleksji; zweryfikowany workflow używa `--parity`. Czasy replay obejmują pliki, a capture zapis fixture — żaden nie jest izolowanym benchmarkiem SQL ani pełnym testem przeglądarki.

## Koordynacja i testy lokalnej bazy

`tools/test_fantasy_jobs.py` i `tools/test_calculation_revisions.py` służą do odizolowanego lokalnego PostgreSQL, nie produkcji. Wymagają lokalnego hosta, bazy `racetrace_test_<nazwa>`, ról `anon`, `authenticated`, `service_role` i dostępnego `psql` (opcjonalnie `RACETRACE_TEST_PSQL`). Używają osobnych baz dla obu zestawów. Sprawdzają m.in. takeover lease, odrzucenie starego właściciela, idempotencję i transakcyjne rewizje. Uruchamia się je świadomie, ponieważ tworzą testowy schemat i dane.

## Zachowane pomiary

[Pakiety wejściowe z 11 października](archive/2026-10-11-input-packages.md) zawierają porównanie raw/reuse, a [pomiar z 3 października](archive/RANKING_READ_PERFORMANCE_2026-10-03.md) opisuje starszą optymalizację SQL. To snapshoty konkretnej maszyny, danych i kodu. Nie są aktualnym SLA i nie należy porównywać liczb z różnych dokumentów bez wyrównania warunków. Dzisiejszy czas trzeba zmierzyć ponownie na używanym środowisku.
