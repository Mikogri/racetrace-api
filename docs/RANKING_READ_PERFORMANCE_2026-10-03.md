# Ranking and rider overview read performance — 2026-10-03

## Implemented

- `Database/optimize_ranking_reads.sql`: `get_ranking_data` retains the original SQL predicates, joins, compact fields and security model. Parameterized PL/pgSQL `EXECUTE` replans for the actual filter values instead of reusing the pathological generic plan. Request values are not concatenated into SQL.
- `get_ranking_points_raw_summary`: when stored summaries are incomplete, fetch rider identities and point totals only. All original round/session/status predicates are reapplied, even when session IDs were resolved earlier. No rival splits, team history, or metric calculation is needed for this position.
- `CompactMetricsPackageParser`: read ordered packages directly instead of cloning their rows into a merged JSON document. Expand each final rider/team identity once. Preserve global last-row-wins resolution, calculation order and independent output JSON ownership.
- `DomainQueryService`: share identical anonymous team-history reads within the request. Different dates/IDs remain separate; privileged import reads bypass reuse. Failed/cancelled source tasks can be retried. This is not an additional cross-request cache.
- Metric formulas, category boundaries, sort/tie rules, public endpoint shapes, cache lifetimes and UI are unchanged. Raw points sums still include zero-point riders and all matching point records.

## Verification

- Captured **26 full public service responses before changing SQL/source**, then compared serialized JSON structures after the final changes: **26/26 identical**. Compared preaggregated and fallback paths separately, not one against the other.
- Cases cover rankings for all seasons and 2026, elite/junior men/women, season/date/session filters, rating/pace/consistency/points sorting, previous-round/previous-filter position comparisons, empty filters, and overviews for rider IDs 18, 183 and 60. Ranking responses use page size 150; this is not an exhaustive test of every URL or every page.
- The requested `class=WCH` characterization returned an empty selection in the existing API too. Actual `world_championship` filtering is covered separately by the SQL regression script; do not treat the empty characterization as a populated World Championship ranking test.
- Transactional SQL deployment compared all original/new compact rows and columns for the entire EM field and a selected 2026 rider. SQL array row order was normalized for this comparison; final API response arrays were compared without normalization.
- **29 live database regression cases** compare every rider's raw point sum and identity names against the raw-ranking payload, including 4 categories, status filters, zero/missing points, conflicting IDs/codes/names, wildcard strings, dates, duplicate session IDs and empty seasons. Reproducible read-only script: `UCI_Stats_API/Database/Tests/verify_ranking_points_summary.sql`.
- **695 tests pass**, Release build succeeds. The normal suite contains opt-in database tests that return without running when their environment variables are absent; the live checks above were executed separately against the configured database.
- New unit coverage: global identity resolution, immutable cached payloads, independent result JSON, team-history date/role isolation, failure retry, concurrent pending reads/cancellation, raw fallback request scope/status, exact context payload and points ties.
- Independent review found a session-only fallback filter regression; it was fixed by reusing the full metrics payload and original SQL filtering CTEs. Re-review found no remaining concrete regression. `git diff --check` passes; graphify updated.

## Measurements

Median seconds, three runs per case, Release isolated service harness, empty API memory cache at the start of each cold run. Existing live database, no PostgreSQL page-cache flush and no host restart. Fallback mode simulates absent preaggregation RPC responses without deleting stored preaggregations. The subsequent raw database requests are real. Overview fallback mode primarily affects its points-position lookup; overview's detail RPC is unchanged.

| Request | Stored summaries | Before | After |
|---|---|---:|---:|
| Ranking EM, 2026, previous-round comparison | Available | 1.430 | 1.220 |
| Ranking EM, 2026, previous-round comparison | Missing | 1.937 | 1.220 |
| Ranking EM, all seasons, previous-round comparison | Available | 4.131 | 3.580 |
| Ranking EM, all seasons, previous-round comparison | Missing | 32.150 | 4.260 |
| Rider 18 overview, EM, 2026 | Available | 0.962 | 0.740 |
| Rider 18 overview, EM, 2026 | Missing | 1.488 | 0.740 |
| Rider 18 overview, EM, all seasons | Available | 3.073 | 2.490 |
| Rider 18 overview, EM, all seasons | Missing | 21.170 | 2.530 |

After values rounded to two decimals. This measures service execution including DB HTTP calls, not browser rendering or public API output-cache hits. Warm/replay timings from the temporary harness are not reported: reusing a scoped service would also reuse the new request-local history memoization between those synthetic calls.

Single database `EXPLAIN ANALYZE` comparison for raw EM/all seasons: **14.388 s → 1.025 s**. Shared buffer hits: **104,926 → 5,482**; temporary blocks: **502 → 0**. The formula calculations were not the main cause of this slowdown.

The all-season points fallback now returns about **66 KB**, instead of the roughly **1.9 MB** complete raw ranking package. Overview's history reads decrease from 3 to 2 on a cold request: a bounded historical lookup and the full current/presentation history are intentionally different queries.

Timings vary with remote database/network load. A prior candidate batch measured the preaggregated all-season overview at 4.33 s despite less API work; the final batch ranged 2.36–3.22 s. Do not interpret the measured median as a guaranteed latency. `get_rider_detail_data` remains the principal broad-overview database cost.

## Deployment and evidence

- SQL changes have been applied to Supabase transactionally. Both functions are `SECURITY INVOKER`, with empty `search_path`, explicit anon/authenticated/service-role execution and no PUBLIC execution grant. No race results, points configuration, formulas or preaggregation records were edited/refreshed.
- Local API source requires rebuild/restart to use the C# optimizations (unless the running development host reloads it). The existing development server was not stopped. No commit or push performed.
- Supabase CLI was unavailable; SQL is stored in the existing `Database` directory convention and was applied through the database connection.
- Temporary audit directory: `C:/Users/Mikolaj/AppData/Local/Temp/racetrace-perf-77b1210760e847729988cf480b116929`. Contains reference JSON under `parity/`, `baseline.log`, `after-final.log`, `run.log`, `final-benchmark.log`, `tests.log`, diagnostic source and `rollback.sql` (original function definitions). No credentials are in the report or SQL scripts. Temporary files are not committed artifacts.
- Unrelated pre-existing frontend Live Timing edits were left untouched.
- Build reports an existing `Microsoft.OpenApi` 2.0.0 vulnerability warning ([advisory](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)). Dependency changes were outside this optimization.
- Database advisor also flags unrelated existing mutable `age_at_event_year` search path, public `pg_trgm`, and execution grants on preaggregation SECURITY DEFINER helpers. This change does not add SECURITY DEFINER functions. Those findings require a separate access review: [function security guidance](https://supabase.com/docs/guides/database/database-linter?lint=0028_anon_security_definer_function_executable).

Planner rationale: [PostgreSQL parameterized EXECUTE documentation](https://www.postgresql.org/docs/current/plpgsql-statements.html#PLPGSQL-STATEMENTS-EXECUTING-DYN).
