# Architektura RaceTrace

RaceTrace składa się z jednej aplikacji ASP.NET Core, jednej aplikacji React i bazy PostgreSQL udostępnianej przez Supabase. To monolit aplikacyjny z podziałem na obszary funkcjonalne, nie zestaw mikroserwisów ani rygorystyczna Clean Architecture. Poszczególne warstwy pozostają w jednym projekcie .NET i korzystają z usług innych obszarów.

API odpowiada za obliczenia sportowe, analitykę i predykcje. Frontend zarządza nawigacją, filtrami, stanem zapytań i prezentacją. Wyjątkiem jest Live Timing: interfejs normalizuje aktualne dane zewnętrznych dostawców, zamiast przeliczać historyczny ranking co kilka sekund.

## Backend

`Program.cs` składa aplikację przez metody z `Infrastructure/ServiceCollectionExtensions.cs`. Rejestruje konfigurację, klienta HTTP, cache i usługi. Pipeline obejmuje kompresję odpowiedzi, telemetrię, obsługę błędów, kontrolę dostępu i Output Cache przed kontrolerami. Większość usług aplikacyjnych ma zakres pojedynczego żądania; cache, kontrola rewizji i SingleFlight są współdzielone w procesie.

Kontrolery w `Controllers/` określają adresy HTTP, typy odpowiedzi i wymagane uprawnienia. `QueryValidationService` oraz `Features/Metrics/MetricsQueryParser.cs` interpretują zapytanie. Usługi w `Services/` organizują odczyty, a pomocnicze moduły w `Features/` przygotowują dane lub odpowiedź. Nie każda klasa w `Services/` jest samodzielną warstwą domenową.

Ranking jest podzielony między `RankingService` (filtry, cache, porównanie pozycji i paginacja), `Features/Ranking/RankingSnapshotBuilder` (złożenie danych i metryk) oraz `RankingResponseProjection` (sortowanie i publiczna prezentacja). `DomainQueryService` pozostaje wspólnym punktem odczytu, ale deleguje pakiety do `Services/RankingInputPackageReader`, metadane do `DomainMetadataQueries`, historię zespołów do `RiderTeamHistoryQueries` i obrazy do `RoundImageQueries`.

`RidersService` udostępnia operacje profilu. `Features/Riders/RiderOverviewQueries`, `RiderResultQueries` i `RiderHistoryQueries` rozdzielają overview, listy wyników i historię; `RiderStatisticsProjection` składa statystyki. Overview dostarcza dane potrzebne zakładkom bez uruchamiania osobnych ciężkich odczytów po każdym przełączeniu zakładki. `CompareService` korzysta z tego samego źródła metryk; `CompareSessionSelection` dobiera sesje, a `CompareResponseProjection` buduje odpowiedź.

`ResultsService` organizuje odczyt wyników. Projekcje w `Features/Results/` odpowiadają za interpretację pomiarów, sektory, splity i Perfect Run, a `ResultTimingAnalytics` za ich analityczne podsumowanie. `RoundsService` obsługuje rundy i sesje; konwersja i zapis obrazów pozostają w wyspecjalizowanych usługach obrazów. `Features/Analytics/` zawiera współdzieloną analitykę rund.

`MetricsService` i `PaceService` prowadzą wspólną ścieżkę kalkulatorów dla rankingu, profilu i porównań. `PaceRunMapper` oddziela interpretację JSON od organizacji obliczeń. Kalkulatory nie odczytują bazy. Ich wzory i ograniczenia opisuje [ALGORITHMS.md](ALGORITHMS.md).

Importy oddzielają pobranie i mapowanie danych od zapisu: przykładowo `ChronoRacePayloadMapper` normalizuje dokument, a `ChronoRaceBatchStore` wykonuje operacje partiami. Administracja korzysta m.in. z `AdminRoundOperations`. Moduł `Features/Fantasy/` ma własne przygotowanie prognoz, synchronizację, reguły sezonu i optimizer; publiczny odczyt nie uruchamia tych kosztownych prac.

## Dostęp do bazy

