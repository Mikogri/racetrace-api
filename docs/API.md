# API i kontrakty

RaceTrace udostępnia REST pod `/api/v1` oraz operacje administracyjne pod `/api/admin`. Pełną listę metod, parametrów i schematów zawiera generowany [contracts/openapi.json](../contracts/openapi.json). Dokument opisuje reguły kontraktu; nie zastępuje katalogu wszystkich pól i aliasów.

## Dostęp i konfiguracja

Home's season overview keeps statistics in the requested season. When that season has no next round, it fetches only the nearest dated scheduled/confirmed/live round from later seasons (one bounded fallback read), excluding cancelled rounds. Adding a 2027 calendar therefore exposes its first round on Home in 2026 without changing the 2026 ranking or statistics. Results defaults to the browser's current UTC year unless `?season=` explicitly selects another year; available future seasons do not change that default.

`ApiAccessMiddleware` i `ApiAccessGuard` sprawdzają token Bearer. Do odczytu służy `ApiAccess:ReadSecret` lub nadpisujące je `API_READ_SECRET`; token administratora również pozwala na odczyt. Zapis wymaga `Admin:ApiSecret` lub `ADMIN_API_SECRET` i filtra administracyjnego. To wspólne sekrety aplikacji, nie sesje użytkowników Supabase Auth. Brak konfiguracji kończy się 503, błędny token 401, a próba administracji tokenem tylko do odczytu — 403.

Klucze Supabase nie są tokenami HTTP RaceTrace. Klucz service role pozostaje po stronie serwera. Nie umieszczamy go w Vite ani w konfiguracji publicznego klienta.

The local Blazor import utility (`UCI_Stats_App`) sends camelCase JSON, including the JSON part of PDF multipart imports, and reads camelCase import responses. It reuses server-side User Secrets from `racetrace-api-2026`; its own configuration takes precedence. Read credentials use `Api:ReadSecret`, `ApiAccess:ReadSecret` or `API_READ_SECRET`; admin credentials use `Api:AdminSecret`, `Admin:ApiSecret` or `ADMIN_API_SECRET` (the old `ApiAccess:AdminSecret` alias is retained). `Api:BaseUrl` defaults to `http://127.0.0.1:5017`; set `Api__BaseUrl=https://api.racetracemtb.com` and matching credentials to target production directly, not the public Pages proxy. Restart the Blazor server after changing credentials. Never publish this administrative utility without separate access protection. Shared development dummy tokens are no longer embedded in its settings.

In Planned round, the Blazor calendar queue captures the same validated round form and forces `scheduled`, with a required non-negative round number (zero is allowed for World Championships) and no live context. Each entry is saved through the existing `POST /api/admin/rounds`; this is sequential, not an atomic batch. It stops on the first error, retains confirmed saved IDs and skips them on subsequent attempts. After a timeout or invalid success response, check the database before retrying because the server may have saved the round. The queue is in-memory and is lost on reload. Duplicate season/type/round numbers are rejected within the queue.

Planned-round dates default to January 1–3 of the next calendar year. The first calendar entry defaults to round 1; adding a valid entry increments the form's round number by one, while validation failures leave it unchanged. `conditionId` may be null for `scheduled` only; no weather condition is guessed. Other statuses still require a positive condition ID. Migration `20261030164941_scheduled_round_optional_condition.sql` was applied to RaceTrace: it relaxes `rounds.condition_id` nullability and adds a validated check allowing null only for scheduled rounds. Counts remained 54 rounds and 21,462 results. Existing rows and ratings are unchanged.

Planned-round requests accept optional `imageSourceRoundId`, mutually exclusive with `image`, to reuse an existing round image or its venue fallback. The server validates the source before creating the round and copies image metadata/storage reference, without downloading, re-encoding or uploading the existing file. Missing images and non-positive source IDs are rejected. No new database schema or public write permissions are required. The Blazor image picker loads existing rounds for a selected source season, with a preview; uploading a PNG clears the reused-image selection. Deploy/restart the updated API before using this option.

W środowisku `Production` kontrola startowa wymaga sekretu odczytu długości co najmniej 32 znaków. Jeśli administracja jest skonfigurowana, jej sekret musi mieć co najmniej 32 znaki i różnić się od sekretu odczytu. Długość nie zastępuje losowości: sekret należy wygenerować kryptograficznie, nie składać z powtórzeń. Pusta konfiguracja administracji pozostawia jej operacje niedostępne. Kontrola wykonuje się przed zadaniami aplikacji; Development i narzędziowy eksport OpenAPI nie wymagają produkcyjnych sekretów.

