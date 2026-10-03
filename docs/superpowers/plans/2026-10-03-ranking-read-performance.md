# Ranking read performance implementation plan

**Goal:** Reduce ranking/overview latency without changing API responses, filters, metric formulas or UI.
**Architecture:** Preserve contracts. Replan the existing raw SQL per call, add a points-only fallback, and reduce repeated JSON processing within a request.
**Tech stack:** PostgreSQL/Supabase, ASP.NET Core net10.0, xUnit.
**Spec:** User-approved optimizations from the 2026-10-03 latency audit.

## Constraints and review focus

- Preserve category isolation, all filter combinations, empty results, ties and historical team semantics.
- Baseline full responses before database changes; compare preaggregated and raw paths independently.
- Keep RLS/security-invoker behavior; never interpolate request values into SQL text.
- Preserve JSON ownership: cached objects must not be mutated by consumers.
- Preserve package ordering and cross-session identity resolution.
- Do not touch unrelated Live Timing frontend changes; no commit/push requested.

## Execution

- [x] Capture 26 baseline responses (rankings and multiple riders, both cache paths).
- [x] Replan raw ranking SQL using parameterized PL/pgSQL EXECUTE; compare raw DTO results and timing.
- [x] Add points-only raw summary; test missing-preaggregation fallback and tie behavior.
- [x] Reduce redundant package expansion/cloning, retaining globally resolved identity metadata; test isolation and response parity.
- [x] Deduplicate team-history reads within a request; test dates, roles, concurrent callers and failures.
- [x] Run full API suite, full-response parity, timing comparison and graph update.

Database deployment is transactional and the old function definitions are retained outside the repository for rollback. Diagnostic baseline/candidate processes use separate build outputs.

Review found that resolved session IDs alone did not preserve conflicting ID/code/name filters. The final points fallback reapplies the exact raw-ranking context predicates. Its 29 database regression cases include these combinations and wildcard names; all passed. Full-response parity remains 26/26, and the API suite passes 695 tests. Detailed measurements and scope are recorded in `docs/RANKING_READ_PERFORMANCE_2026-10-03.md`.
