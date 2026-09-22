# Refactor: domain display metadata

## Scope and invariants

The database owns session names, abbreviations, category labels/gender/order and
round/session status labels. The API exposes one typed, cached read; the frontend
uses its existing category registry and a shared presentation lookup.

Calculators, scoring, time units, session classification, point-system overrides,
filter meaning, live provider polling, layout and animations are outside this
refactor. Existing public codes remain compatible. Local prediction experiments
and frontend color edits are not part of this work.

## Implementation sequence

1. Inspect actual database constraints and both applications; record regression baseline.
2. Add presentation dictionaries and category fields with read-only RLS; replace
   the closed session-type check with a validated dictionary FK. Preserve records.
3. Expose `GET /api/v1/metadata` through the existing protected public API.
4. Consume metadata using one shared TanStack Query entry; preserve generic fallbacks.
5. Remove duplicate display maps, test unknown values and existing labels, verify
   API/frontend suites and compare existing calculation contracts.

## Operational rules

Apply `Database/20260921_display_metadata.sql` before deploying the new API.
The migration does not change session/result values or weights. It initializes
presentation fields only, and does not overwrite already configured labels on rerun.

Register a new type in `session_types` before importing sessions using its `code`.
Edit `name`, `short_name`, `abbreviation`, `group_name` and `display_order` there.
Categories retain existing `name`/`code`; optional `short_name`, `gender` and
`display_order` control presentation. Missing gender means open/unspecified.
New types do not automatically acquire final/qualification semantics or scoring:
that is business behavior, not presentation configuration.

`display_order` orders the metadata catalog and category selectors. Existing
finals-first Live grouping and chronological result ordering remain unchanged;
they are not replaced by the dictionary's catalog order.

Metadata caches use five-minute freshness. Updates are not pushed to already open
tabs instantly. Failed reads must not erase previously learned labels.

Legacy category/session aliases remain a compatibility boundary in the API.
Public reads resolve other codes against the dictionary before querying results;
unregistered values are still rejected. Existing aliases add no database request.
Multiple new-code filters in one request share one metadata snapshot.

The frontend retains a small compatibility fallback for established labels when
talking to an older/unavailable API. Successful metadata always takes precedence;
new database entries do not require additions to that fallback. Countries/ISO
formatting, result-status behavior and provider-specific adapters are not domain
label dictionaries and were not replaced with database configuration.

## Verification (2026-09-22)

- Migration applied to the configured RaceTrace Analytics database.
- Before/after session count: 472; category count: 4; result count: 20,743;
  time-split count: 76,842. Hashes of existing session code/name/status/category
  fields and category code/name fields are unchanged.
- Anonymous RPC read succeeds (4 categories, 9 session types). Anonymous and
  authenticated roles cannot write the new metadata tables. RLS is enabled and
  the session dictionary FK is validated.
- A new test session type appeared in the metadata RPC inside a transaction;
  rollback removed the test entry. No test riders/results were inserted.
- Old running API versus isolated new Release host, same live database: complete
  JSON equality for male/female 2026 ranking (five riders each), rider 60 and 113
  overviews, Compare (60 + 13), and round 7 sessions. No numeric fields ignored.
- The isolated verification API had scheduled Fantasy synchronization disabled
  and was stopped after testing; the user's running API was not restarted.
- API Release: 601 tests pass, including 20 metadata contract tests and 13 dynamic
  filter cases. Existing calculation and response fixtures continue to pass.
- Frontend: 130 tests pass; lint, typecheck and production build verified. Tests cover
  metadata authority, aliases, reactive updates, dynamic types and failed refresh.
- Independent review fixes: Live resolves display metadata by stable session type
  before provider display names; unavailable metadata retains short training labels.

## Existing issues intentionally not changed

- Compare's `includes("final")` label rule also gives semifinals `FIN`. Tissot
  session normalization contains a related final-name classification heuristic.
- `LiveService.hasFilters` does not cover all list-only category/session filters;
  a no-match multi-value filter may fall back to all live sessions.
- Result validity/leader selection recognizes selected fixed statuses. Adding a
  label for a status such as DNQ must not silently define its sporting semantics.
- Tissot/ChronoRace selection partly relies on the competition's name. Renaming
  that name is not a safe substitute for explicit provider configuration.
- NuGet reports the pre-existing NU1903 advisory for Microsoft.OpenApi 2.0.0.
  Dependency upgrades were not bundled into a behavior-preserving refactor.

## Deliberately deferred

- Moving calculation roles, qualification rules or import classification into
  configurable database rules requires separate domain tests and product decisions.
- Generated API clients/OpenAPI types across the entire frontend and decomposition
  of the largest calculator services are larger, independent changes.
- Instant metadata invalidation after imports is not implemented; current cache
  freshness remains five minutes rather than introducing subscriptions/polling.
