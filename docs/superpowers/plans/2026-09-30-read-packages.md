# Read package separation implementation plan

> Execute inline using executing-plans; preserve all public response values.

**Goal:** Reduce database-to-API traffic for ranking and rider details without changing metric formulas or category populations.
**Architecture:** Add read-only, invoker-security RPC projections over existing preaggregates. Ranking excludes raw splits; details keep rider results/splits and deduplicate repeated field statistics. Points use existing awarded points, no metric calculations. Legacy RPCs remain available.
**Tech stack:** ASP.NET Core, PostgreSQL JSONB, existing Supabase REST client and xUnit.
**Spec:** Current user request and preceding agreed proposal in this chat.

## Constraints and review focus
- Preserve sorting/ties, zero-point riders, historical team identity and all metric observations.
- Never combine categories implicitly; resolve session IDs with existing filter logic.
- Incomplete cache must not produce partial rankings; retain legacy fallback.
- Cache keys must distinguish full and ranking-only payloads and database roles.
- DDL is additive: no result/point updates or calculator changes; existing imports maintain the same source cache.

## Tasks
- [x] Commit pre-optimization API/frontend state (2e51e20 / dee7993); 638 tests passed.
- [x] Add SQL projections and validate unchanged retained fields, completeness and invoker grants.
- [x] Add DomainQueryService RPC paths and cache separation; tests for route selection and missing packages.
- [x] Route only ranking snapshots through slim transport; lightweight points with unchanged API tie breaks.
- [x] Evaluate detail compaction: rejected after measurement (157289 -> 162912 bytes, +3.6%); retain original transport.
- [x] Compare complete JSON from baseline 5018 and candidate 5019, run suite, measure payload bytes/times.
- [x] Final independent review and document limitations; no push requested.

## Decisions / progress
- Keep the existing preaggregate as the single stored source. Projections avoid introducing another independently refreshed cache table.
- Do not remove rider/team metadata until a separate identity transport can preserve historical selection exactly.
- Baseline is committed Release build at 5018; user server at 5017 is not restarted.

## Verification (2026-09-30)

- Final Release build / xUnit: 642 passed. Existing NU1903 Microsoft.OpenApi 2.0.0 vulnerability warning remains; no dependency changes in this task.
- Exact response hashes matched in 18/18 requests (nine ranking/overview scenarios, twice each), including multiple categories, seasons, FINAL-only, missing category, and ranking comparison modes.
- Independent review found no blocking issues. Added focused tests for full/slim cache isolation and incomplete points summaries.
- Database: all 482 projected packages equal their originals minus raw splits; zero mismatches. Serialized JSON sum: 10528015 -> 7956586 bytes (-24.4%). Points summary for that entire population: 194274 bytes. These are uncompressed JSON sizes, not network packet measurements.
- Missing package marks points summary incomplete; empty requested set is complete. Security advisor findings unchanged apart from timestamps.
- Both additive SQL migrations applied. Second removes only the experimentally introduced detail compaction functions, which increased size. Existing functions/data/calculators unchanged.

Final replay, first observation per scenario (seconds; shared caches and network affect these, not controlled cold benchmarks):

| Scenario | Baseline | Candidate |
|---|---:|---:|
| Rider 18, EM, all seasons | 3.284 | 2.477 |
| Rider 183, EM, 2026 | 0.904 | 0.783 |
| Rider 60, EM, 2025-26 | 1.387 | 1.069 |
| Rider 183, no category | 2.595 | 1.222 |
| Rider 18, EM, 2026 FINAL | 0.545 | 0.620 |
| Ranking EM, previous round | 1.711 | 2.423 |
| Ranking EM, 2026 points | 0.490 | 0.453 |
| Ranking EW, previous filters | 0.687 | 1.056 |
| Ranking EM, 2022-24 | 0.981 | 0.909 |

Ranking transfer reduction is verified, but ranking latency improvement is NOT established; two scenarios regressed in both replays. Keep this explicit when evaluating deployment. Detail gains primarily come from lightweight points position lookup, not rewritten metric calculations. New optimization source remains uncommitted, baseline checkpoints are committed, nothing pushed. User API on 5017 was left untouched and needs its normal restart to use new code.
