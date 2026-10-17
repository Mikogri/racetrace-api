# RaceTrace — przewodnik po plikach

> Archiwalny spis sprzed refaktoryzacji z 17.10.2026. Poniższe uwagi o problemach i ścieżkach opisują stan przed zmianami, nie aktualny kod. Usunięto demo i RiderSimilarity, zsynchronizowano UserSecretsId, wydzielono DomainMetadataQueries, RiderTeamHistoryQueries i RoundImageQueries. Wspólne wykresy są w components/analytics, a Live ma osobne moduły dostawców, normalizacji, transportu i hooków. Pliki schematów ridera nie mają już mylącego prefiksu get-.

Stan: 17.10.2026. Zakres: **360 plików API + 257 plików frontendu = 617 plików śledzonych w Git**. Każdy ma osobny wpis. Stary projekt UCI_Stats_App, zależności, bin/obj, cache i lokalne artefakty są poza zakresem.

To opis odpowiedzialności i statycznego użycia plików, nie pomiar wykorzystania każdej metody podczas działania. „Tak” wynika z importów, wywołań, rejestracji DI, routingu HTTP lub konfiguracji narzędzi. Kontroler może nie mieć bezpośredniego odwołania do swojej klasy, a nadal działać przez ASP.NET.

**„—” w kolumnie poprawy oznacza brak konkretnej potrzeby zmiany.** Sugestie nie są wdrożone. Nie usunięto żadnych plików aplikacji. Migracje i testy nie wykonują się przy każdym requeście; stan wdrożenia SQL do bazy nie był tu sprawdzany. Pliki generowane są osobno oznaczone; ich wnętrza nie należy poprawiać ręcznie.

## Najważniejsze wnioski

- [vite.config.ts](<C:/Work/Projects/race-trace/racetrace-web-app/vite.config.ts>): proxy nadal szuka starego UserSecretsId; API ma już racetrace-api-2026.
- [racetrace-api/Services/DomainQueryService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DomainQueryService.cs>), RankingService, RidersService i importery skupiają kilka odpowiedzialności; to główne miejsca do stopniowego porządkowania.
- [src/features/live/api/get-live.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/get-live.ts>) łączy komunikację, adaptery i normalizację; [src/features/home/components/home-sections.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/components/home-sections.tsx>) zawiera także wykresy używane poza Home.
- Kilka obrazków/fontów nie ma odwołań w kodzie ani HTML. To kandydaci do usunięcia, nie automatyczny dowód braku odbiorców poza repozytorium.
- Request Details, generalkę Live i dane demo wyłączają świadome flagi. To funkcje ukryte, a nie osierocone moduły.
- Są dwa lockfile frontendu i trzy historyczne lokalizacje SQL. Warto uporządkować workflow; historii migracji nie należy usuwać bez analizy zależności.

## Jak czytać ścieżki

Każdy dział odpowiada katalogowi. Nazwa pliku jest odnośnikiem do lokalnej kopii. Opis mówi **co robi**, status — **kiedy jest używany**, ostatnia kolumna — **co konkretnie można poprawić**.

# API — racetrace-api

## (katalog główny)

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [.gitignore](<C:/Work/Projects/race-trace/racetrace-api/.gitignore>) | Wyklucza buildy, cache, sekrety i pliki lokalne z Git. | Narzędzie Git | — |
| [racetrace-api.slnx](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.slnx>) | Lista projektów rozwiązania .NET. | Build i testy | — |

## contracts

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [openapi.json](<C:/Work/Projects/race-trace/racetrace-api/contracts/openapi.json>) | Wygenerowany zapis publicznego kontraktu API. | Generowanie i kontrola kontraktów | Nie edytować ręcznie; odświeżać eksportem. |

## docs

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [API_CONTRACTS.md](<C:/Work/Projects/race-trace/racetrace-api/docs/API_CONTRACTS.md>) | Proces generowania i kontroli kontraktów OpenAPI/TypeScript. | Dokumentacja | — |
| [PROJECT_EVOLUTION.md](<C:/Work/Projects/race-trace/racetrace-api/docs/PROJECT_EVOLUTION.md>) | Techniczna historia większych zmian projektu. | Dokumentacja | — |
| [RANKING_READ_PERFORMANCE_2026-10-03.md](<C:/Work/Projects/race-trace/racetrace-api/docs/RANKING_READ_PERFORMANCE_2026-10-03.md>) | Historyczne pomiary i optymalizacje rankingu. | Dokumentacja | — |
| [ROUND_ANALYTICS.md](<C:/Work/Projects/race-trace/racetrace-api/docs/ROUND_ANALYTICS.md>) | Definicje wskaźników analityki rundy. | Dokumentacja | — |
| [calculation-consistency.md](<C:/Work/Projects/race-trace/racetrace-api/docs/calculation-consistency.md>) | Wersje kalkulatorów, rewizje i zgodność cache. | Dokumentacja | — |
| [fantasy-backtest-2026-09-16.md](<C:/Work/Projects/race-trace/racetrace-api/docs/fantasy-backtest-2026-09-16.md>) | Historyczna walidacja modeli Fantasy. | Dokumentacja | — |
| [fantasy-browser-sync.md](<C:/Work/Projects/race-trace/racetrace-api/docs/fantasy-browser-sync.md>) | Import Pinkbike i konfiguracja synchronizacji przeglądarkowej. | Dokumentacja | — |
| [fantasy.md](<C:/Work/Projects/race-trace/racetrace-api/docs/fantasy.md>) | Założenia, API i działanie modułu Fantasy. | Dokumentacja | — |
| [incident-conservative-refinement-2026.md](<C:/Work/Projects/race-trace/racetrace-api/docs/incident-conservative-refinement-2026.md>) | Wyniki strojenia konserwatywnego detektora incydentów. | Dokumentacja | — |
| [incident-experiments-2026.md](<C:/Work/Projects/race-trace/racetrace-api/docs/incident-experiments-2026.md>) | Porównanie eksperymentalnych wariantów detektora. | Dokumentacja | — |
| [incident-validation-2026.md](<C:/Work/Projects/race-trace/racetrace-api/docs/incident-validation-2026.md>) | Walidacja incydentów na potwierdzonych przypadkach. | Dokumentacja | — |
| [performance-monitoring.md](<C:/Work/Projects/race-trace/racetrace-api/docs/performance-monitoring.md>) | Znaczenie pomiarów i konfiguracja monitoringu. | Dokumentacja | — |
| [podium-backtest-2026-09-15.md](<C:/Work/Projects/race-trace/racetrace-api/docs/podium-backtest-2026-09-15.md>) | Historyczna ocena skuteczności prognozy podium. | Dokumentacja | — |
| [ranking-incident-comparison-2025-2026.md](<C:/Work/Projects/race-trace/racetrace-api/docs/ranking-incident-comparison-2025-2026.md>) | Wpływ zmian detekcji incydentów na ranking. | Dokumentacja | — |
| [refactor-display-metadata.md](<C:/Work/Projects/race-trace/racetrace-api/docs/refactor-display-metadata.md>) | Opis obsługi słowników i dynamicznych etykiet. | Dokumentacja | — |
| [sector-incident-detection.md](<C:/Work/Projects/race-trace/racetrace-api/docs/sector-incident-detection.md>) | Zasady detekcji i korekt incydentów. | Dokumentacja | — |

## docs/architecture

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [RACETRACE_ARCHITECTURE.md](<C:/Work/Projects/race-trace/racetrace-api/docs/architecture/RACETRACE_ARCHITECTURE.md>) | Szczegółowy opis architektury i granic odpowiedzialności. | Dokumentacja | — |

## docs/deployment

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [2026-10-11.md](<C:/Work/Projects/race-trace/racetrace-api/docs/deployment/2026-10-11.md>) | Instrukcja wdrożenia zmian spójności i migracji. | Dokumentacja | — |

## docs/performance

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [2026-10-04-prepared-ranking-read.md](<C:/Work/Projects/race-trace/racetrace-api/docs/performance/2026-10-04-prepared-ranking-read.md>) | Historyczna optymalizacja przygotowanego odczytu rankingu. | Dokumentacja | — |
| [2026-10-11-input-packages.md](<C:/Work/Projects/race-trace/racetrace-api/docs/performance/2026-10-11-input-packages.md>) | Obecna zasada pakietów wejściowych niezależnych od kalkulatorów. | Dokumentacja | — |

## docs/superpowers/plans

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [2026-09-30-read-packages.md](<C:/Work/Projects/race-trace/racetrace-api/docs/superpowers/plans/2026-09-30-read-packages.md>) | Historyczny plan rozdzielenia pakietów danych. | Dokument historyczny | Zachować jako archiwum; oznaczyć datę i stan realizacji. |
| [2026-10-03-preaggregation-read-path.md](<C:/Work/Projects/race-trace/racetrace-api/docs/superpowers/plans/2026-10-03-preaggregation-read-path.md>) | Historyczny plan zoptymalizowanej ścieżki odczytu. | Dokument historyczny | Zachować jako archiwum; oznaczyć datę i stan realizacji. |
| [2026-10-03-ranking-read-performance.md](<C:/Work/Projects/race-trace/racetrace-api/docs/superpowers/plans/2026-10-03-ranking-read-performance.md>) | Historyczny plan poprawy czasu rankingu. | Dokument historyczny | Zachować jako archiwum; oznaczyć datę i stan realizacji. |

## docs/superpowers/specs

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [2026-10-03-preaggregation-read-path-design.md](<C:/Work/Projects/race-trace/racetrace-api/docs/superpowers/specs/2026-10-03-preaggregation-read-path-design.md>) | Historyczny projekt odczytu preagregacji. | Dokument historyczny | Zachować jako archiwum; oznaczyć datę i stan realizacji. |