Lokalny serwer Vite przekazuje `/api` do `API_PROXY_TARGET` (domyślnie `http://127.0.0.1:5017`) i dodaje nagłówek autoryzacji. Dla lokalnego API próbuje odczytać sekret .NET z `UserSecretsId=racetrace-api-2026`, następnie używa `API_READ_SECRET`, a na końcu lokalnej wartości deweloperskiej. Kod przeglądarki sam nie dodaje tego sekretu. Po wdrożeniu trzeba zapewnić odpowiednie proxy/bramę i routing; `vite preview` nie jest zastępstwem tej konfiguracji.

## Ochrona kosztu żądań

Przed autoryzacją i Output Cache działa wbudowany limiter .NET. Domyślnie dopuszcza 240 żądań na minutę na adres połączenia, a dla administracji i metod zmieniających stan osobny budżet 12 na minutę. Ciężkie odczyty rankingu, overview/metrics ridera, Compare, Analytics, sezonu i podium oraz mutacje współdzielą limit 8 równoległych żądań na proces. Kolejki są wyłączone. Przekroczenie limitu daje 429 z `error.code=RATE_LIMITED` i `Retry-After`; klient nie powinien natychmiast ponawiać pętli żądań.

Wartości konfiguruje sekcja `RequestLimits`: `RequestsPerMinute`, `AdminRequestsPerMinute`, `HeavyConcurrency`; wszystkie muszą być dodatnie. To punkt startowy do testu obciążenia, nie gwarancja obsłużenia określonego ruchu. Limit ciężkich żądań obejmuje także starszy endpoint venues ridera oraz odpowiedzi z cache i nie zmienia kalkulatorów. Ograniczenia są lokalne dla instancji, nie stanowią rozproszonej ochrony DDoS. Frontend respektuje `Retry-After` przy istniejących ograniczonych ponowieniach zapytań, zamiast ponawiać natychmiast; mutacje nadal nie są automatycznie ponawiane.

Adres klienta pochodzi z `Connection.RemoteIpAddress`, nie z bezkrytycznie odczytanego `X-Forwarded-For`. Dla Cloudflare Pages + Railway ustaw `Gateway__Enabled=true`: przed limiterem `GatewayRequestMiddleware` przyjmuje pojedynczy adres z `X-RaceTrace-Client-IP` i ustawia HTTPS, ale tylko przy poprawnym tokenie odczytu. Pages pobiera adres z kontrolowanego przez Cloudflare `CF-Connecting-IP`, nie z dowolnego nagłówka przeglądarki. Bez tej opcji obowiązuje adres połączenia i użytkownicy za proxy mogą współdzielić budżet. Sekret odczytu jest również granicą zaufania bramy i musi pozostać serwerowy; to nie jest logowanie użytkowników. Brama nadal powinna ograniczać nadużycia i nie wystawia administracji. Konfigurację dostawców opisują README obu repozytoriów.

`GET /health/live` jest publicznym, lekkim sprawdzeniem działania procesu. Nie odczytuje Supabase, nie ujawnia konfiguracji i nie potwierdza gotowości bazy, importera ani zewnętrznych dostawców. Jest poza wersjonowanym kontraktem OpenAPI.

## Główne zasoby

