# Dane, pakiety wejściowe i cache

PostgreSQL przechowuje fakty zawodów: rundy, sesje, zawodników, wyniki, pomiary, przyznane punkty i historię zespołów. API pobiera je przez Supabase/PostgREST oraz RPC. Baza ogranicza koszt powtarzanych złączeń i transportu, ale bieżące kalkulatory sportowe nadal wykonują się w C#.

## Import i dane źródłowe

Kontrolery administracyjne przyjmują import sesji, formularz rundy i PDF. Fetchery ChronoRace/Tissot pobierają format dostawcy, mappery sprowadzają go do wewnętrznego modelu, a warstwa zapisu wykonuje upserty partiami. Uzupełnianie wyników i ponowny import pozostają osobnymi operacjami. Danych z bieżącego Live nie należy utożsamiać z zapisanymi wynikami historycznymi.

Importer ChronoRace przyjmuje wyłącznie HTTPS na `prod.chronorace.be`, `www.chronorace.be` lub `chronorace.be`, bez danych logowania i portu innego niż 443. Tissot zachowuje własną listę oficjalnych hostów i również odrzuca takie porty oraz dane logowania. Klienci HTTP tych importerów i proxy ChronoRace nie podążają automatycznie za przekierowaniami. Nowy adres dostawcy wymaga świadomego dopuszczenia w kodzie, a nie wyłączenia kontroli dla dowolnego URL.

Odpowiedzi JSON tych trzech klientów mają limit 16 MiB sprawdzany również podczas odbioru bez `Content-Length`. Odczyt body ma budżet 30 s; klient HTTP ma osobny timeout żądania 30 s. Przekroczenie rozmiaru daje `UPSTREAM_RESPONSE_TOO_LARGE`, nie częściowy import. Te zabezpieczenia nie są pełnym audytem wszystkich integracji: obrazy, PDF, przeglądarka Pinkbike, ograniczenia sieci wychodzącej i konfiguracja hostingu wymagają osobnej kontroli przed publikacją.

Splity są czasami narastającymi; sektory powstają z różnic kolejnych wiarygodnych pomiarów i mety. Brak lub nieprawidłowy checkpoint nie może tworzyć fikcyjnie szybkiego sektora. Cztery pośrednie splity i meta dają pięć sektorów, ale brak dowodu pozostaje brakiem. Korekta incydentu w kalkulatorze nie nadpisuje oficjalnego przejazdu.

Team przy wyniku ma pierwszeństwo. Przy jego braku używane jest przypisanie z `rider_team_history` obowiązujące w dniu sesji. Historia zapisuje okres, źródłową rundę i co najwyżej jeden otwarty okres ridera. Import bez teamu nie powinien zamykać historii. Ranking dobiera team do ostatniego wyniku w wybranym zakresie, nie zawsze do obecnego zespołu.

Punkty są przyznawane według reguł systemu punktowego i przechowywane oddzielnie od Ratingu/ResultScore. Zmiana tabeli punktów może wymagać administracyjnego przeliczenia przyznanych punktów; samo przeładowanie kalkulatora Pace tego nie wykonuje.

Obrazy rund mają osobną ścieżkę odczytu, zamiast base64 w ciężkich odpowiedziach. `RoundImagePayloadParser` konwertuje obraz do WebP z jakością 88; import i administracyjna konwersja korzystają z tej interpretacji. Operacja konwersji istniejących plików jest jawnym zapisem, nie zadaniem startowym API.

## Odczyt rankingu i profilu

Najpierw wybierane są sesje pasujące do filtrów. Dlatego w logach Supabase mogą występować żądania `/sessions`, mimo że przeglądarka wywołała `/api/v1/ranking`. Dopiero kolejnym krokiem jest pobranie pakietów wejściowych albo danych surowych. Nie istnieje obowiązek zgodności ścieżki własnego endpointu z tabelą odczytywaną przez PostgREST.

`get_ranking_input_packages` zwraca zwarte dane sesji; `get_rider_input_data` zwraca zawodnika i pole potrzebne do porównań. Format `v:3` zawiera sesje `s`, wyniki `r`, metadane riderów `d`, teamy `t`, referencje pola `f` w detalach, źródłowe pomiary narastające `ix` i opcjonalne dane prezentacji splitów `x`. `p_include_splits=false` pozwala rankingowi nie przesyłać niepotrzebnych danych wyświetlania.