## racetrace-api.Tests

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminDataServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/AdminDataServiceTests.cs>) | Testy: Edycja, usuwanie rund i administracyjne operacje na danych. | Odkrywany przez xUnit | — |
| [AdminGuardTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/AdminGuardTests.cs>) | Testy: Ochrona endpointów administracyjnych. | Odkrywany przez xUnit | — |
| [ApiAccessGuardTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ApiAccessGuardTests.cs>) | Testy: Walidacja dostępu do odczytu. | Odkrywany przez xUnit | — |
| [ApiAccessMiddlewareTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ApiAccessMiddlewareTests.cs>) | Testy: Zakres ścieżek objętych kontrolą dostępu. | Odkrywany przez xUnit | — |
| [CalculationIdentityTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CalculationIdentityTests.cs>) | Testy: Tożsamość danych i wersji kalkulatorów. | Odkrywany przez xUnit | — |
| [ChartStatisticsTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ChartStatisticsTests.cs>) | Testy: Agregaty i dane wykresów ridera. | Odkrywany przez xUnit | — |
| [ChronoRaceBatchImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ChronoRaceBatchImportTests.cs>) | Testy: Import pełnych rund oraz wielu kategorii. | Odkrywany przez xUnit | — |
| [CommonSessionsServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CommonSessionsServiceTests.cs>) | Testy: Dobór wspólnych sesji riderów. | Odkrywany przez xUnit | — |
| [CompactPackageSequenceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CompactPackageSequenceTests.cs>) | Testy: Kolejność wyników w zwartych pakietach. | Odkrywany przez xUnit | — |
| [CompareServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CompareServiceTests.cs>) | Testy: Spójność porównania dwóch riderów. | Odkrywany przez xUnit | — |
| [CompetitionClassImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CompetitionClassImportTests.cs>) | Testy: Klasy zawodów podczas importu. | Odkrywany przez xUnit | — |
| [CompositionContractTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/CompositionContractTests.cs>) | Testy: Rejestracja i składanie usług aplikacji. | Odkrywany przez xUnit | — |
| [ConsistencyPreparationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ConsistencyPreparationTests.cs>) | Testy: Zgodność przygotowanej ścieżki Consistency. | Odkrywany przez xUnit | — |
| [DataRevisionTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DataRevisionTests.cs>) | Testy: Unieważnianie cache i współbieżna zmiana rewizji. | Odkrywany przez xUnit | — |
| [DisplayMetadataTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DisplayMetadataTests.cs>) | Testy: Słowniki, aliasy i etykiety. | Odkrywany przez xUnit | — |
| [DomainQueryPaginationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DomainQueryPaginationTests.cs>) | Testy: Paginacja zapytań wejściowych do metryk. | Odkrywany przez xUnit | — |
| [DownhillConsistencyCalculatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DownhillConsistencyCalculatorTests.cs>) | Testy: Liczy Consistency, Confidence i składowe porównywalnych przejazdów. | Odkrywany przez xUnit | — |
| [DownhillPaceCalculatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DownhillPaceCalculatorTests.cs>) | Testy: Liczy Pace, Evidence i korekty incydentów. | Odkrywany przez xUnit | — |
| [DownhillRatingCalculatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DownhillRatingCalculatorTests.cs>) | Testy: Łączy składowe oceny w końcowy Rating. | Odkrywany przez xUnit | — |
| [DownhillResultScoreCalculatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DownhillResultScoreCalculatorTests.cs>) | Testy: Ocenia pozycje i agreguje wynik sesji z wagami. | Odkrywany przez xUnit | — |
| [DtoSerializationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DtoSerializationTests.cs>) | Testy: Identyczne nazwy, typy i nullowalność JSON. | Odkrywany przez xUnit | — |
| [DynamicFilterTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/DynamicFilterTests.cs>) | Testy: Dynamiczne klasy, kategorie i inne filtry. | Odkrywany przez xUnit | — |
| [FantasyConfigurationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyConfigurationTests.cs>) | Testy: Walidacja i konfiguracja Fantasy. | Odkrywany przez xUnit | — |
| [FantasyImporterTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyImporterTests.cs>) | Testy: Import cen i obsługa błędów źródła. | Odkrywany przez xUnit | — |
| [FantasyJobCoordinatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyJobCoordinatorTests.cs>) | Testy: Leasing, blokady i publikowanie zadań Fantasy. | Odkrywany przez xUnit | — |
| [FantasyMathTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyMathTests.cs>) | Testy: Punktacja, prawdopodobieństwa i optymalizacja składu. | Odkrywany przez xUnit | — |
| [FantasyServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyServiceTests.cs>) | Testy: Kompletna odpowiedź i rekomendacje Fantasy. | Odkrywany przez xUnit | — |
| [FantasyTrainingParticipantsTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FantasyTrainingParticipantsTests.cs>) | Testy: Dopasowanie startujących z list treningowych. | Odkrywany przez xUnit | — |
| [FieldSpreadTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/FieldSpreadTests.cs>) | Testy: Proporcjonalne progi i przyrostowe różnice czasowe. | Odkrywany przez xUnit | — |
| [LiveChampionshipServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/LiveChampionshipServiceTests.cs>) | Testy: Kontekst generalki na podstawie rundy. | Odkrywany przez xUnit | — |
| [LiveChampionshipTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/LiveChampionshipTests.cs>) | Testy: Punktacja oraz możliwość zmiany mistrza sezonu. | Odkrywany przez xUnit | — |
| [NamingContractTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/NamingContractTests.cs>) | Testy: Konwencje nazw i odwołania do projektu. | Odkrywany przez xUnit | — |
| [OpenApiContractTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/OpenApiContractTests.cs>) | Testy: Zgodność opisu OpenAPI z rzeczywistym kontraktem. | Odkrywany przez xUnit | — |
| [PaceIncidentFieldTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PaceIncidentFieldTests.cs>) | Testy: Korekty incydentów na tle całej stawki. | Odkrywany przez xUnit | — |
| [PaceOptimizationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PaceOptimizationTests.cs>) | Testy: Regresja optymalizacji Pace i walidacji opcji. | Odkrywany przez xUnit | — |
| [PacePreparationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PacePreparationTests.cs>) | Testy: Zgodność danych przygotowanych dla Pace. | Odkrywany przez xUnit | — |
| [PaceServiceSectorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PaceServiceSectorTests.cs>) | Testy: Przekazywanie sektorów i klasyfikacji do kalkulatorów. | Odkrywany przez xUnit | — |
| [PerfectRunResultsTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PerfectRunResultsTests.cs>) | Testy: Budowanie perfect run i czasów referencyjnych. | Odkrywany przez xUnit | — |
| [PerformanceTelemetryTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PerformanceTelemetryTests.cs>) | Testy: Nagłówki i pomiary przetwarzania requestu. | Odkrywany przez xUnit | — |
| [PodiumHistoricalExperimentTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PodiumHistoricalExperimentTests.cs>) | Testy: Zachowanie wariantów modelu historycznej prognozy. | Odkrywany przez xUnit | — |
| [PointAwardServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PointAwardServiceTests.cs>) | Testy: Właściwe punkty dla pozycji, sesji i kategorii. | Odkrywany przez xUnit | — |
| [PointSystemSchemaScriptTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PointSystemSchemaScriptTests.cs>) | Testy: Spójność skryptów struktury punktacji. | Odkrywany przez xUnit | — |
| [PointSystemSeedValidatorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PointSystemSeedValidatorTests.cs>) | Testy: Odrzucanie nieprawidłowych definicji punktacji. | Odkrywany przez xUnit | — |
| [PointSystemSeederTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PointSystemSeederTests.cs>) | Testy: Zapis i aktualizacja systemów punktacji. | Odkrywany przez xUnit | — |
| [PreparedRankingSessionTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PreparedRankingSessionTests.cs>) | Testy: Zgodność przygotowanych sesji z obliczeniami. | Odkrywany przez xUnit | — |
| [PreparedReadRequestTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PreparedReadRequestTests.cs>) | Testy: Liczba i zakres zapytań przygotowanego odczytu. | Odkrywany przez xUnit | — |
| [PublicApiQueryTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PublicApiQueryTests.cs>) | Testy: Parametry, filtrowanie i paginacja publicznego API. | Odkrywany przez xUnit | — |
| [PublicDtoMappingTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/PublicDtoMappingTests.cs>) | Testy: Typy i wartości publicznych DTO. | Odkrywany przez xUnit | — |
| [RankingDataRequestTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingDataRequestTests.cs>) | Testy: Zakres danych pobieranych do rankingu. | Odkrywany przez xUnit | — |
| [RankingPreaggregationIntegrationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingPreaggregationIntegrationTests.cs>) | Testy: Zgodność pakietów z odczytem bazowym. | Testy integracyjne; zależne od środowiska | — |
| [RankingPreparationReuseTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingPreparationReuseTests.cs>) | Testy: Ponowne użycie przygotowania danych rankingu. | Odkrywany przez xUnit | — |
| [RankingReadParityTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingReadParityTests.cs>) | Testy: Identyczne odpowiedzi różnych ścieżek rankingu. | Odkrywany przez xUnit | — |
| [RankingServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingServiceTests.cs>) | Testy: Pozycje, filtry, porównania i odpowiedź rankingu. | Odkrywany przez xUnit | — |
| [RankingSingleFlightTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RankingSingleFlightTests.cs>) | Testy: Współdzielenie obliczeń i anulowanie oczekiwania. | Odkrywany przez xUnit | — |
| [RefactorContractTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RefactorContractTests.cs>) | Testy: Zabezpieczenia kontraktów podczas refaktoryzacji. | Odkrywany przez xUnit | — |
| [ResultScorePreparationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/ResultScorePreparationTests.cs>) | Testy: Zgodność obu ścieżek ResultScore. | Odkrywany przez xUnit | — |
| [RiderProfileImageImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RiderProfileImageImportTests.cs>) | Testy: Rozpoznawanie i zapis zdjęć ridera. | Odkrywany przez xUnit | — |
| [RiderSimilarityTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RiderSimilarityTests.cs>) | Testy: Podobieństwo, grupy i dendrogram. | Odkrywany przez xUnit | — |
| [RiderTeamHistoryMappingTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RiderTeamHistoryMappingTests.cs>) | Testy: Dwa kompatybilne formaty historii teamów. | Odkrywany przez xUnit | — |
| [RoundImageBackfillTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RoundImageBackfillTests.cs>) | Testy: Partiowa konwersja istniejących zdjęć na WebP. | Odkrywany przez xUnit | — |
| [RoundImagePayloadParserTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RoundImagePayloadParserTests.cs>) | Testy: Walidacja formatu, rozmiaru i konwersji obrazu. | Odkrywany przez xUnit | — |
| [RoundRaceAnalyticsTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RoundRaceAnalyticsTests.cs>) | Testy: Wskaźniki analityki rundy i ich brakujące dane. | Odkrywany przez xUnit | — |
| [RoundResultSelectionTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RoundResultSelectionTests.cs>) | Testy: Kategorie Open i najlepszy przejazd w All. | Odkrywany przez xUnit | — |
| [RoundsServiceImageQueryTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/RoundsServiceImageQueryTests.cs>) | Testy: Zapytania oraz dobór zdjęcia rundy. | Odkrywany przez xUnit | — |
| [SeasonSummaryAndPodiumPredictorTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/SeasonSummaryAndPodiumPredictorTests.cs>) | Testy: Podsumowanie sezonu i dobór kolejnej rundy. | Odkrywany przez xUnit | — |
| [SectorIncidentDetectionTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/SectorIncidentDetectionTests.cs>) | Testy: Detekcja anomalii i szacowanie utraconego czasu. | Odkrywany przez xUnit | — |
| [SessionDateTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/SessionDateTests.cs>) | Testy: Normalizacja dat i kolejności sesji. | Odkrywany przez xUnit | — |
| [StageTwoResponseNormalizationTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/StageTwoResponseNormalizationTests.cs>) | Testy: Normalizacja odpowiedzi drugiego etapu API. | Odkrywany przez xUnit | — |
| [SupabaseRestClientTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/SupabaseRestClientTests.cs>) | Testy: Budowanie żądań i obsługa błędów klienta bazy. | Odkrywany przez xUnit | — |
| [SupabaseRestClientTransportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/SupabaseRestClientTransportTests.cs>) | Testy: Transport, odpowiedzi HTTP i dekompresja. | Odkrywany przez xUnit | — |
| [TeamHistoryTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TeamHistoryTests.cs>) | Testy: Dobór właściwego historycznego teamu. | Odkrywany przez xUnit | — |
| [TeamImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TeamImportTests.cs>) | Testy: Mapowanie i import teamów. | Odkrywany przez xUnit | — |
| [TeamsServiceTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TeamsServiceTests.cs>) | Testy: Lista i członkostwo riderów według historii. | Odkrywany przez xUnit | — |
| [TestPointAwardService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TestPointAwardService.cs>) | Atrapa serwisu punktów wykorzystywana przez testy. | Tylko testy | — |
| [TimeSplitImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TimeSplitImportTests.cs>) | Testy: Import poprawnych splitów i obsługa DNF. | Odkrywany przez xUnit | — |
| [TissotTimingJsonFetcherTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TissotTimingJsonFetcherTests.cs>) | Testy: Adresy i format danych Tissot. | Odkrywany przez xUnit | — |
| [TrackFilterTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TrackFilterTests.cs>) | Testy: Opcje i filtrowanie tras. | Odkrywany przez xUnit | — |
| [TrackImportTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/TrackImportTests.cs>) | Testy: Tworzenie i aktualizacja tras. | Odkrywany przez xUnit | — |
| [UciMtbWorldSeriesSupplementTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/UciMtbWorldSeriesSupplementTests.cs>) | Testy: Uzupełnianie brakujących wyników. | Odkrywany przez xUnit | — |
| [UciResultsPdfParserTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/UciResultsPdfParserTests.cs>) | Testy: Odczyt tabel i czasów z PDF. | Odkrywany przez xUnit | — |
| [UciWorldChampionshipJsonAdapterTests.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/UciWorldChampionshipJsonAdapterTests.cs>) | Testy: Normalizacja JSON mistrzostw świata. | Odkrywany przez xUnit | — |
| [racetrace-api.Tests.csproj](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/racetrace-api.Tests.csproj>) | Zależności i ustawienia projektu testów .NET. | Build/testy | — |

## racetrace-api.Tests/Fixtures

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [pinkbike-fantasy-athletes-2026.html](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api.Tests/Fixtures/pinkbike-fantasy-athletes-2026.html>) | Próbka HTML Pinkbike dla testów parsera/importu. | Fixture testowa | — |

## racetrace-api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [API_DOCUMENTATION.md](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/API_DOCUMENTATION.md>) | Dokumentacja endpointów, parametrów i administracji. | Dokumentacja | — |
| [Program.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Program.cs>) | Uruchamia host, pipeline HTTP, middleware i kontrolery. | Start API | — |
| [REFACTOR.md](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/REFACTOR.md>) | Notatki z wcześniejszego porządkowania struktury. | Dokumentacja | — |
| [appsettings.json](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/appsettings.json>) | Domyślne ustawienia API, cache, Fantasy i importów. | Konfiguracja API | — |
| [racetrace-api.csproj](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/racetrace-api.csproj>) | Zależności, target .NET i ustawienia projektu Web API. | Build API | — |
| [racetrace-api.http](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/racetrace-api.http>) | Ręczne przykładowe wywołania API. | Ręczne testy HTTP | Ujednolicić z plikiem DevChecks. |

## racetrace-api/Configuration

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Configuration/AdminOptions.cs>) | Konfiguracja sekretu operacji administracyjnych. | Tak — kod API | — |
| [ApiAccessOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Configuration/ApiAccessOptions.cs>) | Konfiguracja autoryzacji odczytu API. | Tak — kod API | — |
| [PointSystemSeedOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Configuration/PointSystemSeedOptions.cs>) | Ścieżka pliku inicjalizującego systemy punktacji. | Tak — kod API | — |
| [ReadOptimizationOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Configuration/ReadOptimizationOptions.cs>) | Flagi typowanego rankingu i pakietów szczegółów ridera. | Tak — kod API | Doprecyzować nazwę PreparedRiderDetails bez zmiany kontraktu konfiguracji. |
| [SupabaseOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Configuration/SupabaseOptions.cs>) | Adres, klucze i ustawienia dostępu do Supabase. | Tak — kod API | — |

## racetrace-api/Controllers

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminDataController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/AdminDataController.cs>) | Endpointy edycji danych, zdjęć i usuwania rund. | Tak — endpoint HTTP | — |
| [AdminFantasyController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/AdminFantasyController.cs>) | Administracyjne uruchamianie importu i przygotowania Fantasy. | Tak — endpoint HTTP | — |
| [AdminImportController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/AdminImportController.cs>) | Endpointy importu JSON/PDF, rund i zdjęć riderów. | Tak — endpoint HTTP | — |
| [AnalyticsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/AnalyticsController.cs>) | Endpointy profilu rundy, podobieństwa i Pressure Performance. | Tak — endpointy HTTP | — |
| [ChronoRaceLiveController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/ChronoRaceLiveController.cs>) | Proxy katalogu i wyników z ChronoRace. | Tak — endpoint HTTP | — |
| [CompareController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/CompareController.cs>) | Endpoint porównania dwóch riderów. | Tak — endpoint HTTP | — |
| [ConditionsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/ConditionsController.cs>) | Endpoint listy warunków tras. | Tak — endpoint HTTP | — |
| [FantasyController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/FantasyController.cs>) | Endpointy bieżących prognoz, teamu i zasad Fantasy. | Tak — endpoint HTTP | — |
| [LiveChampionshipController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/LiveChampionshipController.cs>) | Endpoint kontekstu i projekcji walki o generalkę. | Tak — endpoint HTTP | — |
| [LiveController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/LiveController.cs>) | Endpointy sesji i wyników Live zapisanych w bazie. | Tak — endpoint HTTP | — |
| [PodiumPredictorController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/PodiumPredictorController.cs>) | Endpoint prognozy podium. | Tak — endpoint HTTP | — |
| [PointsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/PointsController.cs>) | Systemy punktacji, klasy, przeliczanie punktów i pakietów. | Tak — endpoint HTTP | — |
| [RankingController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/RankingController.cs>) | Endpoint rankingu i porównania pozycji. | Tak — endpoint HTTP | — |
| [ResultsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/ResultsController.cs>) | Endpointy wyników sesji, pojedynczego wyniku i splitów. | Tak — endpoint HTTP | — |
| [RidersController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/RidersController.cs>) | Endpointy ridera, overview, wyników, tras i historii teamów. | Tak — endpoint HTTP | — |
| [RoundsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/RoundsController.cs>) | Endpointy rund, sezonów, zdjęć, sesji i wyników. | Tak — endpoint HTTP | — |
| [SearchController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/SearchController.cs>) | Endpoint wyszukiwania encji projektu. | Tak — endpoint HTTP | — |
| [SeasonsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/SeasonsController.cs>) | Endpoint podsumowania sezonu dla Home. | Tak — endpoint HTTP | — |
| [SessionsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/SessionsController.cs>) | Wyszukuje wspólne sesje wybranych riderów. | Tak — endpoint HTTP | — |
| [TeamsController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/TeamsController.cs>) | Endpointy listy i szczegółów teamów. | Tak — endpoint HTTP | — |
| [TracksController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Controllers/TracksController.cs>) | Endpointy tras i rekordów. | Tak — endpoint HTTP | — |

