# Podium Predictor — eksperyment historyczny, 15 września 2026

## Zakres i odcięcie danych

Przetestowano sześć modeli na **40 wyścigach kategorii**: Elite Men i Elite Women,
po 20 rund World Cup z lat 2022–2024. Każdy model wykonał 50 000 symulacji na wyścig,
łącznie **12 000 000 symulacji**. Seed: `20260824`.

Historia zaczyna się w 2021. Dla każdego celu wyłączono cały prognozowany weekend,
łącznie z kwalifikacjami i półfinałami. Dane z poprzednich WCH mogą stanowić historię,
ale prognozowano finały World Cup. Przy wyborze kandydatów nie korzystano z docelowej
listy finałowej ani z przyszłych wyników. Brakujący medalista jest liczony jako błąd.

Przykład wskazany w zadaniu: **4. runda 2024, Val di Sole**, początek 14 czerwca
2024 o 00:00 lokalnie (13 czerwca 22:00 UTC). Przed nią wykorzystano dostępne
**6 034 wyniki EM i 1 508 wyników EW**, po 66 sesji na kategorię. Detektor sektorowy
oznaczył w tej historii 114 obserwacji incydentów EM i 15 EW.

Modele eksperymentalne używają całej dostępnej historii 2021+, z malejącą wagą
starszych przejazdów. Baseline zachowuje okna historii obecnego predictora, a nie
zastępuje ich nową regułą. Wszystkie warianty mają ten sam historyczny zestaw
kandydatów: maksymalnie 30, bieżący sezon, confidence >=35. Nieobecnych w faktycznym
finale nie usuwano po poznaniu wyników.

## Wyniki

„Medaliści w top 3” to udział rzeczywistych medalistów znalezionych w przewidywanej
trójce — **nie** częstotliwość trafienia całego podium. Brier mierzy błąd podanych
prawdopodobieństw; mniejszy wynik jest lepszy.

| Model | Medaliści w top 3, 2022–2023 | Medaliści w top 3, 2024 | Brier, 2024 |
|---|---:|---:|---:|
| Obecne równania predictora, prognoza przed weekendem | 38,1% | **33,3%** | 0,1097 |
| Rating jako przewidywany poziom | 39,3% | 25,0% | 0,1035 |
| Pace jako przewidywany poziom | 40,5% | 30,6% | 0,1098 |
| Czyste tempo i zmienność, bez osobnego ryzyka | 45,2% | 30,6% | 0,0888 |
| Czyste tempo + osobne ryzyko incydentu/DNF | **46,4%** | 30,6% | **0,0879** |
| Czyste tempo + ryzyko + dopasowanie do trasy | **46,4%** | 30,6% | **0,0879** |

Wyboru dokonano wyłącznie według wyników 2022–2023: recall@3, następnie Brier jako
rozstrzygnięcie remisu. Wybrany wariant to `clean-pace-risk`. Sezon 2024 stanowił
osobny sprawdzian; nie zmieniono wyboru na podstawie tego sezonu.

Na 12 wyścigach kategorii w 2024 obecny model znalazł 12 z 36 medalistów, a wybrany
wariant 11 z 36. Wybrany wariant miał niższy Brier o około 20% i lepszy log loss
(0,4408 zamiast 0,6730). Trafienie zwycięzcy według najwyższego win probability
wyniosło 16,7% dla obu. To nie jest jednoznaczna poprawa predykcji kolejności.

## Val di Sole 2024 — runda 4

| Kategoria | Rzeczywiste podium | Obecny model | Czyste tempo + ryzyko |
|---|---|---|---|
| Elite Men | Amaury Pierron, Dakotah Norton, Finn Iles | Loic Bruni, Luca Shaw, Andreas Kolb — 0/3 | Loic Bruni, Loris Vergier, Finn Iles — 1/3 |
| Elite Women | Tahnee Seagrave, Marine Cabirou, Monika Hrastnik | Valentina Höll, Marine Cabirou, Tahnee Seagrave — 2/3 | Valentina Höll, Camille Balanche, Nina Hoffmann — 0/3 |

Żaden z sześciu modeli nie wskazał poprawnie zwycięzcy tej rundy w żadnej z dwóch
kategorii. Nie należy dobierać współczynników tak, żeby dopasować właśnie ten wynik.

## Decyzja i ograniczenia

**Nie zastąpiono produkcyjnego predictora.** Zapisano modele i powtarzalny runner
do dalszych testów. Formuły Pace, Consistency i Rating, API oraz frontend pozostają
bez zmian. Z istniejącego budowania obserwacji wydzielono wspólną metodę, aby test
używał prawdziwego detektora incydentów zamiast jego kopii.

Wynik wskazuje przede wszystkim na nadmierną pewność obecnych prawdopodobieństw,
a nie na gotowy, zdecydowanie lepszy model podium. Nowe rozkłady i priory pozostają
hipotezami eksperymentalnymi; nie są skalibrowanym modelem produkcyjnym.

Ograniczenia: mała próba, brak historycznych list zgłoszeń/kontuzji/prognoz pogody,
ograniczenie kandydatów do historycznej czołówki bieżącego sezonu, aktualny snapshot
bazy z możliwymi późniejszymi korektami historycznych wyników. To test bez przyszłych
sesji, ale nie idealne odtworzenie stanu bazy z dnia prognozy. Modele zakładają udział
kandydatów; prawdopodobieństwa nie uwzględniają wiarygodnie nieobecności na starcie.

## Powtórzenie i weryfikacja

Instrukcja: [README narzędzia](../tools/racetrace-api.PodiumBacktest/README.md).
Pełne wyniki lokalne: `artifacts/podium-backtest/report.md` oraz `results.json`.
Ostatni pełny przebieg wykonano z `--offline --iterations 50000`; baza nie była
odpytywana podczas właściwych symulacji i nie zmieniono żadnych rekordów.

Build poprawny; **484 testy przeszły**, w tym 13 nowych przypadków chroniących
odcięcie historii, rozdzielenie incydentów/DNF, powtarzalność symulacji i blokadę
operacji zapisujących do bazy. Pozostaje wcześniejsze ostrzeżenie NU1903 dotyczące
zależności Microsoft.OpenApi 2.0.0, niezwiązane z eksperymentem.
