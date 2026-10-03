# UI and API duplication cleanup — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove confirmed duplication without changing rendering, interactions, calculations or API contracts.

**Architecture:** Reuse existing components and mapping utilities. Extract only repeated presentation scaffolding, keeping page-specific fetching and styling at the call site. No generic page framework.

**Tech Stack:** React, TypeScript, CSS Modules, Vitest; ASP.NET Core, xUnit.

**Spec:** User request in this conversation: modest frontend/API cleanup; application behavior and output must remain unchanged.

## Global constraints

- Preserve local frontend edits hiding ranking request details and restoring rider Tracks/Results bottom padding.
- No backend/frontend responsibility transfers, dependencies, database changes, query changes, cache changes or calculator changes.
- Preserve DOM structure, accessible labels, CSS timing, layout and failure/retry behavior.
- Do not commit or push without a further user request.

## Review focus

- Initial loading versus refetch: content must remain present during refetch.
- Error versus empty state: error wins and retains retry; rider tab feedback has animate=false.
- Table pagination: preserve 15-row pages, sort state and click targets.
- Invalid identifiers: preserve zero versus null conversion, including overflow.
- Empty condition name: Results currently produces empty slug for whitespace while PublicDtoMapping produces null; do not merge these different behaviors accidentally.

## Task 1 — Shared loading indicator markup

Frontend root: `C:/Work/Projects/UCI_Statistics/racetrace-web-app`.

- [ ] Add `src/components/feedback/update-progress.tsx` with `UpdateProgress({className, label})`: same div role=progressbar, aria-label and child span as existing callers. No wrapper or conditional fetching inside it.
- [ ] Add `update-progress.test.tsx` covering accessible label, forwarded class and exact child structure.
- [ ] Replace identical div/span indicators in `components/data-display/data-table.tsx`, `features/ranking/ranking-page.tsx`, `features/riders/rider-route-layout.tsx`, `features/fantasy/fantasy-page.tsx`, and any verified identical call sites. Keep CSS and visibility conditions unchanged. Leave structurally different span/i indicators alone unless equivalence is established.
- [ ] Run frontend tests, lint and typecheck. Inspect diff for unchanged visibility predicates and labels.

## Task 2 — Rider table presentation scaffold

- [ ] Add `src/features/riders/components/rider-table-content.tsx`: accepts children, hasDataError, isEmpty and onRetry. Render the existing content/resultsPage div once; inside, error/empty TableFeedbackState with animate=false or children.
- [ ] Add `rider-table-content.test.tsx` covering error precedence, retry callback, empty state, children and unchanged class names.
- [ ] Reuse it in `rider-tracks-page.tsx` and `rider-results-page.tsx`. Keep table markup, scroll wrapper, pagination, sorting and navigation unchanged. No CSS changes.
- [ ] Verify both pages with populated, empty and error fixtures; compare DOM before/after and check Statistics remains unaffected.

## Task 3 — Reuse API identifier mapping

API root: `C:/Work/Projects/UCI_Statistics/UCI_Stats_API`.

- [ ] Characterize `PublicDtoMapping.Long` and `NullableLong` in `UCI_Stats_API.Tests/PublicDtoMappingTests.cs`: null, empty, whitespace, valid signed values, zero, invalid text and overflow. Verify existing private helpers use the identical TryParse overload.
- [ ] In `UCI_Stats_API/Services/RankingService.cs` and `RoundsService.cs`, replace private ParseLong/NullableLong calls with existing PublicDtoMapping methods and remove duplicated implementations. Do not change sorting or conversion culture.
- [ ] Keep Results ToCondition separate: whitespace handling differs, so consolidation would change public output.
- [ ] Run all API tests using isolated artifacts: `dotnet test UCI_Stats_API.Tests -c Release -p:UseAppHost=false --artifacts-path .codex-build/quality`.

## Final verification

- [ ] Run `pnpm test`, `pnpm lint`, `pnpm typecheck`, `pnpm build`; retain current tests unchanged.
- [ ] Run API suite and compare representative ranking/overview/results serialized responses before/after against fixed inputs where available. Do not claim live parity without running it.
- [ ] Run git diff --check in both repositories and graphify update after edits.
- [ ] Summarize actual scope, verification and any unverified visual behavior. No claim that the entire project was rewritten/audited.