## racetrace-api/Database

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [20260720_public_api_read_indexes.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260720_public_api_read_indexes.sql>) | Indeksy przyspieszające publiczne odczyty. | Migracja — nie wykonuje się przy starcie | — |
| [20260720_stage1_competition_levels_and_point_systems.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260720_stage1_competition_levels_and_point_systems.sql>) | Poziomy zawodów oraz struktura systemów punktacji. | Migracja — nie wykonuje się przy starcie | — |
| [20260726_planned_round_status.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260726_planned_round_status.sql>) | Status rundy planowanej. | Migracja — nie wykonuje się przy starcie | — |
| [20260730_delete_round_cascade.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260730_delete_round_cascade.sql>) | Usuwanie rundy wraz z zależnymi danymi. | Migracja — nie wykonuje się przy starcie | — |
| [20260801_compact_ranking_data_rpc.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260801_compact_ranking_data_rpc.sql>) | Zwarty odczyt danych rankingu przez RPC. | Migracja — nie wykonuje się przy starcie | — |
| [20260801_rider_detail_preaggregation.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260801_rider_detail_preaggregation.sql>) | Historyczny pakiet preagregacji szczegółów ridera. | Migracja — nie wykonuje się przy starcie | — |
| [20260803_normalize_category_names_and_codes.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260803_normalize_category_names_and_codes.sql>) | Porządkuje nazwy i kody kategorii. | Migracja — nie wykonuje się przy starcie | — |
| [20260812_rider_gender.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260812_rider_gender.sql>) | Płeć zawodnika i jej uzupełnianie. | Migracja — nie wykonuje się przy starcie | — |
| [20260812_rider_team_history.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260812_rider_team_history.sql>) | Okresy przynależności ridera do teamu. | Migracja — nie wykonuje się przy starcie | — |
| [20260813_ranking_session_preaggregation.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260813_ranking_session_preaggregation.sql>) | Pakiety danych rankingu według sesji. | Migracja — nie wykonuje się przy starcie | — |
| [20260815_unify_metrics_preaggregation.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260815_unify_metrics_preaggregation.sql>) | Wspólna infrastruktura pakietów metryk. | Migracja — nie wykonuje się przy starcie | — |
| [20260816_add_2022_world_cup_points.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260816_add_2022_world_cup_points.sql>) | Punktacja World Cup sezonu 2022. | Migracja — nie wykonuje się przy starcie | — |
| [20260816_compact_ranking_preaggregate_transport.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260816_compact_ranking_preaggregate_transport.sql>) | Zmniejszenie formatu przesyłanych pakietów. | Migracja — nie wykonuje się przy starcie | — |
| [20260816_correct_2023_2025_world_cup_points.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260816_correct_2023_2025_world_cup_points.sql>) | Korekty punktacji World Cup 2023–2025. | Migracja — nie wykonuje się przy starcie | — |
| [20260816_optimize_ranking_preaggregate_payload.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260816_optimize_ranking_preaggregate_payload.sql>) | Odchudzanie payloadu historycznych pakietów. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_add_live_chronorace_source.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_add_live_chronorace_source.sql>) | Powiązanie rundy ze źródłem ChronoRace. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_backfill_missing_ranking_preaggregates.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_backfill_missing_ranking_preaggregates.sql>) | Uzupełnienie brakujących pakietów sesji. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_confirmed_round_status.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_confirmed_round_status.sql>) | Status potwierdzonej rundy. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_fix_2021_points_and_missing_result_teams.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_fix_2021_points_and_missing_result_teams.sql>) | Korekta punktów 2021 i braków teamów w wynikach. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_live_round_status.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_live_round_status.sql>) | Status rundy trwającej na żywo. | Migracja — nie wykonuje się przy starcie | — |
| [20260820_round_images_supabase_storage.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260820_round_images_supabase_storage.sql>) | Metadane obrazów rund i zapis w Storage. | Migracja — nie wykonuje się przy starcie | — |
| [20260822_completed_round_status.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260822_completed_round_status.sql>) | Status zakończonej rundy. | Migracja — nie wykonuje się przy starcie | — |
| [20260822_ignore_dsq_in_metric_populations.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260822_ignore_dsq_in_metric_populations.sql>) | Wykluczenie DSQ z populacji obliczeń tempa. | Migracja — nie wykonuje się przy starcie | — |
| [20260822_session_conditions.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260822_session_conditions.sql>) | Warunki przypisane do konkretnej sesji. | Migracja — nie wykonuje się przy starcie | — |
| [20260921_display_metadata.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260921_display_metadata.sql>) | Słowniki etykiet kategorii, sesji i statusów. | Migracja — nie wykonuje się przy starcie | — |
| [20260930_remove_ineffective_detail_projection.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260930_remove_ineffective_detail_projection.sql>) | Usunięcie nieskutecznego wariantu projekcji detali. | Migracja — nie wykonuje się przy starcie | — |
| [20260930_separate_read_packages.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20260930_separate_read_packages.sql>) | Rozdzielenie pakietów rankingowych i szczegółowych. | Migracja — nie wykonuje się przy starcie | — |
| [20261013_round_images_webp.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/20261013_round_images_webp.sql>) | Format WebP i walidacja metadanych obrazów. | Migracja — nie wykonuje się przy starcie | — |
| [optimize_ranking_reads.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/optimize_ranking_reads.sql>) | Dodatkowe funkcje i optymalizacje odczytu rankingu. | Migracja — nie wykonuje się przy starcie | — |
| [point-systems.example.json](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/point-systems.example.json>) | Przykład pliku definicji punktacji do seedera. | Przykład — nie ładuje się sam | — |

## racetrace-api/Database/Tests

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [verify_prepared_ranking_metrics.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/Tests/verify_prepared_ranking_metrics.sql>) | Kontrola poprawności przygotowanych danych rankingu. | Kontrola SQL — ręczne wykonanie | — |
| [verify_ranking_points_summary.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Database/Tests/verify_ranking_points_summary.sql>) | Kontrola podsumowań punktowych rankingu. | Kontrola SQL — ręczne wykonanie | — |

## racetrace-api/DevChecks

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [racetrace-api.dev.http](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/DevChecks/racetrace-api.dev.http>) | Zestaw ręcznych wywołań do lokalnej diagnostyki. | Ręczne testy HTTP | Ujednolicić z drugim plikiem HTTP. |

## racetrace-api/Features/Analytics

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [RoundRaceAnalytics.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Analytics/RoundRaceAnalytics.cs>) | Oblicza statystyki i wskaźniki analizy rundy. | Tak — kod API | — |

## racetrace-api/Features/Fantasy

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [FantasyBacktester.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyBacktester.cs>) | Sprawdza prognozy Fantasy na wcześniejszych wynikach. | Tak — kod API | — |
| [FantasyCalibration.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyCalibration.cs>) | Kalibruje prognozowane pozycje i prawdopodobieństwa. | Tak — kod API | — |
| [FantasyJobCoordinator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyJobCoordinator.cs>) | Koordynuje zadania Fantasy blokadami i leasingiem w bazie. | Tak — kod API | — |
| [FantasyModelConfiguration.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyModelConfiguration.cs>) | Wspólna konfiguracja wersji modelu predykcyjnego Fantasy. | Tak — kod API | — |
| [FantasyOptimizer.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyOptimizer.cs>) | Wybiera skład maksymalizujący punkty w budżecie i limitach. | Tak — kod API | — |
| [FantasyOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyOptions.cs>) | Konfiguracja Fantasy, eventów i lokalnej koordynacji pracy. | Tak — kod API | — |
| [FantasyPreparationService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyPreparationService.cs>) | Przygotowuje prognozy, backtesty i gotowy kontekst Fantasy. | Tak — kod API | — |
| [FantasyPreparedStore.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyPreparedStore.cs>) | Odczytuje i publikuje przygotowane prognozy Fantasy. | Tak — kod API | — |
| [FantasyRacePrediction.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyRacePrediction.cs>) | Ładuje historyczne rundy i wylicza prognozy zawodników. | Tak — kod API | — |
| [FantasyRules2026.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyRules2026.cs>) | Zasady budżetu, składu i punktacji sezonu 2026. | Tak — kod API | Przy kolejnym sezonie wybierać reguły według sezonu. |
| [FantasyScoring.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyScoring.cs>) | Rozkład pozycji przekłada na oczekiwane punkty Fantasy. | Tak — kod API | — |
| [FantasyService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyService.cs>) | Składa odpowiedź Fantasy z cen, prognoz i rekomendacji. | Tak — kod API | — |
| [FantasySyncHostedService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasySyncHostedService.cs>) | W tle synchronizuje ceny i przygotowuje Fantasy. | Tak — kod API | — |
| [FantasyTrainingParticipants.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/FantasyTrainingParticipants.cs>) | Rozpoznaje startujących na podstawie treningów providerów. | Tak — kod API | — |

## racetrace-api/Features/Fantasy/Import

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [FantasyImportModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/FantasyImportModels.cs>) | Modele cen, riderów, statusu i ustawień importu Fantasy. | Tak — modele/kontrakty | — |
| [FantasyPriceSnapshotStore.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/FantasyPriceSnapshotStore.cs>) | Przechowuje historię cen i prób importu Pinkbike. | Tak — kod API | — |
| [FantasyRiderMapper.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/FantasyRiderMapper.cs>) | Łączy tożsamości Pinkbike z riderami w bazie. | Tak — kod API | — |
| [PinkbikeBrowserPage.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/PinkbikeBrowserPage.cs>) | Pobiera stronę przez Playwright, gdy zwykłe HTTP nie wystarcza. | Tak — kod API | — |
| [PinkbikeFantasyImporter.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/PinkbikeFantasyImporter.cs>) | Organizuje import cen przez HTTP, przeglądarkę lub HTML. | Tak — kod API | — |
| [PinkbikeFantasyParser.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Fantasy/Import/PinkbikeFantasyParser.cs>) | Odczytuje ceny i statusy riderów z HTML Pinkbike. | Tak — kod API | — |

## racetrace-api/Features/Imports/ChronoRace

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [ChronoRacePayloadMapper.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Imports/ChronoRace/ChronoRacePayloadMapper.cs>) | Mapuje riderów, wyniki i splity JSON do zapisów importera. | Tak — kod API | — |

## racetrace-api/Features/Metadata

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [DisplayMetadataDto.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Metadata/DisplayMetadataDto.cs>) | Kontrakt słowników kategorii, statusów i sesji. | Tak — kod API | — |
| [DisplayMetadataService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Metadata/DisplayMetadataService.cs>) | Pobiera metadane i uzupełnia aliasy etykiet. | Tak — kod API | — |
| [MetadataController.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Metadata/MetadataController.cs>) | Endpoint słowników używanych do etykiet frontendu. | Tak — endpoint HTTP | — |

## racetrace-api/Features/Metrics

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [MetricsQueryParser.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Metrics/MetricsQueryParser.cs>) | Waliduje i normalizuje filtry metryk. | Tak — kod API | — |

## racetrace-api/Features/Ranking

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [RankingDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Ranking/RankingDtos.cs>) | Publiczny kontrakt rankingu i jego podsumowania. | Tak — modele/kontrakty | — |

## racetrace-api/Features/Results

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [ResultDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Results/ResultDtos.cs>) | Publiczne modele sesji, czasów i wyników. | Tak — modele/kontrakty | — |
| [ResultTimingProjection.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Results/ResultTimingProjection.cs>) | Buduje splity, sektory, gapy, pozycje i perfect run. | Tak — kod API | Rozdzielić przygotowanie referencji od projekcji odpowiedzi. |
| [RoundResultSelection.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Results/RoundResultSelection.cs>) | Wybiera wyniki kategorii Open i najlepszy przejazd w All. | Tak — kod API | — |
| [SessionResultMapper.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Results/SessionResultMapper.cs>) | Mapuje wynik wewnętrzny na publiczny DTO. | Tak — kod API | — |

## racetrace-api/Features/Riders

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [RiderChartStatistics.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Riders/RiderChartStatistics.cs>) | Wyznacza agregaty i serie wykresów ridera. | Tak — kod API | — |
| [RiderDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Riders/RiderDtos.cs>) | Publiczne modele statystyk, tras, rund i overview ridera. | Tak — modele/kontrakty | — |
| [RiderTeamHistoryMapper.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Riders/RiderTeamHistoryMapper.cs>) | Mapuje historię teamów do dwóch istniejących kontraktów JSON. | Tak — kod API | — |
| [RiderTimingStatistics.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Riders/RiderTimingStatistics.cs>) | Liczy sektorowe podsumowania i liczbę wygranych timingów ridera. | Tak — kod API | — |

## racetrace-api/Features/Rounds

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [RoundDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Features/Rounds/RoundDtos.cs>) | Publiczny kontrakt pozycji na liście rund. | Tak — modele/kontrakty | — |

