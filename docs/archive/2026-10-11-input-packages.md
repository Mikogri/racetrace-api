# Calculator-independent input packages

> Archiwalny raport wdrożenia i pomiarów z 11.10.2026. Zasada pakietów pozostaje używana; jej aktualny opis jest w [DATA_PIPELINE.md](../DATA_PIPELINE.md).

## Contract

- Frontend and public endpoint/DTO contracts are unchanged.
- `get_ranking_input_packages` returns compact session inputs. `get_rider_input_data` returns the rider plus the complete field needed for relative calculations.
- The API constructs sectors, field distributions and corrected finishes using `PaceService` and `DownhillPaceCalculator.PrepareSession`. Both cached and raw measurements use these calculations.
- Input format `v: 3` contains `s` sessions, `r` selected results, `d` rider metadata, `t` teams, `f` reference results (details), `ix` cumulative timing measurements and `x` public split display data. It contains no incident decisions, corrected finish references or final metric values.
- The source cache gains `input_splits`, including missing/invalid timing observations instead of only positive display timings. A trigger refreshes it with the source package. Existing payload metadata is reused rather than copied into another complete cache.
- Existing legacy statistics/prepared columns are retained for compatibility, but the new read path does not consume them. Automatic imports/rebuilds no longer call calculator-dependent preparation. The explicit legacy preparation tool remains available.

## Rebuild rules

Changing calculator weights, incident thresholds or metric formulas requires an API deployment and invalidation of computed response caches, **not** a source-package rebuild. `PreparedRiderDetails` retains its configuration name but now controls input-package reuse. The `missing` rebuild checks input availability, not an algorithm fingerprint.

Imports, source corrections, changed point awards or new required input fields still require source invalidation/refresh. Official point awards remain separate persisted facts; changing their rules is not the same operation as changing Rating/Pace/Consistency.

## Correctness fixes

1. Preaggregated Consistency previously discarded DSQ final entries before calculating final qualification. Raw calculations retained that evidence. The fix retains the entry but excludes its performance. DSQ is not reclassified as a finish or an incident. Regression covers DSQ, DNS and DNF. Consistency identity advances to `1.0.1`; weights/formulas do not change.
2. Cross-season metadata was resolved by imported session ID. Snapshots now use race date, explicit result teams take precedence, and current rider/team names are read alongside source packages. Old cached names no longer affect display/search or tied ordering.
3. Status-filtered ranking/points requests previously reused unfiltered session summaries. They now use the matching raw query/population.
4. Raw display-sector ranks now exclude nonpositive cumulative timings, matching cached display ranks. The SQL regression covers negative and zero observations with positive deltas.

## Verification

- 786 Release tests passed after implementation, plus the SQL invalid-timing regression.
- 64 complete public DTO responses compared directly between cached and raw paths: zero differences. Coverage includes all four categories, one/multiple/all seasons, ranking pages 1–5, previous-round/filter/default comparisons, search, country, class, session/date filters, DNS/DNF/DSQ/Finished, empty results and rider overview tabs.
- Initial representative checks additionally compared the corrected raw baseline with the new inputs: Rating, Pace and Consistency unchanged; the fixes above explain previously divergent cache output.
- SQL checked on local PostgreSQL 17.6, then applied to the development Supabase project. 492/492 source packages have input measurements.
- Source invariants unchanged: 21,462 results, 79,562 time splits, sum of official finish times 4,662,810,323 ms.
- New RPCs are `SECURITY INVOKER` with fixed empty search paths and explicit execute grants. Security advisors reported only pre-existing findings, none on the new objects.

Reproduce cross-path checks (read-only; requires local server-side Supabase configuration):

```powershell
dotnet test racetrace-api.Tests -c Release -p:UseAppHost=false
dotnet run --project tools/ReadPathParity -c Release -- --parity --capture --cross-path --prepared --typed --output .codex-build/input-parity
```

## Indicative timing

Local Release services against the same remote database; empty API memory caches, two repetitions, midpoint in seconds. Network/JIT noise applies; not an SLA or a browser-load measurement.

| Request | Input packages | Raw source |
| --- | ---: | ---: |
| Ranking 2026, Elite Men, previous-round comparison | 1.46 | 1.76 |
| Ranking all seasons, Elite Men, previous-round comparison | 2.89 | 6.45 |
| Rider 183, 2026, Elite Men | 1.18 | 1.04 |
| Rider 18, all seasons, Elite Men | 1.72 | 1.97 |

Packages are not guaranteed to win on a small selection. The principal guarantee is calculator flexibility and equal results, with a substantial advantage for large ranking reads. Raw rider reads also avoid the former SQL statistical preparation.