`ISupabaseRestClient`, `SupabaseRestClient` i `SupabaseQuery` korzystają z PostgREST oraz funkcji RPC, nie z bezpośredniego połączenia SQL ani Entity Framework. Nazwa Supabase jest więc uzasadniona protokołem, rolami i obsługą Storage. Sam PostgreSQL nie zastąpi tego transportu bez adaptera lub uruchomienia zgodnego PostgREST.

`Infrastructure/Supabase/` parsuje zwarte pakiety transportowe. Te modele nie są publicznymi DTO. Obowiązuje rozdzielenie kontraktu bazy, wejścia kalkulatorów i odpowiedzi HTTP; [DATA_PIPELINE.md](DATA_PIPELINE.md) opisuje pakiety i fallbacki.

Migracje znajdują się w `supabase/migrations/`, a starsze skrypty również pod `racetrace-api/Database/` i innymi historycznymi lokalizacjami SQL. Ich obecność w repozytorium nie dowodzi wdrożenia do konkretnej bazy. API nie stosuje migracji podczas startu.

## Frontend

The public interface is branded **Race Trace MTB** (repository names and technical identifiers stay unchanged). Home uses a 46 px main heading (`--font-size-hero`), scaled down on narrow screens. Next Round shows “// Season over, see you {nextSeason} //” after a successful overview with no next round. Text stays secondary grey; only slashes use primary. Header metadata retains three entries: season, Season over, and See you {nextSeason}. The year follows the latest completed season; if the selected year has no rounds, the existing seasons dictionary supplies the latest known season rather than incrementing the calendar year. Loading and request errors remain separate states.

`src/app/` zawiera router, providery i stany tras. `src/features/` grupuje Home, Ranking, Riders, Rounds, Compare, Live i Fantasy. Wspólne kontrolki, tabele, komunikaty, tooltipy i wykresy są w `src/components/`; współdzielona analityka jest w `components/analytics`, nie w komponentach Home. Kolory, odstępy, typografia i promienie pochodzą z `src/styles/tokens/`. Styl wizualny to ciemny motyw „night”: tło `#050505`, szklane grafitowe panele z połyskiem i jasną krawędzią, ciemnozielony przycisk główny i zielony akcent `#45cf86`. Tabele i karty rund mają półprzezroczystą powierzchnię z lekkim rozmyciem tła (`--component-frost-surface`, `--component-frost-blur`); banery podsumowań i pasek menu używają ciemniejszej, neutralnie szarej, przezroczystej `--component-banner-surface`; banery mają bardzo lekkie rozmycie (`--component-light-blur`, 3 px), a menu mocniejsze (`--component-nav-blur`, 12 px). Panele nie rzucają cienia. Wykresy na stronach zawodnika, rundy, Compare i Live nie mają własnego tła ani krawędzi: `Panel` z propem `bare` zdejmuje powierzchnię i ramkę. Sekcje analityczne na Home (Final Performance, Sector Impact, Race Gaps) zostają w ramce; ich osadzone wersje na stronie rundy są bez ramki. Modale (`Dialog`, wybór zawodnika) to matowe szkło z mocnym rozmyciem, jasną górną krawędzią i bez rzucanego cienia; tło pod nimi jest przyciemnione i rozmyte, a wybór zawodnika (450 px) pokazuje jednego zawodnika na wiersz, bez ramek (nazwisko nad zespołem); hover i zaznaczenie mają to samo jaśniejsze tło, a zaznaczony wiersz dodatkowo ptaszek na końcu. Na mobile menu to panel wysuwany z prawej strony pod paskiem nawigacji (z przyciemnieniem tła); logo stoi po lewej, przycisk menu po prawej. Przycisk zamknięcia nie zmienia tła ani kursora. Obrys focusu (`--color-interaction-focus`) używa koloru primary, a focus pól tekstowych jest szary (`--color-interaction-focus-subtle-border`). Menu kontekstowe filtrów mają półprzezroczyste tło jak tooltipy, tylko nieco jaśniejsze i z mocniejszym rozmyciem (20 px). Fonty to Inter (tekst i interfejs) oraz IBM Plex Mono (nagłówki, dane, kontrolki); tytuły stron i nagłówek Home mają pionowy gradient biały→szary. Treść i stopka mają cienkie krawędzie boczne. `PageBackdrop` w `AppShell` rysuje stałe tło: promienie światła z góry (`components/backgrounds/light-rays.tsx`, WebGL2; bez WebGL2 nic nie jest rysowane) i słabą siatkę z maską radialną. Treść strony w `PageContainer` wchodzi kolejno animacją `rise`; `prefers-reduced-motion` wyłącza animacje CSS, a promienie zwalniają i pomijają intro.