## racetrace-api/Infrastructure

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminAuthorizationFilter.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/AdminAuthorizationFilter.cs>) | Wspólna kontrola uprawnień endpointów administracyjnych. | Tak — kod API | — |
| [ApiAccessMiddleware.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiAccessMiddleware.cs>) | Sprawdza dostęp do ścieżek API i OpenAPI. | Tak — kod API | — |
| [ApiContractExtensions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiContractExtensions.cs>) | Konfiguruje OpenAPI i opisy rzeczywistych kontraktów HTTP. | Tak — kod API | — |
| [ApiException.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiException.cs>) | Kontrolowany wyjątek aplikacyjny z kodem HTTP i błędu. | Tak — kod API | — |
| [ApiExceptionMiddleware.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiExceptionMiddleware.cs>) | Zamienia wyjątki na spójną odpowiedź błędu. | Tak — kod API | — |
| [ApiQueryContracts.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiQueryContracts.cs>) | Opisuje parametry zapytań na potrzeby OpenAPI. | Tak — kod API | — |
| [ApiReadOutputCachePolicy.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ApiReadOutputCachePolicy.cs>) | Dobiera cache odpowiedzi HTTP z uwzględnieniem żądań i rewizji. | Tak — kod API | — |
| [ISupabaseRestClient.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ISupabaseRestClient.cs>) | Kontrakt dostępu HTTP/RPC do bazy; podmieniany w testach. | Tak — kod API | — |
| [ServiceCollectionExtensions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/ServiceCollectionExtensions.cs>) | Rejestruje DI, konfigurację, klientów HTTP i cache. | Tak — kod API | — |
| [SupabaseApiException.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/SupabaseApiException.cs>) | Wyjątek zawierający szczegóły błędu Supabase/PostgREST. | Tak — kod API | — |
| [SupabaseQuery.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/SupabaseQuery.cs>) | Buduje parametry select, filtrów, sortowania i paginacji. | Tak — kod API | — |
| [SupabaseQueryResult.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/SupabaseQueryResult.cs>) | Wynik odczytu wraz z liczbą rekordów. | Tak — kod API | — |
| [SupabaseRestClient.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/SupabaseRestClient.cs>) | Realizuje odczyty, zapisy i RPC przez PostgREST. | Tak — kod API | — |
| [SupabaseRole.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/SupabaseRole.cs>) | Rozróżnia dostęp anon i service-role. | Tak — kod API | — |

## racetrace-api/Infrastructure/Calculations

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AlgorithmCatalog.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Calculations/AlgorithmCatalog.cs>) | Wersje, konfiguracje i fingerprinty kalkulatorów. | Tak — kod API | — |
| [CalculationProvenance.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Calculations/CalculationProvenance.cs>) | Zbiera metadane obliczeń i dodaje je do odpowiedzi. | Tak — kod API | — |
| [DataRevisionService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Calculations/DataRevisionService.cs>) | Odczytuje rewizje danych i współdzieli krótki cache kontroli. | Tak — kod API | — |
| [DataRevisionSnapshot.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Calculations/DataRevisionSnapshot.cs>) | Buduje identyfikator rewizji dla zakresu danych. | Tak — kod API | — |
| [RequestCalculationIdentity.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Calculations/RequestCalculationIdentity.cs>) | Łączy kontekst requestu, rewizję danych i wersję algorytmu. | Tak — kod API | — |

## racetrace-api/Infrastructure/Supabase

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [CompactMetricsPackageParser.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Supabase/CompactMetricsPackageParser.cs>) | Odczytuje zwarte pakiety wejściowe i ścieżki zgodności. | Tak — kod API | Oddzielić bieżący format od parserów wersji historycznych. |

## racetrace-api/Infrastructure/Telemetry

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [ApiTelemetry.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/ApiTelemetry.cs>) | Liczniki, pomiary czasu i metryki operacji API. | Tak — kod API | — |
| [ApiTelemetryMiddleware.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/ApiTelemetryMiddleware.cs>) | Mierzy cały request i uzupełnia nagłówki diagnostyczne. | Tak — kod API | — |
| [CountingStream.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/CountingStream.cs>) | Liczy przesyłane bajty bez dodatkowego buforowania całości. | Tak — kod API | — |
| [SupabaseTelemetryHandler.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/SupabaseTelemetryHandler.cs>) | Mierzy transfer i czas komunikacji z Supabase. | Tak — kod API | — |
| [TelemetryOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/TelemetryOptions.cs>) | Ustawienia monitoringu i eksportera telemetrycznego. | Tak — kod API | — |
| [TelemetryServiceCollectionExtensions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Infrastructure/Telemetry/TelemetryServiceCollectionExtensions.cs>) | Rejestruje monitoring i OpenTelemetry. | Tak — kod API | — |

## racetrace-api/Models

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/AdminDtos.cs>) | Modele żądań edycji i importów administracyjnych. | Tak — modele/kontrakty | — |
| [AnalyticsDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/AnalyticsDtos.cs>) | Kontrakty profilu rundy, Field Spread i Pressure Performance. | Tak — modele/kontrakty | — |
| [ApiErrorResponse.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/ApiErrorResponse.cs>) | Spójny format odpowiedzi błędów. | Tak — modele/kontrakty | — |
| [CompareDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/CompareDtos.cs>) | Kontrakt porównania dwóch riderów. | Tak — modele/kontrakty | — |
| [FantasyDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/FantasyDtos.cs>) | Kontrakty cen, prognoz, teamu i zasad Fantasy. | Tak — modele/kontrakty | — |
| [ListResponse.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/ListResponse.cs>) | Wspólna lista wyników z metadanymi paginacji. | Tak — modele/kontrakty | — |
| [MetricsModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/MetricsModels.cs>) | Kontekst filtrów i składowe metryk zawodników. | Tak — modele/kontrakty | — |
| [PageRequest.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/PageRequest.cs>) | Ustawienia numeru i rozmiaru strony. | Tak — modele/kontrakty | — |
| [PointAwardModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/PointAwardModels.cs>) | Modele sesji i reguł przyznawania punktów. | Tak — modele/kontrakty | — |
| [PointSystemSeedModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/PointSystemSeedModels.cs>) | Kontrakt pliku inicjalizacji punktacji i wynik operacji. | Tak — modele/kontrakty | — |
| [PublicApiDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/PublicApiDtos.cs>) | Pozostałe publiczne kontrakty danych API. | Tak — modele/kontrakty | — |
| [RankingDataBundle.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RankingDataBundle.cs>) | Pakiet sesji, wyników, splitów i punktów do obliczeń. | Tak — modele/kontrakty | — |
| [RankingReadData.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RankingReadData.cs>) | Typowany odczyt rankingu i zgodność ze starszą reprezentacją. | Tak — modele/kontrakty | — |
| [RankingResponse.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RankingResponse.cs>) | Wewnętrzna odpowiedź rankingu przed publicznym mapowaniem. | Tak — modele/kontrakty | — |
| [RiderSimilarityDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RiderSimilarityDtos.cs>) | Kontrakt grup i dendrogramu podobieństwa riderów. | Tak — modele/kontrakty | — |
| [RoundImageFile.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RoundImageFile.cs>) | Model obrazu rundy i metadanych pliku. | Tak — modele/kontrakty | — |
| [RoundRaceAnalyticsDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/RoundRaceAnalyticsDtos.cs>) | Kontrakt zaawansowanej analityki rundy. | Tak — modele/kontrakty | — |
| [SeasonSummaryDtos.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Models/SeasonSummaryDtos.cs>) | Kontrakty podsumowania sezonu i prognozy podium. | Tak — modele/kontrakty | — |

## racetrace-api/Properties

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AssemblyInfo.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Properties/AssemblyInfo.cs>) | Udostępnia elementy internal testom i narzędziom. | Kompilacja | — |
| [launchSettings.json](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Properties/launchSettings.json>) | Profile, adresy i porty lokalnego startu API. | Lokalne uruchamianie | Utrzymać spójność portu z proxy frontendu. |

## racetrace-api/Services

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AdminDataService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/AdminDataService.cs>) | Edytuje encje, usuwa rundy i zarządza zdjęciami. | Tak — kod API | Podzielić operacje edycji, obrazów i usuwania. |
| [AdminGuard.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/AdminGuard.cs>) | Weryfikuje sekret administracyjny. | Tak — kod API | — |
| [AdminSessionReimportService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/AdminSessionReimportService.cs>) | Zastępuje dane wybranej sesji poprawionym importem. | Tak — kod API | — |
| [AlgorithmOptions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/AlgorithmOptions.cs>) | Konfiguracje wag, progów i kalibracji kalkulatorów. | Tak — kod API | — |
| [AnalyticsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/AnalyticsService.cs>) | Przygotowuje profil rundy i Pressure Performance. | Tak — kod API | — |
| [ApiAccessGuard.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ApiAccessGuard.cs>) | Weryfikuje uprawnienia do odczytu API. | Tak — kod API | — |
| [BearerTokenValidator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/BearerTokenValidator.cs>) | Wspólna walidacja tokena Bearer. | Tak — kod API | — |
| [ChronoRaceImportService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ChronoRaceImportService.cs>) | Tworzy i aktualizuje encje oraz wyniki importowanej sesji. | Tak — kod API | Rozdzielić zapis encji od organizacji całego importu. |
| [ChronoRaceJsonFetcher.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ChronoRaceJsonFetcher.cs>) | Pobiera i waliduje źródłowy JSON ChronoRace. | Tak — kod API | — |
| [ChronoRaceLiveProxyService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ChronoRaceLiveProxyService.cs>) | Proxy danych live z kontrolą źródła i cache. | Tak — kod API | — |
| [ChronoRaceRoundFormImportService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ChronoRaceRoundFormImportService.cs>) | Organizuje import pełnej rundy i jej sesji. | Tak — kod API | — |
| [CompareService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/CompareService.cs>) | Wyznacza wspólne dane, metryki i porównanie riderów. | Tak — kod API | Oddzielić dobór wspólnych sesji od budowania DTO. |
| [CompetitionClassWeighting.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/CompetitionClassWeighting.cs>) | Dobiera wagi i priorytety klas zawodów do obliczeń. | Tak — kod API | — |
| [ConditionsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ConditionsService.cs>) | Pobiera i filtruje słownik warunków. | Tak — kod API | — |
| [ConsistencyModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ConsistencyModels.cs>) | Modele wejścia i wyników kalkulatora Consistency. | Tak — modele/kontrakty | — |
| [ConsistencyStatistics.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ConsistencyStatistics.cs>) | Statystyki i miary rozrzutu używane przez Consistency. | Tak — kod API | — |
| [DomainQueryService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DomainQueryService.cs>) | Wspólny odczyt kontekstu, wyników, pakietów i relacji. | Tak — kod API | Największy kandydat do podziału: kontekst, pakiety i historia teamów. |
| [DownhillConsistencyCalculator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DownhillConsistencyCalculator.cs>) | Liczy Consistency, Confidence i składowe porównywalnych przejazdów. | Tak — kod API | — |
| [DownhillPaceCalculator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DownhillPaceCalculator.cs>) | Liczy Pace, Evidence i korekty incydentów. | Tak — kod API | Dalszy podział tylko przy zachowaniu kolejności i zgodności obliczeń. |
| [DownhillRatingCalculator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DownhillRatingCalculator.cs>) | Łączy składowe oceny w końcowy Rating. | Tak — kod API | — |
| [DownhillResultScoreCalculator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/DownhillResultScoreCalculator.cs>) | Ocenia pozycje i agreguje wynik sesji z wagami. | Tak — kod API | — |
| [ImportResultJsonNormalizer.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ImportResultJsonNormalizer.cs>) | Rozpoznaje format JSON i wybiera adapter importu. | Tak — kod API | — |
| [JsonNodeExtensions.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/JsonNodeExtensions.cs>) | Bezpiecznie odczytuje pola dynamicznych struktur JSON. | Tak — kod API | — |
| [JsonSerializerProxy.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/JsonSerializerProxy.cs>) | Serializuje modele do JsonObject z polityką nazw web. | Tak — kod API | — |
| [JsonSorting.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/JsonSorting.cs>) | Wspólne sortowanie kolekcji JsonObject. | Tak — kod API | — |
| [LiveChampionshipCalculator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/LiveChampionshipCalculator.cs>) | Wylicza punkty top 2 i możliwość zmiany zwycięzcy sezonu. | Tak — kod API | — |
| [LiveChampionshipService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/LiveChampionshipService.cs>) | Buduje kontekst oraz projekcję generalki z danych live. | Tak — kod API | — |
| [LiveService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/LiveService.cs>) | Obsługuje bazodanową listę sesji i leaderboard Live. | Tak — kod API | Oddzielić kontrakt bazodanowy od streamu zewnętrznych providerów w dokumentacji. |
| [MetricsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/MetricsService.cs>) | Uruchamia kalkulatory i łączy metryki zawodników. | Tak — kod API | — |
| [PaceModels.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PaceModels.cs>) | Modele sesji, sektorów, statystyk i wyników Pace. | Tak — modele/kontrakty | — |
| [PaceService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PaceService.cs>) | Przygotowuje dane całej stawki i organizuje obliczenia metryk. | Tak — kod API | Oddzielić mapowanie danych JSON od przygotowania kalkulatorów. |
| [PaceStatistics.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PaceStatistics.cs>) | Wspólne statystyki i rankingi potrzebne do Pace. | Tak — kod API | — |
| [PodiumPredictionBacktester.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PodiumPredictionBacktester.cs>) | Ocenia skuteczność wariantów predykcji na danych historycznych. | Backtesty i testy — nie requesty API | — |
| [PodiumPredictionFeatureBuilder.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PodiumPredictionFeatureBuilder.cs>) | Buduje cechy ridera dla modelu prognozy podium. | Tak — kod API | — |
| [PodiumPredictionModel.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PodiumPredictionModel.cs>) | Model probabilistyczny i symulacje przyszłego wyniku. | Tak — kod API | — |
| [PodiumPredictorService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PodiumPredictorService.cs>) | Łączy kontekst nadchodzącej rundy z prognozami riderów. | Tak — kod API | — |
| [PointAwardService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PointAwardService.cs>) | Przyznaje punkty według właściwego systemu, kategorii i sesji. | Tak — kod API | — |
| [PointSystemSeedHostedService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PointSystemSeedHostedService.cs>) | Opcjonalnie inicjalizuje punktację z pliku przy starcie API. | Po ustawieniu ścieżki pliku seed | — |
| [PointSystemSeedValidator.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PointSystemSeedValidator.cs>) | Waliduje plik i spójność definicji punktacji. | Tak — kod API | — |
| [PointSystemSeeder.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PointSystemSeeder.cs>) | Zapisuje definicje systemów punktacji w bazie. | Tak — kod API | — |
| [PointsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PointsService.cs>) | Udostępnia punktację i organizuje jej przeliczanie. | Tak — kod API | — |
| [PublicApiCodes.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PublicApiCodes.cs>) | Normalizuje starsze aliasy kategorii i sesji. | Tak — kod API | Ograniczyć zamknięte listy tam, gdzie dostępny jest słownik bazy. |
| [PublicDtoMapping.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/PublicDtoMapping.cs>) | Wspólne mapowanie identyfikatorów i pól publicznych DTO. | Tak — kod API | — |
| [QueryValidationService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/QueryValidationService.cs>) | Waliduje zapytania, filtry, paginację i sortowanie. | Tak — kod API | — |
| [RankingPreaggregationService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RankingPreaggregationService.cs>) | Odświeża pakiety wejściowe; zachowuje starszą ścieżkę przygotowania. | Refresh: import/admin; Prepare: narzędzie | Wyraźniej oddzielić legacy PrepareAsync używane w narzędziu. |
| [RankingService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RankingService.cs>) | Liczy ranking, porównania pozycji i cache snapshotów. | Tak — kod API | Oddzielić snapshot i porównanie pozycji od projekcji wierszy. |
| [RankingSingleFlight.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RankingSingleFlight.cs>) | Łączy identyczne obliczenia trwające równocześnie. | Tak — kod API | — |
| [ResponseFactory.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ResponseFactory.cs>) | Buduje wspólne odpowiedzi list i paginacji. | Tak — kod API | — |
| [ResultsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/ResultsService.cs>) | Pobiera wyniki i składa odpowiedzi sesji oraz rund. | Tak — kod API | Oddzielić pobranie danych od mapowania i analityki. |
| [RiderCategoryResolver.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RiderCategoryResolver.cs>) | Wybiera kategorię ridera z wieku i dostępnych startów. | Tak — kod API | — |
| [RiderProfileImageImportService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RiderProfileImageImportService.cs>) | Pobiera i przypisuje zdjęcia zawodników. | Tak — kod API | — |
| [RiderSimilarityMath.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RiderSimilarityMath.cs>) | Liczy podobieństwo profili i grupowanie riderów. | Tak — kod API | — |
| [RiderSimilarityService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RiderSimilarityService.cs>) | Przygotowuje dane i odpowiedź endpointu podobieństwa. | Tak — endpoint HTTP; brak sekcji UI | — |
| [RidersService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RidersService.cs>) | Overview, metryki, wyniki, trasy i historia zawodnika. | Tak — kod API | Podzielić overview, listy i historyczne endpointy według odpowiedzialności. |
| [RoundImageBackfillService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RoundImageBackfillService.cs>) | Konwertuje partiami istniejące obrazy PNG do WebP. | Tak — kod API | — |
| [RoundImagePayloadParser.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RoundImagePayloadParser.cs>) | Waliduje obraz i przygotowuje WebP do zapisu. | Tak — kod API | — |
| [RoundImageStorageService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RoundImageStorageService.cs>) | Wysyła, pobiera i adresuje obrazy w Storage. | Tak — kod API | — |
| [RoundsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/RoundsService.cs>) | Lista rund, sezony, sesje, zdjęcia i wybór wyniku. | Tak — kod API | Rozdzielić obrazy od zapytań o rundy. |
| [SearchService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/SearchService.cs>) | Przeszukuje riderów, teamy, trasy i rundy. | Tak — kod API | — |
| [SeasonSummaryService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/SeasonSummaryService.cs>) | Buduje podsumowanie sezonu oraz kontekst rund Home. | Tak — kod API | — |
| [SectorIncidentDetector.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/SectorIncidentDetector.cs>) | Wykrywa anomalie sektorów i opisuje utratę czasu. | Tak — kod API | — |
| [SessionsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/SessionsService.cs>) | Dobiera sesje wspólne dla wskazanych riderów. | Tak — kod API | — |
| [TeamsService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/TeamsService.cs>) | Lista i szczegóły teamów z członkostwem według historii. | Tak — kod API | — |
| [TissotTimingJsonFetcher.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/TissotTimingJsonFetcher.cs>) | Pobiera i rozpoznaje JSON wyników Tissot. | Tak — kod API | — |
| [TracksService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/TracksService.cs>) | Lista tras, powiązane rundy i rekordy. | Tak — kod API | — |
| [UciMtbWorldSeriesSupplementService.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/UciMtbWorldSeriesSupplementService.cs>) | Uzupełnia brakujące wyniki z alternatywnego źródła UCI. | Tak — kod API | Oddzielić dopasowanie zawodników od organizacji pobierania i zapisu. |
| [UciResultsPdfParser.cs](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/Services/UciResultsPdfParser.cs>) | Odczytuje wyniki i splity z PDF UCI/Tissot. | Tak — kod API | — |

