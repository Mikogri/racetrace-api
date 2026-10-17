# PodiumBacktest

Narzędzie badawcze porównuje sześć wariantów prognozy na historycznych danych. Nie podmienia produkcyjnego predictora, nie zapisuje wyników do bazy i nie dobiera modelu automatycznie. Protokół i zachowane wyniki opisuje [raport eksperymentu](../../docs/archive/podium-backtest-2026-09-15.md), a aktualny model — [PREDICTIONS.md](../../docs/PREDICTIONS.md).

Z katalogu głównego repozytorium API:

```powershell
dotnet run --project tools/racetrace-api.PodiumBacktest -- --api racetrace-api --output artifacts/podium-backtest --iterations 50000
```

Pierwsze uruchomienie pobiera rundy, sesje i `get_ranking_data` tylko do odczytu, z lokalną konfiguracją Supabase. `--download-only` zapisuje wejścia bez symulacji, a `--offline` odtwarza snapshot bez dostępu do sieci. Nowy snapshot wymaga nowego katalogu wyjściowego. Wynikami są `report.md`, `results.json` i `cache/*.json` w ignorowanych artefaktach; nie publikuj sekretów ani nagłówków autoryzacji.

Narzędzie wymaga historycznego targetu i próbki development/holdout; brak wymaganych danych kończy test błędem, a nie wynikiem zero. Ocena wyłącza cały target weekend, nie używa przyszłej listy finałowej i nie jest dokładnym replayem bieżącego endpointu podium.

Tryby `--incident-audit` i `--ranking-incident-comparison` służą badaniom detektora na zamrożonych wejściach; nie są częścią publicznego API. Przykład audytu i zasady interpretacji są w [ALGORITHMS.md](../../docs/ALGORITHMS.md).
