# Fantasy — kontrola na rzeczywistych danych, 16.09.2026

Silnik: `ridge-monte-carlo-v1`. Warstwa Fantasy: `fantasy-empirical-blend-v3`.
Scoring: `pinkbike-dh-2026-v1`. Odczyty bazy, bez modyfikacji historycznych wyników.

Przygotowanie: **38,76 s**, 255 prognoz z dostępnej historii (199 EM, 56 EW),
34 próby kategorii z 17 rund sezonów 2025–2026. Łącznie 1933 obserwacje zawodnik–runda.
Z 99 riderów katalogu Pinkbike 90 ma dopasowanie do bazy, 88 ma dostępną prognozę.

| Kategoria | Obserwacje | MAE, pkt | RMSE, pkt | Średni Spearman | Top-6 recall |
|---|---:|---:|---:|---:|---:|
| Elite Men | 1438 | 67,07 | 106,10 | 0,431 | 32,35% (33/102) |
| Elite Women | 495 | 105,56 | 145,58 | 0,480 | 55,88% (57/102) |

MAE/RMSE agregowane po obserwacjach. Spearman jest średnią ze zdefiniowanych korelacji rund, nie korelacją jednej połączonej tabeli.
Dla samego 2026: 16 prób kategorii, 895 obserwacji, MAE 77,02 pkt, RMSE 116,42 pkt, top-6 recall 40,625% (39/96).

Współczynniki mieszania: EM finał 0,96 / Q1 0,72, EW finał 0,89 / Q1 0,71.
Tak wysoki udział modelu bazowego oznacza ograniczoną siłę indywidualnych prognoz. Nie dopasowywano współczynników ręcznie, aby wyniki wyglądały atrakcyjniej.
Brak odrębnego raportu nieskalibrowanego wariantu w tym przebiegu — **nie dowodzi on poprawy względem obecnego podium predictora**.

Punktacja 2026 jest stosowana retrospektywnie. Kalibracja każdej próby korzysta tylko z wcześniejszych rund; dane prognozowanego weekendu są wyłączone.
Statusy nieukończenia nie są zamieniane na ostatnie miejsce. Niepotwierdzone przypadki zachowania Q1 po DNS/DSQ są pomijane w ocenie punktów łącznych.

Nie ma kompletnego historycznego katalogu cen potwierdzonego przed każdą ocenianą rundą i odpowiednich historycznych list startowych.
Dlatego **nie podano wyniku składu, oracle ani regret**. Dzisiejszych cen nie użyto zamiast dawnych.
Obecna lista startowa kolejnej rundy także nie jest potwierdzona — produkcyjny ekran nie pokazuje fikcyjnej rekomendacji.

Weryfikacja techniczna: testy punktacji, globalnego optimum, kalibracji, zajętości miejsc, importu, mapowania, trwałości danych, dopuszczenia składu i kontraktów API.
Sprawdzono działające odczyty lokalnego API i ochronę administracyjną: publiczny token → sync 403; anonimowy odczyt → 401.
Przykładowe ciepłe odczyty `current/riders/recommendation` trwały 12–20 ms lokalnie (nie jest to benchmark produkcyjny).
Kontrola przeglądarkowa: lista cen i prognoz, MEN/WOMEN, sortowanie, Rules, zachowanie danych podczas odświeżenia, brak poziomego overflow przy mobilnej szerokości 390 px.