## racetrace-api/supabase

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [.gitignore](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/supabase/.gitignore>) | Wyklucza lokalne artefakty narzędzia Supabase z Git. | Narzędzie Git | Ujednolicić położenie konfiguracji Supabase przy porządkowaniu migracji. |

## racetrace-api/supabase/migrations

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [20260930212704_pace_incident_field_context.sql](<C:/Work/Projects/race-trace/racetrace-api/racetrace-api/supabase/migrations/20260930212704_pace_incident_field_context.sql>) | Kontekst stawki dla przygotowania danych incydentów. | Migracja — nie wykonuje się przy starcie | — |

## supabase/migrations

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [20261003214005_prepared_ranking_metrics.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261003214005_prepared_ranking_metrics.sql>) | Historyczne przygotowane dane metryk rankingu. | Migracja — nie wykonuje się przy starcie | — |
| [20261003215623_prepared_rider_detail_read.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261003215623_prepared_rider_detail_read.sql>) | Historyczna zoptymalizowana ścieżka odczytu detali. | Migracja — nie wykonuje się przy starcie | — |
| [20261010210121_calculation_revisions.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261010210121_calculation_revisions.sql>) | Rewizje danych i unieważnianie cache obliczeń. | Migracja — nie wykonuje się przy starcie | — |
| [20261010210124_fantasy_job_coordination.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261010210124_fantasy_job_coordination.sql>) | Leasing, status i współdzielenie zadań Fantasy. | Migracja — nie wykonuje się przy starcie | — |
| [20261010224954_calculator_neutral_input_packages.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261010224954_calculator_neutral_input_packages.sql>) | Pakiety surowych wejść niezależne od kalkulatora. | Migracja — nie wykonuje się przy starcie | — |
| [20261010225709_input_read_identity_parity.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261010225709_input_read_identity_parity.sql>) | Spójność identyfikatorów i odpowiedzi ścieżek odczytu. | Migracja — nie wykonuje się przy starcie | — |
| [20261011000100_input_sector_rank_validity.sql](<C:/Work/Projects/race-trace/racetrace-api/supabase/migrations/20261011000100_input_sector_rank_validity.sql>) | Prawidłowy dobór czasów do sektorowych rankingów. | Migracja — nie wykonuje się przy starcie | — |

## tools/CalculatorParity

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [CalculatorParity.csproj](<C:/Work/Projects/race-trace/racetrace-api/tools/CalculatorParity/CalculatorParity.csproj>) | Zależności i ustawienia projektu narzędziowego .NET. | Build narzędzia | — |
| [Program.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/CalculatorParity/Program.cs>) | Porównuje wyjścia kalkulatorów między wersjami kodu. | Narzędzie uruchamiane ręcznie | — |

## tools

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [Measure-ReadParity.ps1](<C:/Work/Projects/race-trace/racetrace-api/tools/Measure-ReadParity.ps1>) | Uruchamia pomiary i porównania odczytu z PowerShell. | Narzędzie uruchamiane ręcznie | — |
| [incident-conservative-refinement.py](<C:/Work/Projects/race-trace/racetrace-api/tools/incident-conservative-refinement.py>) | Analizuje wyniki konserwatywnego wariantu detektora. | Narzędzie uruchamiane ręcznie | — |
| [incident-experiments.py](<C:/Work/Projects/race-trace/racetrace-api/tools/incident-experiments.py>) | Porównuje warianty wykrywania incydentów. | Narzędzie uruchamiane ręcznie | — |
| [ranking-incident-comparison.py](<C:/Work/Projects/race-trace/racetrace-api/tools/ranking-incident-comparison.py>) | Porównuje metryki rankingu przed i po zmianie detektora. | Narzędzie uruchamiane ręcznie | — |
| [test_calculation_revisions.py](<C:/Work/Projects/race-trace/racetrace-api/tools/test_calculation_revisions.py>) | Testuje SQL rewizji i unieważniania danych. | Narzędzie uruchamiane ręcznie | — |
| [test_fantasy_jobs.py](<C:/Work/Projects/race-trace/racetrace-api/tools/test_fantasy_jobs.py>) | Testuje SQL blokad i koordynacji zadań Fantasy. | Narzędzie uruchamiane ręcznie | — |

## tools/OpenApiExport

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [OpenApiExport.csproj](<C:/Work/Projects/race-trace/racetrace-api/tools/OpenApiExport/OpenApiExport.csproj>) | Zależności i ustawienia projektu narzędziowego .NET. | Build narzędzia | — |
| [Program.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/OpenApiExport/Program.cs>) | Eksportuje OpenAPI bez uruchamiania publicznego serwera. | Narzędzie uruchamiane ręcznie | — |

## tools/ReadPathParity

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [Parity.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/Parity.cs>) | Porównuje pełne odpowiedzi obu ścieżek. | Narzędzie uruchamiane ręcznie | — |
| [Program.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/Program.cs>) | Uruchamia pomiary oraz porównania surowych i pakietowych odczytów. | Narzędzie uruchamiane ręcznie | — |
| [README.md](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/README.md>) | Instrukcja narzędzia pomiarów i zgodności odczytu. | Instrukcja narzędzia | — |
| [ReadPathParity.csproj](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/ReadPathParity.csproj>) | Zależności i ustawienia projektu narzędziowego .NET. | Build narzędzia | — |
| [Stages.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/Stages.cs>) | Mierzy kolejne etapy przetwarzania danych. | Narzędzie uruchamiane ręcznie | — |
| [input-sector-rank-regression.sql](<C:/Work/Projects/race-trace/racetrace-api/tools/ReadPathParity/input-sector-rank-regression.sql>) | Przypadek SQL regresji prawidłowości pozycji sektorów. | Ręczny test SQL | — |

## tools/racetrace-api.PodiumBacktest

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [AssemblyInfo.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/AssemblyInfo.cs>) | Ustawienia kompilacji narzędzia backtestowego. | Narzędzie uruchamiane ręcznie | — |
| [CleanPaceExperiment.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/CleanPaceExperiment.cs>) | Eksperymentuje z prognozą opartą o tempo bez incydentów. | Narzędzie uruchamiane ręcznie | — |
| [ConfirmedIncidentAudit.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/ConfirmedIncidentAudit.cs>) | Zestawia wykrycia z potwierdzonymi incydentami. | Narzędzie uruchamiane ręcznie | — |
| [HistoricalData.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/HistoricalData.cs>) | Wczytuje i przygotowuje dane historycznych rund. | Narzędzie uruchamiane ręcznie | — |
| [IncidentAudit.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/IncidentAudit.cs>) | Generuje listy wykrytych anomalii i korekt. | Narzędzie uruchamiane ręcznie | — |
| [Program.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/Program.cs>) | Uruchamia historyczne testy modeli predykcji. | Narzędzie uruchamiane ręcznie | — |
| [README.md](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/README.md>) | Instrukcja uruchamiania eksperymentu podium. | Instrukcja narzędzia | — |
| [RankingIncidentComparison.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/RankingIncidentComparison.cs>) | Porównuje oceny riderów dla wariantów detektora. | Narzędzie uruchamiane ręcznie | — |
| [ReadOnlyCacheHandler.cs](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/ReadOnlyCacheHandler.cs>) | Cache odczytów HTTP dla powtarzalnych eksperymentów. | Narzędzie uruchamiane ręcznie | — |
| [racetrace-api.PodiumBacktest.csproj](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/racetrace-api.PodiumBacktest.csproj>) | Zależności i ustawienia projektu narzędziowego .NET. | Build narzędzia | — |

## tools/racetrace-api.PodiumBacktest/Fixtures

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [incidents-2026.csv](<C:/Work/Projects/race-trace/racetrace-api/tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv>) | Potwierdzone incydenty do walidacji detektora. | Dane do audytu | — |

# Frontend — racetrace-web-app

## (katalog główny)

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [.env.example](<C:/Work/Projects/race-trace/racetrace-web-app/.env.example>) | Przykładowe zmienne środowiskowe frontendu. | Konfiguracja — szablon | Aktualizować razem z walidacją env.ts. |
| [.gitignore](<C:/Work/Projects/race-trace/racetrace-web-app/.gitignore>) | Wyklucza buildy, cache, sekrety i pliki lokalne z Git. | Narzędzie Git | — |
| [README.md](<C:/Work/Projects/race-trace/racetrace-web-app/README.md>) | Instrukcja uruchamiania i pracy z frontendem. | Dokumentacja | — |
| [eslint.config.js](<C:/Work/Projects/race-trace/racetrace-web-app/eslint.config.js>) | Reguły lintowania TypeScript, React i hooków. | Lint | — |
| [index.html](<C:/Work/Projects/race-trace/racetrace-web-app/index.html>) | Dokument startowy SPA, metadane i aktualna favicon. | Start aplikacji | — |
| [package-lock.json](<C:/Work/Projects/race-trace/racetrace-web-app/package-lock.json>) | Dokładne wersje zależności dla npm. | Tylko instalacja przez npm | Przy wyborze pnpm usunąć z aktywnego workflow. |
| [package.json](<C:/Work/Projects/race-trace/racetrace-web-app/package.json>) | Zależności i polecenia uruchamiania, testów i builda. | Instalacja/build | Wskazać jeden package manager. |
| [pnpm-lock.yaml](<C:/Work/Projects/race-trace/racetrace-web-app/pnpm-lock.yaml>) | Dokładne wersje zależności dla pnpm. | Tylko instalacja przez pnpm | Nie utrzymywać równolegle konkurencyjnego lockfile. |
| [pnpm-workspace.yaml](<C:/Work/Projects/race-trace/racetrace-web-app/pnpm-workspace.yaml>) | Ustawienia workspace i instalacji pnpm. | Narzędzie pnpm | — |
| [tsconfig.app.json](<C:/Work/Projects/race-trace/racetrace-web-app/tsconfig.app.json>) | Ścisłe reguły TypeScript dla kodu aplikacji. | TypeScript/build | — |
| [tsconfig.json](<C:/Work/Projects/race-trace/racetrace-web-app/tsconfig.json>) | Łączy konfiguracje kompilacji aplikacji i narzędzi. | TypeScript/build | — |
| [tsconfig.node.json](<C:/Work/Projects/race-trace/racetrace-web-app/tsconfig.node.json>) | Reguły TypeScript dla konfiguracji narzędzi. | TypeScript/build | — |
| [vite.config.ts](<C:/Work/Projects/race-trace/racetrace-web-app/vite.config.ts>) | Konfiguracja Vite oraz lokalnego proxy i sekretu API. | Dev/build | Uzgodnić stary UserSecretsId z racetrace-api-2026 w API. |
| [vitest.config.ts](<C:/Work/Projects/race-trace/racetrace-web-app/vitest.config.ts>) | Konfiguracja środowiska i odkrywania testów Vitest. | Testy | — |