Pakiety **nie zawierają decyzji o incydencie ani końcowych Pace, Consistency i Ratingu**. Parser i kalkulatory odtwarzają sektory, rozkłady pola i korekty w API. Istotne jest pełne pole odniesienia przed filtrowaniem pojedynczego ridera; wyszukiwanie nie może zmieniać sportowego znaczenia jego tempa.

`RankingInputPackageReader` sprawdza kompletność zestawu sesji. Brak pakietów nie może skutkować częściowym rankingiem udającym kompletny. Ścieżki surowe i starsze kontrakty RPC pozostają fallbackiem; błąd autoryzacji lub sieci nie powinien być ukrywany jako zwykły brak pakietu. Detale potrafią użyć surowego źródła także dla nieobsługiwanego przez reuse filtra statusu.

`ReadOptimizations:TypedRanking=true` włącza typowany odczyt. Historycznie nazwana flaga `PreparedRiderDetails=true` oznacza dziś ponowne użycie **pakietów źródłowych**, nie zapisanych wyników kalkulatora. Obie są włączone w repozytoryjnym `appsettings.json`.

## Co wymaga przebudowy

Zmiana wzoru, progu incydentu lub wagi kalkulatora wymaga wdrożenia nowego API oraz odświeżenia obliczonych odpowiedzi, nie przebudowy źródłowych pakietów. Zmiana wyników, pomiarów, przyznanych punktów albo schematu wejścia wymaga odświeżenia źródła i pakietów. `input_splits` zachowuje także brakujące i nieprawidłowe obserwacje potrzebne interpretacji danych.

Normalne odświeżanie używa `refresh_ranking_session_preaggregate`; tryb `missing` sprawdza dostępność wejść. Dawne kolumny `prepared_payload` i `prepared_version`, RPC przygotowanych metryk oraz `RankingPreaggregationService.PrepareAsync` zachowano dla kompatybilności i jawnych narzędzi powrotu. Bieżąca ścieżka ich nie konsumuje, a zwykły import nie przygotowuje kalkulatorozależnych wyników. Samo istnienie tych nazw nie oznacza, że należy je odbudowywać po każdej zmianie wzoru.

## Warstwy cache

Pakiety w pamięci procesu żyją 30 s i rozróżniają zakres danych, rolę oraz sesję. Parsowanie/przygotowanie tej samej niemutowanej paczki jest dodatkowo współdzielone wewnątrz jednego żądania. To inne mechanizmy niż trwała preagregacja w PostgreSQL.

`RankingService` przechowuje snapshot obliczeń przez 30 s. `RankingSingleFlight` scala nakładające się żądania o tej samej tożsamości w danym procesie; nie koordynuje wielu hostów. Sortowanie i paginacja korzystają z przygotowanych wierszy. Porównanie z ostatnią rundą lub wcześniejszymi filtrami może wymagać drugiego kontekstu obliczeń, ale nie musi ponownie przygotowywać identycznych sesji.

Output Cache przechowuje odpowiedzi endpointów objętych polityką: ranking 30 s, season overview i podium 60 s, analytics 5 min. Uwzględnia wszystkie parametry query; nie zapisuje błędów ani odpowiedzi z `Set-Cookie`. Autoryzacja pozostaje przed cache. `refresh=1` omija lookup i zapis odpowiedzi rankingu oraz wymusza jego przeliczenie, ale nie jest poleceniem usunięcia wszystkich cache aplikacji i bazy.

TanStack Query ma domyślnie 30 s świeżości i 5 min przechowywania nieaktywnych danych. Słowniki filtrów mają 5 min świeżości, wspólny klucz sezonów i odświeżanie przy wejściu, powrocie do okna lub odzyskaniu połączenia, jeżeli dane są nieświeże. Otwarcie dropdownu nie oznacza osobnego pobrania. `/api/v1/metadata` dostarcza etykiety, skróty i kolejność typów. Kategorie są utrzymywane przez katalog lokalny uzupełniany metadanymi i odpowiedziami API; nie mają oddzielnego endpointu kategorii. Nowy import nie jest natychmiastowym powiadomieniem przeglądarki. Live ma własny polling, opisany w [ARCHITECTURE.md](ARCHITECTURE.md).