| Ścieżka pod `/api/v1` | Zastosowanie |
| --- | --- |
| `/ranking` | Ranking, sortowanie, paginacja i porównanie pozycji. |
| `/home/ranking` | Top Rated and Standings for the same season/category in one response. |
| `/riders`, `/riders/resolve`, `/riders/{riderId}` | Katalog, mapowanie UCI ID i profil. |
| `/riders/{riderId}/overview`, `/metrics`, `/results`, `/venues`, `/team-history` | Zbiorczy profil, metryki oraz wyspecjalizowane odczyty ridera. Przyrostki odnoszą się do tej samej ścieżki ridera. |
| `/rounds`, `/rounds/seasons`, `/rounds/{roundId}` | Kalendarz, dostępne sezony i kontekst rundy. |
| `/rounds/{roundId}/sessions`, `/results`, `/image` | Sesje, wyniki rundy i osobny plik obrazu. Przyrostki odnoszą się do tej samej ścieżki rundy. |
| `/sessions/common`, `/sessions/{sessionId}/results` | Wspólne sesje i wynik konkretnej sesji. |
| `/results/{resultId}`, `/results/{resultId}/time-splits` | Pojedynczy wynik i pomiary. |
| `/compare` | Jedna odpowiedź dla porównania dwóch zawodników. |
| `/analytics/round-profile`, `/analytics/riders/{riderId}/pressure-performance` | Profil rundy i analityka presji zawodnika. |
| `/seasons/{season}/overview`, `/podium-predictor` | Podsumowanie sezonu i prognoza podium. |
| `/fantasy/current`, `/riders`, `/recommendation`, `/rules` | Odczyty Fantasy; ostatnie trzy przyrostki odnoszą się do `/fantasy`. |
| `/tracks`, `/teams`, `/conditions`, `/competition-classes`, `/points/systems`, `/metadata`, `/search` | Zasoby pomocnicze, opcje filtrów, etykiety/skrótowce i wyszukiwanie. |
| `/live/sessions`, `/live/chronorace/*`, `/live/rounds/{roundId}/championship` | Zachowane odczyty live, proxy dostawcy i projekcja klasyfikacji sezonu. Istnienie endpointu nie oznacza, że UI zawsze go wywołuje. |

Administracja obejmuje import sesji i rund z ChronoRace/Tissot, import PDF, edycję zasobów i obrazów, usuwanie rund, ponowny import sesji, punktację, odświeżanie pakietów oraz synchronizację Fantasy. Dokładne metody i formaty wejścia należy sprawdzać w OpenAPI i kontrolerze; zapis nie jest dozwolony przez publiczny token odczytu.

## JSON i modele

Fantasy rider responses expose round-scoped `entryStatus` (`unknown`, `entered`, `not_entered`) instead of the retired health flag. This is an intentional coordinated breaking change: regenerate OpenAPI/TypeScript and deploy API and frontend together. Athlete profiles and admin edits no longer include the retired field; rider selections use explicit identity columns rather than wildcard reads. Entry status is not a replacement for official DNS/DNF/DSQ result status. See [PREDICTIONS.md](PREDICTIONS.md) for evidence and eligibility rules.

ChronoRace result proxy and import boundaries retain only known sporting rider fields. Provider field names, official result statuses and timing records are preserved; unrecognized rider attributes are not forwarded or persisted. External mixed payloads may still contain ignored attributes in transit, so raw response logging/archiving must remain disabled.

HTTP oraz schematy OpenAPI używają `camelCase`: `RiderId` staje się `riderId`, a `GapToLeader` — `gapToLeader`. Preferowany model C# to krótki `sealed record`, o ile zachowuje wymagane wartości domyślne i logikę. Atrybut nazwy jest potrzebny tylko dla rzeczywistego wyjątku kontraktu, nie dla zwykłego `camelCase`.

Dynamiczne `JsonObject` i `JsonNode` przechodzą przez `ApiJsonNodeConverterFactory`: writer normalizuje nazwy właściwości tylko na granicy HTTP i nie mutuje źródłowego dokumentu. Klucze słowników identyfikujące dane pozostają niezmienione (`DictionaryKeyPolicy=null`). Odczyt dynamicznego JSON nie dokonuje analogicznego przepisywania. `AdminJsonFields` mapuje obsługiwane pola PATCH na kolumny bazy.

Kontrakt bazy, nazwy parametrów RPC, prywatne snapshoty, pliki seed i surowe wejścia dostawców nie zostały globalnie przemianowane. Proxy ChronoRace jawnie korzysta z serializacji dostawcy. Wymagane są testy obu granic: nazwa poprawna dla SQL nie musi być poprawna dla odpowiedzi publicznej. Klient używający starego `snake_case` wymaga migracji razem z API.

Nie wszystkie odpowiedzi są zamkniętymi DTO. Dynamiczne zasoby i niektóre administracyjne koperty pozostają otwartym JSON-em w OpenAPI. Wygenerowany typ nie potwierdza pełnego kształtu takiej odpowiedzi; frontend nadal potrzebuje schematu Zod i świadomego adaptera.

## Filtry, walidacja i paginacja