## public

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [favicon.svg](<C:/Work/Projects/race-trace/racetrace-web-app/public/favicon.svg>) | Dodatkowa ikona witryny. | Brak odwołań w repo | Kandydat do usunięcia; brak odniesień w kodzie i HTML. |
| [icons.svg](<C:/Work/Projects/race-trace/racetrace-web-app/public/icons.svg>) | Dodatkowy zestaw ikon SVG. | Brak odwołań w repo | Kandydat do usunięcia; brak odniesień w kodzie i HTML. |

## scripts

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [api-contracts.check.mjs](<C:/Work/Projects/race-trace/racetrace-web-app/scripts/api-contracts.check.mjs>) | Testuje kontrolę świeżości i generowanie kontraktów. | Test narzędzia kontraktów | — |
| [api-contracts.mjs](<C:/Work/Projects/race-trace/racetrace-web-app/scripts/api-contracts.mjs>) | Eksportuje, generuje i porównuje kontrakty API. | Skrypty package.json | — |

## src/app

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [app.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/app.tsx>) | Łączy aplikację z routerem. | Tak — osiągalny z aplikacji | — |
| [provider.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/provider.tsx>) | Udostępnia wspólny QueryClient komponentom. | Tak — osiągalny z aplikacji | — |
| [router.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/router.tsx>) | Definiuje strony, lazy loading i granice błędów. | Tak — osiągalny z aplikacji | — |

## src/app/routes

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [legacy-route-redirects.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/routes/legacy-route-redirects.tsx>) | Przekierowuje starsze adresy do obecnych widoków. | Tak — osiągalny z aplikacji | Sprawdzić zachowanie query string przy przekierowaniach. |
| [route-states.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/routes/route-states.module.css>) | Style: Widoki błędu routingu, 404 i skeleton strony. | Tak — import CSS | — |
| [route-states.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/routes/route-states.tsx>) | Widoki błędu routingu, 404 i skeleton strony. | Tak — osiągalny z aplikacji | — |
| [use-required-route-param.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/app/routes/use-required-route-param.ts>) | Odczytuje wymagany parametr adresu; obsługuje jego brak. | Tak — osiągalny z aplikacji | — |

## src/assets/fonts

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [IBMPlexMono-Text-Latin1.woff2](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/fonts/IBMPlexMono-Text-Latin1.woff2>) | Starszy statyczny plik fontu IBM Plex Mono. | Brak odwołań w repo | Kandydat do usunięcia; brak odniesień w kodzie i HTML. |

## src/assets

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [hero.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/hero.png>) | Przykładowe zdjęcie rundy dla danych demo. | Demo — obecnie wyłączone | Ładować dopiero w trybie demo, jeśli będzie potrzebne. |
| [info-hexagon.svg](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/info-hexagon.svg>) | Ikona wyjaśnienia sekcji Analytics. | Tak — odwołanie/import | — |
| [round-placeholder.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/round-placeholder.png>) | Placeholder rundy bez zdjęcia. | Tak — odwołanie/import | — |
| [vite.svg](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/vite.svg>) | Pozostałe logo szablonu Vite. | Brak odwołań w repo | Kandydat do usunięcia; brak odniesień w kodzie i HTML. |

## src/assets/icons

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [check.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/check.png>) | Ikona zaznaczenia. | Brak odwołań w repo | Kandydat do usunięcia; brak odniesień w kodzie i HTML. |
| [chevron.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/chevron.png>) | Strzałka rozwijania kontrolek. | Tak — odwołanie/import | — |
| [close.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/close.png>) | Ikona zamknięcia/wyczyszczenia. | Tak — odwołanie/import | — |
| [failed.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/failed.png>) | Ikona stanu błędu pobierania. | Tak — odwołanie/import | — |
| [no-data.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/no-data.png>) | Ikona braku danych. | Tak — odwołanie/import | — |
| [rider-dot.svg](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/rider-dot.svg>) | Ikona zawodnika w modalu wyboru. | Tak — odwołanie/import | — |
| [switch.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/icons/switch.png>) | Ikona zmiany ridera w Compare. | Tak — odwołanie/import | — |

## src/assets/logos

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [racetrace-favicon-v2.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/logos/racetrace-favicon-v2.png>) | Wariant ikony aplikacji. | Brak odwołań w repo | Kandydat do usunięcia po sprawdzeniu użycia poza repo. |
| [racetrace-favicon-v3.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/logos/racetrace-favicon-v3.png>) | Wariant ikony aplikacji. | Tak — HTML lub import | — |
| [racetrace-favicon.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/logos/racetrace-favicon.png>) | Wariant ikony aplikacji. | Brak odwołań w repo | Kandydat do usunięcia po sprawdzeniu użycia poza repo. |
| [racetrace-mark.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/logos/racetrace-mark.png>) | Znak aplikacji używany w nawigacji. | Tak — HTML lub import | — |

## src/assets/riders

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [rider-blue.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-blue.png>) | Wariant kolorystyczny grafiki zawodnika: blue. | Tak — wybór grafiki/Compare | — |
| [rider-green.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-green.png>) | Wariant kolorystyczny grafiki zawodnika: green. | Tak — wybór grafiki/Compare | — |
| [rider-red-white.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-red-white.png>) | Wariant kolorystyczny grafiki zawodnika: red-white. | Tak — wybór grafiki/Compare | — |
| [rider-red.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-red.png>) | Wariant kolorystyczny grafiki zawodnika: red. | Tak — wybór grafiki/Compare | — |
| [rider-white.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-white.png>) | Wariant kolorystyczny grafiki zawodnika: white. | Tak — wybór grafiki/Compare | — |
| [rider-yellow-black.png](<C:/Work/Projects/race-trace/racetrace-web-app/src/assets/riders/rider-yellow-black.png>) | Wariant kolorystyczny grafiki zawodnika: yellow-black. | Tak — wybór grafiki/Compare | — |

## src/components/charts

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [chart-data.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/chart-data.test.ts>) | Testy: Wyznacza domeny osi, ticki i dane pomocnicze wykresów. | Odkrywany przez Vitest | — |
| [chart-data.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/chart-data.ts>) | Wyznacza domeny osi, ticki i dane pomocnicze wykresów. | Tak — osiągalny z aplikacji | — |
| [charts.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/charts.module.css>) | Wspólne style osi, wykresów i aktywnych punktów. | Tak — import CSS | — |
| [metric-scatter-chart.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/metric-scatter-chart.module.css>) | Style: Wspólny scatter Pace/Consistency z tooltipami i podświetleniem. | Tak — import CSS | — |
| [metric-scatter-chart.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/metric-scatter-chart.tsx>) | Wspólny scatter Pace/Consistency z tooltipami i podświetleniem. | Tak — osiągalny z aplikacji | — |
| [performance-radar.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/performance-radar.tsx>) | Rysuje profil osiągów zawodnika jako radar. | Tak — osiągalny z aplikacji | — |
| [position-chart.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/charts/position-chart.tsx>) | Wspólny wykres pozycji, punktów i ratingów. | Tak — osiągalny z aplikacji | Rozważyć oddzielenie przygotowania tooltipów od renderowania. |

## src/components/data-display

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [animated-number.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/animated-number.tsx>) | Animuje liczby podczas ładowania i zmiany danych. | Tak — osiągalny z aplikacji | — |
| [cycling-text.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/cycling-text.tsx>) | Animuje tekst losowanymi literami i cyframi. | Tak — osiągalny z aplikacji | — |
| [data-table.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/data-table.module.css>) | Style: Wspólne elementy semantycznej tabeli i sortowania. | Tak — import CSS | — |
| [data-table.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/data-table.tsx>) | Wspólne elementy semantycznej tabeli i sortowania. | Tak — osiągalny z aplikacji | — |
| [details-disclosure.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/details-disclosure.tsx>) | Rozwija i zwija blok szczegółów diagnostycznych. | Obecnie tylko ukryte Request Details | — |
| [event-class-badge.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/event-class-badge.module.css>) | Style: Wyświetla skrót klasy i przypisany lub neutralny kolor. | Tak — import CSS | — |
| [event-class-badge.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/event-class-badge.test.tsx>) | Testy: Wyświetla skrót klasy i przypisany lub neutralny kolor. | Odkrywany przez Vitest | — |
| [event-class-badge.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/event-class-badge.tsx>) | Wyświetla skrót klasy i przypisany lub neutralny kolor. | Tak — osiągalny z aplikacji | — |
| [performance-value.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/performance-value.tsx>) | Formatuje Pace/Consistency i dobiera kolor oceny. | Tak — osiągalny z aplikacji | — |
| [rating-value.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/rating-value.module.css>) | Style: Formatuje Rating, kolor i animację wartości. | Tak — import CSS | — |
| [rating-value.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/rating-value.tsx>) | Formatuje Rating, kolor i animację wartości. | Tak — osiągalny z aplikacji | — |
| [request-details.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/request-details.module.css>) | Style: Pokazuje transfer, czas oraz etapy obsługi requestu. | Style ukrytych Request Details | — |
| [request-details.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/request-details.tsx>) | Pokazuje transfer, czas oraz etapy obsługi requestu. | Ukryte: SHOW_RANKING_REQUEST_DETAILS=false | Nie liczyć niewyświetlanego licznika values. |
| [rider-cell.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/rider-cell.module.css>) | Style: Nazwa, kraj i team ridera oraz link do detali. | Tak — import CSS | — |
| [rider-cell.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/data-display/rider-cell.tsx>) | Nazwa, kraj i team ridera oraz link do detali. | Tak — osiągalny z aplikacji | — |

## src/components/feedback

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [states.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/feedback/states.module.css>) | Style: Wspólne loading, failed to load i no data. | Tak — import CSS | — |
| [states.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/feedback/states.tsx>) | Wspólne loading, failed to load i no data. | Tak — osiągalny z aplikacji | ErrorState przyjmuje opis, którego nie wyświetla; uporządkować kontrakt. |
| [update-progress.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/feedback/update-progress.test.tsx>) | Testy: Wspólny pasek aktualizacji danych. | Odkrywany przez Vitest | — |
| [update-progress.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/feedback/update-progress.tsx>) | Wspólny pasek aktualizacji danych. | Tak — osiągalny z aplikacji | — |

## src/components/filters/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [filter-option-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/api/filter-option-query-keys.ts>) | Wspólne klucze cache słowników filtrów. | Tak — osiągalny z aplikacji | — |
| [get-domain-metadata.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/api/get-domain-metadata.test.tsx>) | Testy: Pobiera słowniki etykiet i kategorii z API. | Odkrywany przez Vitest | — |
| [get-domain-metadata.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/api/get-domain-metadata.ts>) | Pobiera słowniki etykiet i kategorii z API. | Tak — osiągalny z aplikacji | — |
| [get-filter-options.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/api/get-filter-options.ts>) | Pobiera i normalizuje trasy, klasy, warunki oraz sezony. | Tak — osiągalny z aplikacji | — |
| [get-seasons.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/api/get-seasons.ts>) | Wspólne query sezonów i pięciominutowa świeżość słowników. | Tak — osiągalny z aplikacji | — |

## src/components/filters

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [context-filters.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/context-filters.module.css>) | Style: Składa pasek filtrów metryk i obsługuje wybór. | Tak — import CSS | — |
| [context-filters.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/context-filters.tsx>) | Składa pasek filtrów metryk i obsługuje wybór. | Tak — osiągalny z aplikacji | — |
| [filter-button.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/filter-button.module.css>) | Style: Wspólna prezentacja przycisku otwierającego filtr. | Tak — import CSS | — |
| [filter-button.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/filter-button.tsx>) | Wspólna prezentacja przycisku otwierającego filtr. | Tak — osiągalny z aplikacji | — |
| [filter-popover.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/filter-popover.module.css>) | Style: Wspólne menu filtra, pozycjonowanie i zamykanie. | Tak — import CSS | — |
| [filter-popover.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/filter-popover.tsx>) | Wspólne menu filtra, pozycjonowanie i zamykanie. | Tak — osiągalny z aplikacji | — |
| [multi-select.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/multi-select.module.css>) | Style: Lista opcji z wielokrotnym wyborem i wyszukiwaniem. | Tak — import CSS | — |
| [multi-select.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/multi-select.test.tsx>) | Testy: Lista opcji z wielokrotnym wyborem i wyszukiwaniem. | Odkrywany przez Vitest | — |
| [multi-select.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/filters/multi-select.tsx>) | Lista opcji z wielokrotnym wyborem i wyszukiwaniem. | Tak — osiągalny z aplikacji | — |

## src/components/forms

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [search-input.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/forms/search-input.module.css>) | Style: Wspólne pole wyszukania z ikoną i czyszczeniem. | Tak — import CSS | — |
| [search-input.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/forms/search-input.tsx>) | Wspólne pole wyszukania z ikoną i czyszczeniem. | Tak — osiągalny z aplikacji | — |

## src/components/layout

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [app-shell.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/app-shell.tsx>) | Układ strony: nawigacja, content i stopka. | Tak — osiągalny z aplikacji | — |
| [footer.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/footer.module.css>) | Style: Renderuje stopkę i odnośniki projektu. | Tak — import CSS | — |
| [footer.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/footer.tsx>) | Renderuje stopkę i odnośniki projektu. | Tak — osiągalny z aplikacji | — |
| [page-container.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/page-container.module.css>) | Style: Wspólne wrappery strony, nagłówków i sekcji. | Tak — import CSS | — |
| [page-container.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/page-container.test.tsx>) | Testy: Wspólne wrappery strony, nagłówków i sekcji. | Odkrywany przez Vitest | — |
| [page-container.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/layout/page-container.tsx>) | Wspólne wrappery strony, nagłówków i sekcji. | Tak — osiągalny z aplikacji | — |

