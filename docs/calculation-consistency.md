# Calculation consistency and rollout

## Scope

This release adds infrastructure, not new sports formulas. Calculator defaults and incident thresholds remain unchanged. New baseline algorithm labels start at `1.0.0`; existing prediction model identifiers are retained. No historical version numbers are inferred.

Main files:

- `Infrastructure/Calculations/AlgorithmCatalog.cs`: dependency graph, canonical configuration hashes and version identities.
- `Services/AlgorithmOptions.cs`: shared immutable calculator defaults and incident thresholds.
- `Infrastructure/Calculations/DataRevisionService.cs`: lazy, process-wide revision read, fresh for at most five seconds. Failed checks do not extend freshness.
- `Infrastructure/Calculations/CalculationProvenance.cs`: response provenance headers and concurrent-write check before serialization.
- `Services/RankingService.cs`: revision/version-aware memory and SingleFlight identities; an outdated in-flight calculation is rejected, not published under a current key.
- `Infrastructure/ApiReadOutputCachePolicy.cs`: query parameters plus data/algorithm identity, dependency tags, final storage check.
- `Services/DomainQueryService.cs`: session-scoped raw package caching and existing prepared-data fallback.

## Rollout (explicit administration required)

1. Apply `supabase/migrations/20261010210121_calculation_revisions.sql` after existing source/preaggregation migrations.
2. Apply `supabase/migrations/20261010210124_fantasy_job_coordination.sql` for distributed Fantasy coordination.
3. Enable `CacheCoherence:Enabled=true` (`CacheCoherence__Enabled=true` as an environment variable) on **all** API instances.
4. Enable `Fantasy:DistributedCoordinationEnabled=true` on all scheduled and manual workers together. Read `fantasy-browser-sync.md` before migrating existing local artifacts.
5. Rebuild prepared calculations through the existing administrative workflow. Old algorithm-specific packages are not relabelled; reads fall back to raw input while the matching version is missing.

Flags default to false so deploying binaries before the SQL does not break existing installations. They are not an automatic migration mechanism. A deployment with flags disabled retains the legacy cache/coordination guarantees. Both migrations were applied to RaceTrace Analytics on 11 October 2026 (Europe/Warsaw), after disposable PostgreSQL verification. Runtime flags were not changed during publication; see [deployment verification](deployment/2026-10-11.md).

## Invalidation

| Change | Affected identities |
| --- | --- |
| Result, split, awarded points, session or round metadata | Session/round/season and unrestricted reads; raw packages of that round are removed transactionally |
| Moving a result/session or changing a round's season | Both old and new dependency scopes |
| Rider/team history or track metadata | Rounds containing affected records, plus unrestricted reads |
| Global category/condition/class/scoring dictionaries | Global dependency and raw packages |
| Pace/incident parameters or implementation version | Prepared Pace inputs and all dependent Rating, predictor and analytics identities; original imported results are kept |
| Consistency parameters/version | Consistency, Rating and their dependents; unrelated Pace preparation stays reusable |

Revision updates share the source SQL transaction. Rollback also rolls back invalidation. Reads never increment revisions. Existing importers can use several HTTP transactions; this mechanism does **not** turn a multi-request import into a single database transaction. It protects each committed change and rejects calculations spanning detected changes. Run large imports administratively, not as an assumption of cross-request atomicity.

Instances keep independent caches. They read the same small revision map, at most once per five seconds on ordinary reads. Completion checks bypass this short cache before publishing newly computed snapshots. Old entries expire normally but are no longer addressable by the new revision key; unrelated seasonal/session entries stay reusable. No Redis, global cache clearing or subscription is required.

SingleFlight remains process-local: identical keys share one calculation; different keys run independently. A cancelled waiter cannot cancel the shared work. It is not a cross-instance ranking lock.

## Provenance and reproducibility

Analytical responses expose `X-Algorithm-Version`, `X-Configuration-Hash`, `X-Computed-At`, and (when enabled) `X-Data-Revision`. OutputCache stores original headers with the body. Cached ranking snapshots retain their computation timestamp; when a response combines snapshots the timestamp identifies the oldest reused calculation, not the HTTP delivery time.

Podium responses additionally expose model version, prediction-as-of, historical cutoff, simulation seed and iteration count in headers. The historical cutoff is not claimed to be the unavailable training date of the existing static ridge coefficients. Fantasy prepared artifacts retain their actual preparation time, data revision, dependency fingerprint and configuration hash, alongside the existing calibration history.

Keep the exported algorithm manifest with each release. Bump an affected algorithm version when its implementation changes; parameter edits change the configuration hash automatically. Dependency closure propagates both changes. Class weights continue to come from database priorities, covered by data revisions.

## Browser freshness

TanStack Query preserves previous content while refreshing. Existing 30-second data freshness, five-minute garbage collection and five-minute dictionary freshness remain. Returning to the window now refreshes stale queries; mounting stale queries and explicit refetch also refresh them. There is no global polling. An already-open idle browser is **not** promised a five-second update; the bound applies to backend revision checks when requests are made.

## Verification

- `dotnet test racetrace-api.Tests -c Release -p:UseAppHost=false`
- `tools/test_calculation_revisions.py`: isolated PostgreSQL trigger, rollback, dependency and permissions tests.
- `tools/test_fantasy_jobs.py`: real concurrent lease acquisition, fencing, retry and publication tests.
- `tools/CalculatorParity`: compares full fixed-fixture numerical outputs against an archived baseline assembly, not approximate scores.
- See `API_CONTRACTS.md` and `performance-monitoring.md` for contract export and telemetry commands.

The five-second window is intentional bounded consistency, not serializable reads across arbitrary concurrent HTTP transactions. A detected mid-calculation change returns retryable `503 DATA_REVISION_CHANGED`; incomplete new calculations never replace a successful cached snapshot.

### Verified locally (2026-10-10)

- API: 775 tests passed. Some pre-existing optional database tests return early without external credentials; these counts alone are not production integration evidence.
- Frontend: 170 tests passed (`vitest run --maxWorkers=2`), ESLint, TypeScript and production Vite build passed. No stylesheet or view component was changed.
- Contracts: 66 exported paths, current OpenAPI/TypeScript check passed, two generator checks passed. Repeated algorithm manifest exports were byte-identical.
- Numerical regression: 49 full serialized result bundles exactly match the pre-change assembly, including incidents, DNF, Rating boundaries and fixed-seed normal/WCH simulations. This is fixture coverage, not a comparison of every production rider.
- PostgreSQL: real loopback tests passed for scoped revision triggers/rollback and eight competing Fantasy workers/fencing/publication. No production migration was applied.
- Remaining pre-existing warning: xUnit2031 in `DynamicFilterTests`. Existing frontend dev dependency audit warnings were not addressed by unrelated package upgrades; the production dependency audit was clean.

In distributed mode, a completed scheduled Fantasy time bucket remains idempotent. If dependencies change afterwards, reads reject the outdated forecast; rebuilding happens on the next eligible scheduled cycle or a manual run with a new idempotency key. It is not silently returned as current.
