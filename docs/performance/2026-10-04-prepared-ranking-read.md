# Prepared ranking read — verification

## Scope

Two internal read optimizations, no calculator or public response changes:

- Persist the existing calculator's per-rider, per-session incident-corrected finish reference and field summary. Rider overview reads that reference instead of transferring and preparing rival populations.
- Ranking rows share metadata; legacy nested JSON is created only for consumers that read it. Current/comparison rankings reuse preparation of identical session packages within the request.

Original reference: `b935d7642f1d877e624c2e6fa847fe4ae4834fb7`, compiled independently from the unchanged original checkout. No frontend changes.

## Correctness

- 64 full public responses compared with `JsonNode.DeepEquals`: 32 queries with database preaggregation and forced cache-miss fallback. Zero differences.
- Coverage: four categories, one/multiple/all seasons, ranking pages 1–5, previous round/filter/default comparisons, search, country, competition class, session/date filters, DNF/DNS/DSQ/Finished, empty selections, rider overview without explicit category, historical teams and all overview tabs.
- Four representative queries repeated three times per version: zero differences. Stored HTTP fixtures reject source-data drift and replay offline; another 12 responses per version matched exactly.
- Release test suite: 705 passed, zero failed/skipped (before final independent review).
- Database rollback assertions: revision changes on source refresh; stale prepared writes are rejected; old/missing versions and status filters return incomplete; anon/authenticated cannot save prepared packages.
- 482 existing cached sessions, all 482 prepared with `pace-field-v1`. No official times, results, points or scoring formulas changed.

## Measurements

Three repetitions, fresh application memory cache each time, existing database preaggregation. Database/PostgreSQL cache was **not** cleared. Includes Supabase HTTP/deserialization and API service computation, not browser rendering or frontend HTTP transfer. Fixture validation adds a small equal recording overhead.

| Query | Before median (range), ms | After median (range), ms | DB → API response bytes before → after |
|---|---:|---:|---:|
| Elite Men ranking 2026, previous round | 1758 (1237–2532) | 1519 (1507–2272) | 738703 → 738703 |
| Elite Men ranking all years, previous round | 4288 (3972–4386) | 3380 (3128–3704) | 4328756 → 4328756 |
| Bruni overview, all years, Elite Men | 4056 (3790–4434) | 1128 (1126–1327) | 1322281 → 500240 |
| Vermette overview 2026, Elite Men | 1459 (1411–1470) | 893 (864–1069) | 256157 → 112573 |

Offline fixture replay medians, including fixture file reads/deserialization and service CPU, excluding network:

| Query | Before, ms | After, ms |
|---|---:|---:|
| Ranking 2026 | 794 | 740 |
| Ranking all years | 2716 | 1782 |
| Bruni all years | 281 | 84 |
| Vermette 2026 | 49 | 10 |

The ranking transfer is unchanged; its gain is reduced preparation/materialization. Rider details reduce both transfer and CPU. Three runs are indicative, not a production SLA. HTTP response-cache hits and SQL execution alone were not timed separately in this experiment.

## Deployment / rollback

Apply both migrations in `supabase/migrations` in timestamp order before deploying the API. These are additive migrations for the existing RaceTrace schema, not a new-database bootstrap. Both were applied to the existing development database. The rollback test requires prepared data and must always end with `ROLLBACK`.

`ReadOptimizations:PreparedRiderDetails` and `ReadOptimizations:TypedRanking` control the new paths. Both are enabled in application configuration after parity checks; set either to `false` to revert that read path without deleting source or prepared data. Existing cache TTLs remain unchanged. A new API process/configuration reload is needed to use updated code/configuration.

Existing refresh/import services now prepare through `RankingPreaggregationService`. The existing missing-preaggregation rebuild also selects stale/missing prepared versions. A source payload update creates a new revision and clears preparation. Saving compares that revision atomically. A concurrent refresh may reject preparation; reads then use the legacy fallback until a subsequent rebuild. No unbounded retry loop.

`RankingPreaggregationService.AlgorithmVersion` must change whenever preparation formulas or their interpretation change. Status-filtered overview always uses the old population-aware path. Missing RPC, incomplete envelopes and malformed prepared payloads have controlled fallback; network/authentication failures are not silently swallowed.

Security advisor found no new finding naming the new functions. The pre-existing `Microsoft.OpenApi 2.0.0` NU1903 vulnerability warning remains outside this optimization.

## Implementation decisions

- Retain compact scalar JSON in `RankingReadResult` rather than rewrite all calculator input types; shared metadata is treated read-only and legacy consumers receive clones.
- Reuse is scoped and keyed by immutable package object identity. A refreshed/replaced package is a different key; there is no new global preparation cache.
- Persist only corrected finish references; existing compact payload already contains sector and field summaries.
- Tests include post-implementation coverage additions; not every test was written first. Parser legacy-shape and malformed/missing fallback regressions were verified failing before their fixes.

The implementation initially remains on `codex/prepared-ranking-read`; this report does not claim the user's running API or GitHub has been updated.