## src/components/navigation

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [back-button.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/back-button.module.css>) | Style: Wspólny przycisk powrotu i animacja strzałki. | Tak — import CSS | — |
| [back-button.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/back-button.tsx>) | Wspólny przycisk powrotu i animacja strzałki. | Tak — osiągalny z aplikacji | — |
| [mobile-navigation.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/mobile-navigation.module.css>) | Style: Nawigacja na małych ekranach. | Tak — import CSS | — |
| [mobile-navigation.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/mobile-navigation.tsx>) | Nawigacja na małych ekranach. | Tak — osiągalny z aplikacji | — |
| [pagination.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/pagination.module.css>) | Style: Przyciski stron i wielokropek zamiast pełnej listy. | Tak — import CSS | — |
| [pagination.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/pagination.tsx>) | Przyciski stron i wielokropek zamiast pełnej listy. | Tak — osiągalny z aplikacji | — |
| [rider-navigation-state.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/rider-navigation-state.ts>) | Zapamiętuje źródłową sekcję, powrót i aktywne menu. | Tak — osiągalny z aplikacji | Utrzymywać reguły prefiksów tras w jednym miejscu. |
| [rider-navigation.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/rider-navigation.test.tsx>) | Testy: Tworzy link ridera i przekazuje kontekst filtrów. | Odkrywany przez Vitest | — |
| [rider-navigation.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/rider-navigation.tsx>) | Tworzy link ridera i przekazuje kontekst filtrów. | Tak — osiągalny z aplikacji | — |
| [segmented-control.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/segmented-control.module.css>) | Style: Jednokrotny wybór kategorii, sesji lub widoku. | Tak — import CSS | — |
| [segmented-control.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/segmented-control.tsx>) | Jednokrotny wybór kategorii, sesji lub widoku. | Tak — osiągalny z aplikacji | — |
| [tabs.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/tabs.module.css>) | Style: Wspólne zakładki z animacją aktywnego elementu. | Tak — import CSS | — |
| [tabs.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/tabs.tsx>) | Wspólne zakładki z animacją aktywnego elementu. | Tak — osiągalny z aplikacji | — |
| [top-navigation.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/top-navigation.module.css>) | Style: Główne menu desktopowe. | Tak — import CSS | — |
| [top-navigation.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/navigation/top-navigation.tsx>) | Główne menu desktopowe. | Tak — osiągalny z aplikacji | — |

## src/components/overlays/__snapshots__

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [dialog.test.tsx.snap](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/__snapshots__/dialog.test.tsx.snap>) | Zapis struktury DOM modala do testu regresyjnego. | Snapshot testowy | Aktualizować tylko po świadomej zmianie struktury. |

## src/components/overlays

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [dialog.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/dialog.module.css>) | Style: Wspólna konstrukcja modala, backdrop i animacje. | Tak — import CSS | — |
| [dialog.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/dialog.test.tsx>) | Testy: Wspólna konstrukcja modala, backdrop i animacje. | Odkrywany przez Vitest | — |
| [dialog.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/dialog.tsx>) | Wspólna konstrukcja modala, backdrop i animacje. | Tak — osiągalny z aplikacji | — |
| [select-rider-modal.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/select-rider-modal.module.css>) | Style: Wyszukuje i wybiera ridera do Compare. | Tak — import CSS | — |
| [select-rider-modal.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/select-rider-modal.test.tsx>) | Testy: Wyszukuje i wybiera ridera do Compare. | Odkrywany przez Vitest | — |
| [select-rider-modal.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/select-rider-modal.tsx>) | Wyszukuje i wybiera ridera do Compare. | Tak — osiągalny z aplikacji | Oddzielić listę wyników od sterowania modalem przy dalszej rozbudowie. |
| [tooltip.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/tooltip.module.css>) | Style: Wspólne tooltipy zwykłe i wykresowe z portalem/pozycjonowaniem. | Tak — import CSS | — |
| [tooltip.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/tooltip.tsx>) | Wspólne tooltipy zwykłe i wykresowe z portalem/pozycjonowaniem. | Tak — osiągalny z aplikacji | — |
| [use-dialog-focus.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/overlays/use-dialog-focus.ts>) | Focus trap, Escape i przywracanie focusa po modalu. | Tak — osiągalny z aplikacji | — |

## src/components/ui

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [button.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/button.module.css>) | Style: Wspólne warianty, stany i styl przycisków. | Tak — import CSS | — |
| [button.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/button.tsx>) | Wspólne warianty, stany i styl przycisków. | Tak — osiągalny z aplikacji | — |
| [panel.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/panel.module.css>) | Style: Wspólne panele, nagłówki, info i baner podsumowania. | Tak — import CSS | — |
| [panel.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/panel.test.tsx>) | Testy: Wspólne panele, nagłówki, info i baner podsumowania. | Odkrywany przez Vitest | — |
| [panel.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/panel.tsx>) | Wspólne panele, nagłówki, info i baner podsumowania. | Tak — osiągalny z aplikacji | — |
| [typography.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/typography.module.css>) | Style: Wspólne warianty tekstu i nagłówków. | Tak — import CSS | — |
| [typography.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/components/ui/typography.tsx>) | Wspólne warianty tekstu i nagłówków. | Tak — osiągalny z aplikacji | — |

## src/config

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [demo-data.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/config/demo-data.ts>) | Flaga przykładowych danych i sztuczne opóźnienie. | Demo wyłączone; flaga importowana | Zachować jako świadomie wyłączony tryb podglądu. |
| [env.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/config/env.ts>) | Waliduje adres API i konfigurację klienta. | Tak — osiągalny z aplikacji | — |
| [pagination.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/config/pagination.ts>) | Ustawienia liczby wierszy i paginacji. | Tak — osiągalny z aplikacji | — |
| [routes.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/config/routes.ts>) | Wspólne ścieżki i funkcje budowania adresów. | Tak — osiągalny z aplikacji | — |

## src/features/compare/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [compare-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/api/compare-query-keys.ts>) | Klucze cache porównania dwóch riderów. | Tak — osiągalny z aplikacji | — |
| [get-compare.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/api/get-compare.ts>) | Pobiera Compare i waliduje kontrakt odpowiedzi. | Tak — osiągalny z aplikacji | — |

## src/features/compare

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [compare-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/compare-page.module.css>) | Style: Filtry, riderzy w URL, modale i stan aktualizacji Compare. | Tak — import CSS | — |
| [compare-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/compare-page.tsx>) | Filtry, riderzy w URL, modale i stan aktualizacji Compare. | Tak — osiągalny z aplikacji | — |

## src/features/compare/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [compare-panels.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/components/compare-panels.module.css>) | Style: Tabele i wykresy porównawcze. | Tak — import CSS | — |
| [compare-panels.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/components/compare-panels.tsx>) | Tabele i wykresy porównawcze. | Tak — osiągalny z aplikacji | Oddzielić przygotowanie serii od komponentów tabel. |
| [compare-rider-hero.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/components/compare-rider-hero.module.css>) | Style: Zdjęcie, tożsamość i wybór ridera w Compare. | Tak — import CSS | — |
| [compare-rider-hero.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/compare/components/compare-rider-hero.tsx>) | Zdjęcie, tożsamość i wybór ridera w Compare. | Tak — osiągalny z aplikacji | — |

## src/features/fantasy/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-fantasy.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/api/get-fantasy.ts>) | Zapytania Fantasy, zasady i walidacja odpowiedzi. | Tak — osiągalny z aplikacji | — |

## src/features/fantasy

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [fantasy-format.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/fantasy-format.ts>) | Formatowanie budżetu, projekcji i etykiet Fantasy. | Tak — osiągalny z aplikacji | — |
| [fantasy-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/fantasy-page.module.css>) | Style: Best Team, prognozy, filtry i komunikaty Fantasy. | Tak — import CSS | — |
| [fantasy-page.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/fantasy-page.test.tsx>) | Testy: Best Team, prognozy, filtry i komunikaty Fantasy. | Odkrywany przez Vitest | — |
| [fantasy-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/fantasy-page.tsx>) | Best Team, prognozy, filtry i komunikaty Fantasy. | Tak — osiągalny z aplikacji | — |
| [fantasy-rules-dialog.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/fantasy/fantasy-rules-dialog.tsx>) | Modal z punktacją, budżetem i zasadami Fantasy. | Tak — osiągalny z aplikacji | — |

## src/features/home/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-home-data.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/api/get-home-data.ts>) | Query i schematy podsumowania, prognoz oraz analizy rundy. | Tak — osiągalny z aplikacji | Oddzielić schematy od query, jeśli plik dalej rośnie. |
| [home-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/api/home-query-keys.ts>) | Klucze cache sekcji Home. | Tak — osiągalny z aplikacji | — |

## src/features/home/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [analytics-links.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/components/analytics-links.test.tsx>) | Testuje przejście z Home bezpośrednio do Analytics rundy. | Odkrywany przez Vitest | — |
| [home-sections.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/components/home-sections.module.css>) | Style: Sekcje Home i część współdzielonej analityki rund. | Tak — import CSS | Rozdzielić razem z sekcjami Home; zachować obecne wartości. |
| [home-sections.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/components/home-sections.tsx>) | Sekcje Home i część współdzielonej analityki rund. | Tak — osiągalny z aplikacji | Podzielić według sekcji; współdzielone wykresy wynieść poza Home. |

## src/features/home

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [home-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/home-page.module.css>) | Style: Składa Home; zarządza niezależnymi kategoriami sekcji. | Tak — import CSS | — |
| [home-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/home/home-page.tsx>) | Składa Home; zarządza niezależnymi kategoriami sekcji. | Tak — osiągalny z aplikacji | — |

## src/features/live/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-live.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/get-live.test.ts>) | Testy: Odkrywanie sesji, polling i normalizacja providerów Live. | Odkrywany przez Vitest | — |
| [get-live.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/get-live.ts>) | Odkrywanie sesji, polling i normalizacja providerów Live. | Tak — osiągalny z aplikacji | Rozdzielić adaptery ChronoRace/Tissot od hooków i modeli. |
| [live-championship.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/live-championship.test.ts>) | Testy: Panel walki o generalkę w finale sezonu. | Odkrywany przez Vitest | — |
| [live-championship.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/live-championship.ts>) | Pobiera kontekst i projekcję generalki na podstawie live wyników. | Wyłączone: SHOW_LIVE_CHAMPIONSHIP=false | Zachować wyłączoną funkcję zgodnie z flagą. |
| [live-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/api/live-query-keys.ts>) | Klucze cache i snapshotów Live. | Tak — osiągalny z aplikacji | — |

## src/features/live/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [live-championship.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-championship.module.css>) | Style: Panel walki o generalkę w finale sezonu. | Styl wyłączonej funkcji generalki | — |
| [live-championship.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-championship.test.tsx>) | Testy: Panel walki o generalkę w finale sezonu. | Odkrywany przez Vitest | — |
| [live-championship.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-championship.tsx>) | Panel walki o generalkę w finale sezonu. | Wyłączone: SHOW_LIVE_CHAMPIONSHIP=false | — |
| [live-course-chart.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-course-chart.module.css>) | Style: Wykres gapów zawodników na trasie i legenda. | Tak — import CSS | — |
| [live-course-chart.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-course-chart.tsx>) | Wykres gapów zawodników na trasie i legenda. | Tak — osiągalny z aplikacji | — |
| [live-status-badge.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-status-badge.module.css>) | Style: Status sesji/rundy i pulsująca kropka Live. | Tak — import CSS | — |
| [live-status-badge.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/components/live-status-badge.tsx>) | Status sesji/rundy i pulsująca kropka Live. | Tak — osiągalny z aplikacji | — |

## src/features/live

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [live-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/live-page.module.css>) | Style: Lista sesji aktualnej rundy z odświeżaniem. | Tak — import CSS | — |
| [live-page.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/live-page.test.tsx>) | Testy: Lista sesji aktualnej rundy z odświeżaniem. | Odkrywany przez Vitest | — |
| [live-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/live-page.tsx>) | Lista sesji aktualnej rundy z odświeżaniem. | Tak — osiągalny z aplikacji | — |
| [live-results-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/live-results-page.module.css>) | Style: Szczegóły sesji: zegary, wykres i tabela live. | Tak — import CSS | — |
| [live-results-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/live/live-results-page.tsx>) | Szczegóły sesji: zegary, wykres i tabela live. | Tak — osiągalny z aplikacji | — |

## src/features/project-info

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [about-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/project-info/about-page.module.css>) | Style: Strona informacji o projekcie. | Tak — import CSS | — |
| [about-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/project-info/about-page.tsx>) | Strona informacji o projekcie. | Tak — osiągalny z aplikacji | — |
| [privacy-policy-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/project-info/privacy-policy-page.tsx>) | Nagłówek i stan braku treści polityki prywatności. | Tak — osiągalny z aplikacji | — |

## src/features/project-info/content

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [about-content.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/project-info/content/about-content.ts>) | Teksty strony About. | Tak — osiągalny z aplikacji | — |

## src/features/ranking/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-ranking.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/ranking/api/get-ranking.ts>) | Pobiera ranking, waliduje odpowiedź i obsługuje refresh. | Tak — osiągalny z aplikacji | — |
| [ranking-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/ranking/api/ranking-query-keys.ts>) | Klucze cache rankingu i jego filtrów. | Tak — osiągalny z aplikacji | — |

## src/features/ranking

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [ranking-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/ranking/ranking-page.module.css>) | Style: Tabela rankingu, filtry, sortowanie i zmiany pozycji. | Tak — import CSS | — |
| [ranking-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/ranking/ranking-page.tsx>) | Tabela rankingu, filtry, sortowanie i zmiany pozycji. | Tak — osiągalny z aplikacji | Ukryte narzędzia diagnostyczne trzymać za jedną flagą. |