`/privacy-policy` renders static English notice content using the shared page/section typography and About layout, with semantic section headings and email links. The controller is Mikołaj Grigoriew (Poland), contact `contact@racetracemtb.com`, in the final section. The page has no draft banner or last-updated panel. It distinguishes email correspondence from in-app messaging and self-managed backups from possible provider copies, explicitly leaving unverified provider retention/settings unresolved. Updating the page does not verify those settings, certify compliance or execute the pending data-erasure migration.

`src/lib/api-client.ts` tworzy adres, wysyła `fetch`, łączy anulowanie z limitem czasu, odczytuje JSON i sprawdza go schematem Zod. `api-request-diagnostics.ts` zbiera pomiary odpowiedzi niezależnie od transportu. Hooki w `features/*/api/` definiują parametry, klucze cache i politykę TanStack Query. Typy wygenerowane z OpenAPI sprawdzają zgodność kompilacji; Zod pozostaje walidacją w runtime. Ten podział jest opisany w [API.md](API.md).

Filtry są zapisywane w URL. Nawigacja z rankingu do profilu przekazuje jego kontekst; nie każda nawigacja do zawodnika dziedziczy wszystkie filtry. Prefiksy tras szczegółów zachowują pochodzenie w menu. Przekierowania starszych adresów są obsługiwane w routerze i sprawdzane testami. Zakładki profilu nie resetują wspólnych filtrów.

Pierwszy odczyt, odświeżenie istniejących danych, błąd i brak danych są osobnymi stanami. Wspólne komponenty utrzymują spójne skeletony, spinner, progress bar i komunikaty; odświeżanie zachowuje zawartość i płynnie zmienia przyciemnienie. Wykresy korzystają z Recharts i wspólnych tooltipów. Nie należy kopiować tego zachowania do kolejnych lokalnych implementacji.

Ordinary and chart tooltips share `TooltipSurface`. Its standard `backdrop-filter` declaration follows the WebKit-prefixed fallback so production CSS optimization retains the standard browser property. `node --test scripts/tooltip-css.check.mjs` exercises the minified CSS output in CI, not just the source stylesheet; blur remains 5 px with a 25% background.

## Live Timing

Moduły w `features/live/api/` rozdzielają hooki, transport, adapter ChronoRace, adapter Tissot i normalizację wyników. API dostarcza rundę, kontekst i mapowanie UCI ID na zawodnika; źródłem bieżących czasów jest dostawca. Katalog i snapshot sesji są odświeżane co 3 sekundy. Zapytania o kontekst rundy i mapowanie identyfikatorów mają inną politykę świeżości, więc nie oznacza to odpytywania własnego rankingu co 3 sekundy.

ChronoRace może korzystać z kontrolowanego proxy API. Tissot odkrywa wydarzenia i fazy sezonu, zamiast zakładać niezmienność numerów wydarzeń. Zmiana dostawcy nie zmienia modelu tabeli. Normalizacja odróżnia startujących, jadących i zakończonych oraz nie dopisuje mety na podstawie niepełnego czasu. UI zachowuje widoczne wyniki podczas automatycznych aktualizacji.

## Granice potwierdzenia

Ten opis wynika z kodu repozytoriów. Nie potwierdza konfiguracji wdrożonego hosta, aktywnych flag, aktualnego schematu zdalnej bazy ani dostępności dostawców. Ustawienia środowiska mogą nadpisywać wartości z repozytorium. Zasady cache i wdrożenia są w [DATA_PIPELINE.md](DATA_PIPELINE.md), a diagnostyka w [PERFORMANCE.md](PERFORMANCE.md).
