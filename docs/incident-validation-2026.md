# Walidacja potwierdzonych incydentów 2026

Etykiety pochodzą z dostarczonego CSV. Brak wpisu nie oznacza czystego przejazdu. Nie wyliczamy precision ani false-positive rate bez zweryfikowanych negatywów. Dopasowanie: sezon, klasa, venue, kategoria, sesja, nazwisko; niejednoznaczności pozostają niedopasowane. Przypadki treningowe/DSQ/DNS są oddzielone od detekcji sektorów. Jest to ocena na danych użytych w rozwoju reguł, nie niezależny test.

Numery w CSV opisują strefę punktu pomiarowego (`reported_numbered_split_zone`), nie zawsze dokładny sektor czasowy. Dlatego porównujemy wykrycie przejazdu; podajemy też strefę z CSV do ręcznej oceny lokalizacji. Nazwy skrócone w źródle są jawnie mapowane tylko w narzędziu audytu (Vali/Valentina, Hattie/Harriet itd.).

| CSV | Venue / rider | Sesja | Stan | Wskazane sektory | Strefa z CSV | Korekta Pace czasu |
|---|---|---|---|---|---|---:|
| RT-2026-0001 | Mona Yongpyong / Jenna Hastings | FINAL | Silna anomalia | S3 |  | 24.347 s |
| RT-2026-0002 | Mona Yongpyong / Anna Newkirk | FINAL | Silna anomalia | S3 |  | 13.398 s |
| RT-2026-0003 | Mona Yongpyong / Marine Cabirou | FINAL | Silna anomalia | S3 |  | 33.998 s |
| RT-2026-0004 | Mona Yongpyong / Bode Burke | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0005 | Mona Yongpyong / Jordan Williams | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0006 | Mona Yongpyong / Jackson Goldstone | FINAL | Silna anomalia | S4 |  | 5.667 s |
| RT-2026-0007 | Mona Yongpyong / Amaury Pierron | FINAL | Silna anomalia | S4 |  | 2.444 s |
| RT-2026-0010 | Loudenvielle / Sacha Earnest | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0012 | Loudenvielle / Daniel Castellanos Liberal | FINAL | Niewykryty (DNF) |  |  | 0.000 s |
| RT-2026-0013 | Loudenvielle / Bernard Kerr | FINAL | Silna anomalia | S5 |  | 7.748 s |
| RT-2026-0014 | Loudenvielle / Asa Vermette | FINAL | DNS / DSQ — bez korekty | — |  | — |
| RT-2026-0015 | Loudenvielle / Oliver Davis | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0016 | Loudenvielle / Nathan Pontvianne | FINAL | Silna anomalia | S2 |  | 8.489 s |
| RT-2026-0017 | Loudenvielle / Loris Vergier | FINAL | Silna anomalia | S4 |  | 11.994 s |
| RT-2026-0018 | Loudenvielle / Till Alran | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0019 | Loudenvielle / Ethan Craik | FINAL | Silna anomalia | S4 |  | 20.407 s |
| RT-2026-0020 | Loudenvielle / Amaury Pierron | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0021 | Leogang / Henry Kerr | FINAL | Silna anomalia | S1 |  | 14.497 s |
| RT-2026-0022 | Leogang / Antoine Pierron | FINAL | Silna anomalia | S4 |  | 13.438 s |
| RT-2026-0023 | Leogang / Martin Maes | FINAL | Silna anomalia | S4 |  | 8.839 s |
| RT-2026-0024 | Leogang / Finn Iles | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0026 | Leogang / Asa Vermette | FINAL | Silna anomalia | S3 |  | 55.190 s |
| RT-2026-0027 | Leogang / Loris Vergier | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0028 | Leogang / Jordan Williams | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0029 | Leogang / Henri Kiefer | FINAL | Niewykryty |  | 1 | 0.000 s |
| RT-2026-0030 | Leogang / Dylan Maples | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0032 | Lenzerheide / Veronika Widmann | FINAL | Silna anomalia | S1 |  | 10.393 s |
| RT-2026-0033 | Lenzerheide / Vali Höll | FINAL | Silna anomalia | S4 |  | 3.466 s |
| RT-2026-0034 | Lenzerheide / Myriam Nicole | FINAL | Silna anomalia | S2 | 1 | 3.303 s |
| RT-2026-0035 | Lenzerheide / Benoit Coulanges | FINAL | Silna anomalia | S4 |  | 31.311 s |
| RT-2026-0036 | Lenzerheide / Bode Burke | FINAL | Silna anomalia | S4 |  | 0.000 s |
| RT-2026-0037 | Lenzerheide / Nathan Pontvianne | FINAL | Silna anomalia | S5 |  | 1.713 s |
| RT-2026-0038 | Lenzerheide / Lachlan Stevens-McNab | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0040 | La Thuile / Bode Burke | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0041 | La Thuile / Roger Vieira | FINAL | Silna anomalia | S2 |  | 11.394 s |
| RT-2026-0042 | La Thuile / Ethan Craik | FINAL | Silna anomalia | S4 |  | 34.316 s |
| RT-2026-0043 | La Thuile / Henri Kiefer | FINAL | Silna anomalia | S1 | 1 | 25.176 s |
| RT-2026-0045 | La Thuile / Angel Suarez | FINAL | Silna anomalia | S2 |  | 12.971 s |
| RT-2026-0047 | Pal Arinsal / Phoebe Gale | FINAL | Silna anomalia | S2 |  | 14.078 s |
| RT-2026-0048 | Pal Arinsal / Eliana Hulsebosch | FINAL | Silna anomalia | S3 |  | 16.276 s |
| RT-2026-0049 | Pal Arinsal / Lisa Baumann | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0050 | Pal Arinsal / Gloria Scarsi | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0051 | Pal Arinsal / Ethan Craik | FINAL | Silna anomalia | S1 |  | 0.000 s |
| RT-2026-0052 | Pal Arinsal / Asa Vermette | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0053 | Pal Arinsal / Michael Delesalle | FINAL | Silna anomalia | S4 |  | 7.158 s |
| RT-2026-0054 | Pal Arinsal / Max Alran | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0055 | Pal Arinsal / Amaury Pierron | FINAL | Silna anomalia | S5 |  | 23.785 s |
| RT-2026-0056 | Pal Arinsal / Bode Burke | FINAL | Niewykryty (DNF) |  | 4 | 0.000 s |
| RT-2026-0057 | Pal Arinsal / Lachlan Stevens-McNab | FINAL | Silna anomalia | S1 |  | 17.320 s |
| RT-2026-0059 | Val di Sole / Jess Blewitt | FINAL | Niewykryty (DNF) |  |  | 0.000 s |
| RT-2026-0060 | Val di Sole / Matilda Melton | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0061 | Val di Sole / Phoebe Gale | FINAL | Silna anomalia | S3 | 3 | 16.252 s |
| RT-2026-0062 | Val di Sole / Sacha Earnest | FINAL | Silna anomalia | S5 |  | 8.363 s |
| RT-2026-0063 | Val di Sole / Gloria Scarsi | FINAL | Silna anomalia | S2 |  | 25.333 s |
| RT-2026-0064 | Val di Sole / Marine Cabirou | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0065 | Val di Sole / Reece Wilson | FINAL | Silna anomalia | S3 |  | 9.150 s |
| RT-2026-0066 | Val di Sole / Troy Brosnan | FINAL | Silna anomalia | S4 |  | 18.325 s |
| RT-2026-0067 | Val di Sole / Ryan Pinkerton | FINAL | Niewykryty |  | 2 | 0.000 s |
| RT-2026-0068 | Val di Sole / Luke Meier-Smith | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0069 | Whistler / Lisa Baumann | FINAL | Silna anomalia | S1 | 1 | 10.715 s |
| RT-2026-0070 | Whistler / Anna Newkirk | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0071 | Whistler / Eliana Hulsebosch | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0072 | Whistler / Dane Jewett | FINAL | Silna anomalia | S1 |  | 15.292 s |
| RT-2026-0073 | Whistler / Oisin O'Callaghan | FINAL | Silna anomalia | S3 | 3 | 23.812 s |
| RT-2026-0074 | Whistler / Ryan Pinkerton | FINAL | Silna anomalia | S1 | 1 | 5.654 s |
| RT-2026-0075 | Whistler / Richie Rude | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0076 | Whistler / Henri Kiefer | FINAL | Silna anomalia | S5 |  | 9.261 s |
| RT-2026-0077 | Whistler / Lachlan Stevens-McNab | FINAL | DNS / DSQ — bez korekty | — |  | — |
| RT-2026-0078 | Les Gets / Matilda Melton | FINAL | Silna anomalia | S3 |  | 8.793 s |
| RT-2026-0079 | Les Gets / Gracey Hemstreet | FINAL | Silna anomalia | S2 |  | 5.256 s |
| RT-2026-0080 | Mona Yongpyong / Tahnee Seagrave | Q1 | Niewykryty |  |  | 0.000 s |
| RT-2026-0081 | Mona Yongpyong / Jackson Goldstone | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0082 | Leogang / Finn Iles | Q1 | Silna anomalia | S2 |  | 9.289 s |
| RT-2026-0083 | Lenzerheide / Finn Iles | Q1 | Niewykryty |  |  | 0.000 s |
| RT-2026-0084 | Loudenvielle / Anna Newkirk | Q1 | Niewykryty (DNF) |  |  | 0.000 s |
| RT-2026-0085 | La Thuile / Myriam Nicole | Q1 | Silna anomalia | S5 |  | 86.130 s |
| RT-2026-0086 | La Thuile / Antoine Rogge | Q1 | Niewykryty (DNF) |  |  | 0.000 s |
| RT-2026-0087 | La Thuile / Abby Hogie | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0088 | Pal Arinsal / Myriam Nicole | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0089 | Val di Sole / Tyler Waite | QUALIFYING | Niewykryty (DNF) |  |  | 0.000 s |
| RT-2026-0090 | Whistler / Remi Thirion | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0091 | Loudenvielle / Aletha Ostgaard | FINAL | Silna anomalia | S1 |  | 7.012 s |
| RT-2026-0092 | Leogang / Rosa Zierl | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0093 | Lenzerheide / Aletha Ostgaard | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0094 | La Thuile / Jonty Williamson | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0095 | Les Gets / Rosa Marie Jensen | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0096 | La Thuile / Jonty Williamson | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0097 | Val di Sole / Jackson Goldstone | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0098 | La Thuile / Hattie Harnden | Q1 | Silna anomalia | S1 | 1 | 8.070 s |
| RT-2026-0099 | Val di Sole / Max Hartenstern | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0100 | Loudenvielle / Loic Bruni | TIMED_TRAINING | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0101 | Mona Yongpyong / Wyn Masters | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0102 | Loudenvielle / Jackson Goldstone | Q1 | DNS / DSQ — bez korekty | — |  | — |
| RT-2026-0103 | Loudenvielle / Jackson Goldstone | Q2 | DNS / DSQ — bez korekty | — |  | — |
| RT-2026-0104 | Les Gets / Amaury Pierron | FINAL | Możliwa anomalia |  |  | 0.000 s |
| RT-2026-0105 | Les Gets / Martin Maes | FINAL | Niewykryty |  |  | 0.000 s |
| RT-2026-0106 | Les Gets / Max Alran | Q1 | Silna anomalia | S3 |  | 7.143 s |
| RT-2026-0107 | Whistler / Jordan Williams | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0108 | La Thuile / Sacha Earnest | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0109 | Lenzerheide / Anna Newkirk | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0111 | Loudenvielle / Mille Johnset | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0112 | Loudenvielle / Anna Newkirk | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0113 | Leogang / Hattie Harnden | UNKNOWN | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0114 | Leogang / Troy Brosnan | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0115 | Whistler / Jackson Goldstone | Q1 | Silna anomalia | S5 |  | 133.649 s |
| RT-2026-0116 | Lenzerheide / Jackson Goldstone | Q1 | Silna anomalia | S1 |  | 33.673 s |
| RT-2026-0117 | Lenzerheide / Bernard Kerr | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0118 | Whistler / Jackson Goldstone | PRACTICE | Poza sesjami ocenianymi / brak określonej sesji | — |  | — |
| RT-2026-0119 | Whistler / Ryder Wilson | FINAL | Niewykryty |  |  | 0.000 s |

Unikalne dopasowane przejazdy oceniane: 86; silna anomalia: 48; tylko możliwa: 7; niewykryte: 31. DNF bez zlokalizowanej anomalii nie jest zaliczony jako wykryty sektor.

Silne wskazania przed zmianą: 37, po zmianie: 48, na dokładnie tych samych dopasowanych przejazdach.

- Silna anomalia: 48 wpisów CSV.
- Niewykryty: 25 wpisów CSV.
- Niewykryty (DNF): 6 wpisów CSV.
- DNS / DSQ — bez korekty: 4 wpisów CSV.
- Możliwa anomalia: 7 wpisów CSV.
- Poza sesjami ocenianymi / brak określonej sesji: 19 wpisów CSV.