## src/features/results/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-rounds.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/api/get-rounds.ts>) | Pobiera listę rund; zawiera wyłączone dane demo. | Tak — osiągalny z aplikacji | — |
| [results-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/api/results-query-keys.ts>) | Klucze cache listy rund. | Tak — osiągalny z aplikacji | — |

## src/features/results/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [session-results-table.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/components/session-results-table.module.css>) | Style: Tabela sesji: sortowanie, splity/sektory, perfect run i linki. | Tak — import CSS | — |
| [session-results-table.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/components/session-results-table.test.tsx>) | Testy: Tabela sesji: sortowanie, splity/sektory, perfect run i linki. | Odkrywany przez Vitest | — |
| [session-results-table.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/components/session-results-table.tsx>) | Tabela sesji: sortowanie, splity/sektory, perfect run i linki. | Tak — osiągalny z aplikacji | — |

## src/features/results

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [results-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/results-page.module.css>) | Style: Lista rund pogrupowana według sezonu. | Tak — import CSS | — |
| [results-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/results-page.tsx>) | Lista rund pogrupowana według sezonu. | Tak — osiągalny z aplikacji | — |
| [results-requests.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/results/results-requests.test.tsx>) | Testuje liczbę requestów Results oraz współdzielenie sezonów. | Odkrywany przez Vitest | — |

## src/features/riders/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-pressure-performance.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-pressure-performance.ts>) | Schemat danych Pressure Performance wewnątrz overview. | Tak — osiągalny z aplikacji | Nazwa pliku sugeruje request; zmienić na pressure-performance-schema. |
| [get-rider-overview.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-rider-overview.ts>) | Jedno query danych wszystkich tabów ridera i schemat odpowiedzi. | Tak — osiągalny z aplikacji | — |
| [get-rider-sessions.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-rider-sessions.test.ts>) | Testy: Normalizuje wyniki sesji otrzymane wewnątrz overview. | Odkrywany przez Vitest | — |
| [get-rider-sessions.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-rider-sessions.ts>) | Normalizuje wyniki sesji otrzymane wewnątrz overview. | Tak — osiągalny z aplikacji | Nazwa pliku sugeruje request; zmienić na rider-session-schema. |
| [get-rider-team-history.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-rider-team-history.ts>) | Schemat historii teamów wewnątrz overview. | Tak — osiągalny z aplikacji | Nazwa pliku sugeruje request; zmienić na team-history-schema. |
| [get-riders.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/get-riders.ts>) | Lista riderów dla wyszukiwarki i modala Compare. | Tak — osiągalny z aplikacji | — |
| [rider-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/api/rider-query-keys.ts>) | Wspólne klucze cache list riderów i overview. | Tak — osiągalny z aplikacji | — |

## src/features/riders/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [rider-table-content.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/components/rider-table-content.test.tsx>) | Testy: Wspólny wrapper tabów Tracks/Results i stanów błędu. | Odkrywany przez Vitest | — |
| [rider-table-content.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/components/rider-table-content.tsx>) | Wspólny wrapper tabów Tracks/Results i stanów błędu. | Tak — osiągalny z aplikacji | — |

## src/features/riders

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [rider-details-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-details-page.tsx>) | Statystyki i wykresy ridera. | Tak — osiągalny z aplikacji | — |
| [rider-pages.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-pages.module.css>) | Wspólne style statystyk, tabel i wykresów w detalach ridera. | Tak — import CSS | — |
| [rider-results-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-results-page.tsx>) | Tabela wyników ridera z sortowaniem i paginacją. | Tak — osiągalny z aplikacji | — |
| [rider-route-layout.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-route-layout.module.css>) | Style: Wspólny header, filtry, overview i animacja tabów ridera. | Tak — import CSS | — |
| [rider-route-layout.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-route-layout.tsx>) | Wspólny header, filtry, overview i animacja tabów ridera. | Tak — osiągalny z aplikacji | — |
| [rider-tracks-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/riders/rider-tracks-page.tsx>) | Tabela osiągów ridera na trasach. | Tak — osiągalny z aplikacji | — |

## src/features/rounds/api

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [get-round.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/api/get-round.ts>) | Zapytania szczegółów rundy, sesji i wyników. | Tak — osiągalny z aplikacji | — |
| [round-query-keys.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/api/round-query-keys.ts>) | Klucze cache szczegółów i analityki rundy. | Tak — osiągalny z aplikacji | — |
| [round-race-analytics-schema.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/api/round-race-analytics-schema.ts>) | Waliduje kontrakt zaawansowanej analityki rundy. | Tak — osiągalny z aplikacji | — |

## src/features/rounds/components

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [round-analysis.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-analysis.module.css>) | Style: Składa Analytics i wykres Field Progression. | Tak — import CSS | — |
| [round-analysis.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-analysis.tsx>) | Składa Analytics i wykres Field Progression. | Tak — osiągalny z aplikacji | Usunąć zależność od komponentów katalogu Home przy ich wydzieleniu. |
| [round-race-charts.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-race-charts.tsx>) | Wspólne wykresy używane w analizie rundy. | Tak — osiągalny z aplikacji | — |
| [round-race-sections.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-race-sections.module.css>) | Style: Bloki Chaos, korelacji, sektorów i Run Efficiency. | Tak — import CSS | — |
| [round-race-sections.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-race-sections.test.tsx>) | Testy: Bloki Chaos, korelacji, sektorów i Run Efficiency. | Odkrywany przez Vitest | — |
| [round-race-sections.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/components/round-race-sections.tsx>) | Bloki Chaos, korelacji, sektorów i Run Efficiency. | Tak — osiągalny z aplikacji | — |

## src/features/rounds

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [round-category-session.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/round-category-session.test.tsx>) | Testuje zachowanie sesji przy zmianie kategorii i tabów. | Odkrywany przez Vitest | — |
| [round-details-page.module.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/round-details-page.module.css>) | Style: Filtry kategorii/sesji, baner oraz Results/Analytics. | Tak — import CSS | — |
| [round-details-page.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/features/rounds/round-details-page.tsx>) | Filtry kategorii/sesji, baner oraz Results/Analytics. | Tak — osiągalny z aplikacji | — |

## src/hooks

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [use-client-table.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/hooks/use-client-table.ts>) | Sortowanie, paginacja i reset strony lokalnej tabeli. | Tak — osiągalny z aplikacji | — |
| [use-debounced-value.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/hooks/use-debounced-value.ts>) | Opóźnia reakcję na zmieniający się tekst wyszukania. | Tak — osiągalny z aplikacji | — |
| [use-metrics-filters.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/hooks/use-metrics-filters.ts>) | Zarządza wspólnymi filtrami metryk zapisanymi w URL. | Tak — osiągalny z aplikacji | — |

## src/lib

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [api-client.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/api-client.ts>) | HTTP, timeout, błędy, walidacja Zod i metadane odpowiedzi. | Tak — osiągalny z aplikacji | Rozdzielić diagnostykę od transportu, jeśli będzie się rozrastać. |
| [api-response-meta.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/api-response-meta.test.ts>) | Testy: Przechowuje metadane requestu i odczytuje Server-Timing. | Odkrywany przez Vitest | — |
| [api-response-meta.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/api-response-meta.ts>) | Przechowuje metadane requestu i odczytuje Server-Timing. | Tak — osiągalny z aplikacji | — |
| [react-query.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/react-query.test.ts>) | Testy: Wspólny cache, retry i zachowanie poprzednich danych. | Odkrywany przez Vitest | — |
| [react-query.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/react-query.ts>) | Wspólny cache, retry i zachowanie poprzednich danych. | Tak — osiągalny z aplikacji | — |
| [validation.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/lib/validation.ts>) | Wspólne schematy identyfikatorów, nulli i paginacji. | Tak — osiągalny z aplikacji | — |

## src

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [main.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/main.tsx>) | Uruchamia React; ładuje fonty i globalne style. | Tak — osiągalny z aplikacji | Sprawdzić, czy wszystkie warianty fontów są potrzebne. |

## src/styles

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [globals.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/globals.css>) | Łączy tokeny; ustawia tło, focus i wspólne interakcje. | Tak — globalny CSS | Ograniczać globalne selektory button:hover na rzecz komponentów. |
| [reset.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/reset.css>) | Resetuje domyślne style przeglądarki. | Tak — globalny CSS | — |

## src/styles/tokens

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [colors.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/colors.css>) | Semantyczne kolory powierzchni, tekstu, ocen i wykresów. | Tak — tokeny CSS | — |
| [components.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/components.css>) | Wspólne rozmiary i parametry komponentów. | Tak — tokeny CSS | — |
| [layout.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/layout.css>) | Szerokości, układ i wartości layoutu. | Tak — tokeny CSS | — |
| [primitives.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/primitives.css>) | Bazowa paleta i pierwotne wartości design systemu. | Tak — tokeny CSS | — |
| [radii.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/radii.css>) | Wspólne promienie zaokrągleń. | Tak — tokeny CSS | — |
| [spacing.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/spacing.css>) | Wspólne wartości odstępów. | Tak — tokeny CSS | — |
| [typography.css](<C:/Work/Projects/race-trace/racetrace-web-app/src/styles/tokens/typography.css>) | Fonty, grubości, rozmiary i definicje font-face. | Tak — tokeny CSS | — |

## src/testing

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [query-keys.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/testing/query-keys.test.ts>) | Testuje stabilność i rozdzielenie kluczy cache. | Odkrywany przez Vitest | — |
| [setup.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/testing/setup.ts>) | Ustawia matchery, cleanup i środowisko testów. | Vitest setup | — |

## src/types

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [api-contract.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/api-contract.test.ts>) | Testy: Wiąże schematy Zod z typami wygenerowanymi z OpenAPI. | Odkrywany przez Vitest | — |
| [api-contract.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/api-contract.ts>) | Wiąże schematy Zod z typami wygenerowanymi z OpenAPI. | Tak — osiągalny z aplikacji | — |
| [api-contract.type-test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/api-contract.type-test.ts>) | Kompilacyjne testy typów i wykrywania zmian kontraktu. | Typecheck — nie runtime | — |
| [api.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/api.ts>) | Klasa błędu API i typy odpowiedzi klienta. | Tak — osiągalny z aplikacji | — |
| [domain.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/domain.ts>) | Wspólne typy prezentacyjne, identyfikatory i filtry. | Tak — osiągalny z aplikacji | — |

## src/types/generated

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [api.d.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/types/generated/api.d.ts>) | Typy TypeScript wygenerowane z kontraktu OpenAPI. | Kompilacja/kontrakty | Nie edytować ręcznie; regenerować. |

## src/utils

| Plik | Rola / odpowiedzialność | Użycie | Co poprawić |
| --- | --- | --- | --- |
| [categories.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/categories.test.ts>) | Testy: Domyślne kategorie, odkrywanie nowych, płeć i lokalny cache. | Odkrywany przez Vitest | — |
| [categories.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/categories.ts>) | Domyślne kategorie, odkrywanie nowych, płeć i lokalny cache. | Tak — osiągalny z aplikacji | Docelowo ograniczyć fallbacki na rzecz kompletnego słownika API. |
| [competition-classes.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/competition-classes.test.ts>) | Testy: Skróty i kolory klas; neutralny fallback dla nowych klas. | Odkrywany przez Vitest | — |
| [competition-classes.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/competition-classes.ts>) | Skróty i kolory klas; neutralny fallback dla nowych klas. | Tak — osiągalny z aplikacji | — |
| [countries.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/countries.ts>) | Mapuje kody krajów na flagi i pełne nazwy. | Tak — osiągalny z aplikacji | — |
| [display-name.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/display-name.ts>) | Normalizuje wielkość liter nazwisk i teamów. | Tak — osiągalny z aplikacji | — |
| [domain-metadata.test.tsx](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/domain-metadata.test.tsx>) | Testy: Cache słowników oraz pełne i skrócone etykiety. | Odkrywany przez Vitest | — |
| [domain-metadata.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/domain-metadata.ts>) | Cache słowników oraz pełne i skrócone etykiety. | Tak — osiągalny z aplikacji | — |
| [formatters.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/formatters.test.ts>) | Testy: Wspólne formatowanie czasu, gapów, dat i liczb. | Odkrywany przez Vitest | — |
| [formatters.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/formatters.ts>) | Wspólne formatowanie czasu, gapów, dat i liczb. | Tak — osiągalny z aplikacji | — |
| [join-class-names.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/join-class-names.ts>) | Łączy warunkowe nazwy klas CSS. | Tak — osiągalny z aplikacji | — |
| [metrics-api-query.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/metrics-api-query.test.ts>) | Testy: Przekłada filtry UI na parametry zapytania API. | Odkrywany przez Vitest | — |
| [metrics-api-query.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/metrics-api-query.ts>) | Przekłada filtry UI na parametry zapytania API. | Tak — osiągalny z aplikacji | — |
| [rating.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/rating.test.ts>) | Testy: Dobiera ton oceny według wartości widocznej po zaokrągleniu. | Odkrywany przez Vitest | — |
| [rating.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/rating.ts>) | Dobiera ton oceny według wartości widocznej po zaokrągleniu. | Tak — osiągalny z aplikacji | — |
| [rider-image.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/rider-image.ts>) | Wybiera wariant zdjęcia ridera według identyfikatora. | Tak — osiągalny z aplikacji | — |
| [url-filters.test.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/url-filters.test.ts>) | Testy: Czyta i zapisuje filtry w URL. | Odkrywany przez Vitest | — |
| [url-filters.ts](<C:/Work/Projects/race-trace/racetrace-web-app/src/utils/url-filters.ts>) | Czyta i zapisuje filtry w URL. | Tak — osiągalny z aplikacji | — |

## Ograniczenia i dalsza praca

- Brak odwołań oceniano w śledzonych źródłach, konfiguracji i HTML obu repozytoriów. Nie sprawdzano zewnętrznych aplikacji, deploymentów ani ręcznie zapisanych URL zasobów.
- Status „narzędzie” nie oznacza kodu zbędnego: narzędzia chronią zgodność wyników i służą pomiarom.
- Ocena testów dotyczy ich przeznaczenia i sposobu uruchamiania; na potrzeby tego dokumentu nie zmieniano kodu i nie powtarzano całego zestawu testów.
- Kontrola kompletności: 617 różnych ścieżek, po jednym wpisie na każdy plik z git ls-files. Ten nowy dokument nie wchodzi do tej liczby bazowej.