## Rewizje i invalidacja

`CacheCoherence:Enabled` jest domyślnie wyłączone; nie jest jawnie włączone w śledzonym `appsettings.json`. Włączenie wymaga migracji `20261010210121_calculation_revisions.sql` i serwerowych uprawnień do RPC. `DataRevisionService` pobiera rewizje przez `get_calculation_revisions` najwyżej raz na 5 s aktywnej instancji, chyba że żądany jest wymuszony odczyt. Nie jest to timer automatycznie odświeżający UI.

Przy aktywnej spójności klucze cache uwzględniają zakres rewizji i fingerprint algorytmu. Output Cache sprawdza rewizję ponownie przed publikacją i nie zachowuje odpowiedzi powstałej w trakcie zmiany danych. Nieudane sprawdzenie rewizji nie przedłuża ważności starej; API może zwrócić błąd dostępności, zamiast potwierdzać niezweryfikowaną aktualność.

Triggery rewizji są atomowe w granicach transakcji PostgreSQL. Import składający się z wielu żądań REST nie jest jedną transakcją: jego części mogą być widoczne po kolejnych commitach. Rewizje pomagają odrzucić obliczenie obejmujące różne zaobserwowane stany, lecz nie zmieniają importera w atomowy batch. Przy wyłączonej spójności obowiązują starsze reguły TTL; nie należy obiecywać identycznej gwarancji invalidacji.

## Utrzymanie i wdrożenie

### Sport-only data rollout — approval pending

The local implementation removes health indicators from importer mappings, DTOs, admin edits and Fantasy selection. Production erasure is **not performed automatically**. Migration `20261019234716_remove_health_data.sql` requires explicit owner approval. It replaces the five installed read RPC bodies while retaining signatures/grants, clears compact identity slot 10 to JSON null without shifting later fields, scrubs nested legacy keys from stored input packages, invalidates legacy prepared outputs, sanitizes shared price snapshots, removes shared forecasts and drops `riders.has_injury` without `CASCADE`. Source results, timing inputs and calculator formulas are unchanged. An unknown dependency must abort the transaction.

Rollout: stop all API/import/background writers; confirm the actual Supabase target, volumes and restricted recovery procedure; apply the approved migration; clean local/persistent Fantasy files; deploy the matching API/frontend; restart processes to discard in-memory and HTTP output caches; synchronize prices and regenerate forecasts; verify public JSON and numerical parity. Do not roll back to code that reimports retired fields. Price snapshots and forecasts use separate configured directories (`Fantasy:Import:SnapshotDirectory` and `Fantasy:DataDirectory/predictions`); confirm the deployed paths before running cleanup.

`tools/Remove-LegacyFantasyData.ps1 -PriceDirectory <absolute-prices-path> -ForecastDirectory <absolute-predictions-path>` previews file scope. Only add `-Apply` after approval. It preserves whitelisted catalogue identities/prices/history, marks sanitized snapshots `dataVersion: 1`, and removes only hashed forecast JSON files for regeneration. It never copies original data into a new backup. `tools/Remove-LegacyFantasyData.Tests.ps1` checks preview, erasure, retained sporting values, deletion scope and repeated execution using synthetic files.

Database/file backups, operator exports, previously saved source HTML, logs and Git history require a separate inventory and retention/access decision; the migration does not erase those copies. Do not claim completed production erasure or publish a definitive privacy notice until deployment and retained-copy handling are verified. The incident-audit fixture retains only sporting matching columns, not source narratives or medical links.

Migracje stosuje się jawnie po sprawdzeniu środowiska i zależności. [Archiwalne potwierdzenie wdrożenia](archive/deployment-2026-10-11.md) dokumentuje konkretny przebieg, nie obecne ustawienia produkcji. Obecność pliku SQL w repozytorium ani wartość domyślna flagi nie dowodzi aktywacji na serwerze.

Trwałość prognoz Fantasy i koordynację zadań opisuje [PREDICTIONS.md](PREDICTIONS.md). Sposób mierzenia osobno komunikacji z bazą i obliczeń API opisuje [PERFORMANCE.md](PERFORMANCE.md).