Context filter dictionaries use `GET /api/v1/filter-options`: one anonymous Supabase RPC (`get_filter_options`) returns only seasons, track labels/venue/country, conditions and competition-class labels. Existing endpoints remain available. Ordering and limits match the previous context requests: descending distinct seasons from the newest 1,000 round rows, up to 150 tracks and 100 conditions by name, and all competition levels by priority (including inactive ones). Ties use IDs for stable ordering. Categories remain independent client metadata. Context dictionaries retain the five-minute React Query freshness policy; Results still requests only `/rounds/seasons`, not the full bundle.

Deploy `supabase/migrations/20261029195017_filter_options.sql` before the API/frontend rollout, with explicit production approval. It adds a read-only `SECURITY INVOKER` function with an empty search path and explicit execution grants, retaining existing table grants/RLS. Applied to RaceTrace Analytics on 2026-10-29 after operator approval; live RPC dictionary equality and one upstream call were verified. Old clients continue to use unchanged endpoints; application rollback does not require dropping the function. Shared Compare inputs use the separately applied `20261029195018_compare_shared_rider_input.sql`; neither migration deploys API/frontend code.

Wspólny parser metryk obsługuje m.in. kategorie, sezony, daty, trasy, warunki i klasy. Poszczególne endpointy mają własny podzbiór filtrów. Listy są przekazywane jako CSV; pusty lub nieobecny filtr nie oznacza automatycznie „zero wyników”. Starsze aliasy query nadal są obsługiwane tam, gdzie definiuje je parser — zmiana nazw odpowiedzi na `camelCase` ich nie usunęła.

`QueryValidationService` odrzuca niepoprawne liczby, daty, kierunki i nieobsługiwane sortowanie. Wspólna paginacja zaczyna się od strony 1, domyślnie ma 30 elementów i dopuszcza 1–150 elementów. `PublicPagedResponse<T>` zwraca `items`, `page`, `pageSize`, `totalItems`, `totalPages`, `hasPreviousPage`, `hasNextPage`; dla pustego zbioru `totalPages=0`. Nie każdy endpoint jest paginowany.

Domyślne sortowanie UI rankingu to Rating; sam `RankingService` bez parametru sortowania domyślnie używa punktów malejąco. Nie należy utożsamiać ustawienia strony z domyślnym zachowaniem endpointu.

Home uses `/home/ranking?season=2026&category=elite_men` for both ranking cards. The response contains the existing ranking payloads sorted by Rating and total points descending, each limited to the first five riders without position comparison. Both projections reuse the normal ranking snapshot and calculators; no season-summary, round or winner queries are added. Matching card filters share one frontend request, while different categories keep independent query/loading/error state and controls. Other ranking filters, sorting, search, comparison and pagination parameters are not inputs to this Home endpoint.

Open Men i Open Women są syntetycznymi wyborami wyników/analityki rundy, nie osobnymi kategoriami rankingu. Widok All wybiera najlepszy ukończony przejazd zawodnika i zachowuje informację o sesji. Szczegóły zakresu Analytics opisuje [ROUND_ANALYTICS.md](ROUND_ANALYTICS.md).

## Frontend i generowanie kontraktów

W `racetrace-web-app` `src/lib/api-client.ts` odpowiada za HTTP, anulowanie, timeout i sprawdzenie odpowiedzi Zod. Domyślny timeout wynosi 12 s i jest ustawiany przez `VITE_API_TIMEOUT_MS`; bazowy adres przez `VITE_API_BASE_URL`. Błędy transportu, HTTP i schematu mają różne kody `ApiError`. Czas i rozmiar odpowiedzi mierzy osobny moduł diagnostyczny.

Eksporter ASP.NET generuje `contracts/openapi.json` z metadanych kontrolerów i DTO. Pinned `openapi-typescript` tworzy typy w `src/types/generated/api.d.ts`; nie edytujemy ich ręcznie. Eksport uruchamia host narzędziowy bez produkcyjnego startu, dostępu do Supabase i zadań w tle. Pracując w repozytorium frontendu:

```powershell
npm run contracts:export
npm run contracts:generate
npm run contracts:check
npm run contracts:test
```

`contracts:check` sprawdza zgodność artefaktów i potrafi wykryć rozjazd między repozytoriami. Po zmianie DTO należy sprawdzić zarówno JSON, jak i wygenerowane schematy oraz frontend. Sam poprawny build C# nie dowodzi zachowania kontraktu.
