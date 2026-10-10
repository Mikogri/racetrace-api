# API contracts

Backend DTOs and controller response metadata are the source of truth. `contracts/openapi.json` is exported by the official ASP.NET Core OpenAPI generator. The frontend uses pinned `openapi-typescript` to generate `src/types/generated/api.d.ts`; do not edit either generated file.

Keep the API and web repositories side by side. From `racetrace-web-app`:

```sh
npm ci
npm run contracts:export    # refresh only the backend document
npm run contracts:generate  # export and generate TypeScript
npm run contracts:check     # re-export into a temporary directory; fail on drift, never rewrite tracked files
npm run contracts:test      # generator/freshness tests and compile-time schema compatibility
npm test                   # includes runtime Zod regressions
```

Backend-only export (use an absolute output path):

```sh
dotnet run --project tools/OpenApiExport -- /absolute/path/openapi.json
dotnet test racetrace-api.Tests --filter FullyQualifiedName~OpenApiContractTests
dotnet run --project tools/OpenApiExport -- --algorithms /absolute/path/algorithms.json
```

The exporter starts an isolated, loopback-only metadata host. It does not run the production entry point, register Supabase clients, load secrets, or start background jobs. Dependencies must already be restored for fully offline operation. Object keys are sorted ordinally and the ephemeral server address is removed; no timestamp is embedded. JSON line endings are normalized during freshness checks.

The `--algorithms` mode writes the current catalog's effective parameter values, versions and dependency edges without starting any HTTP host. Archive that deterministic manifest with deployment artifacts to reproduce configuration identity; it is separate from the API contract version.

## Compatibility checks

`ApiContractExtensions` aligns OpenAPI names with MVC snake_case defaults while preserving explicit `JsonPropertyName` overrides. Response-only DTO properties are documented as present when the serializer always writes them, including nullable values. Canonical JSON number types are emitted; MVC still accepts numeric strings in request bodies for backward compatibility. Dates remain `date`/`date-time` when the backend declares `DateOnly`/`DateTimeOffset`; arbitrary string dates are not guessed.

Legacy query parameters read from `Request.Query` are documented in `ApiQueryContracts`, including aliases and CSV string filters. This metadata must be updated alongside parser changes. Parameters with alternative aliases remain individually optional; runtime validation still enforces required combinations and valid ranges. The generator does not replace backend validation.

Existing feature API modules use generated schema types in compile-time assertions against **Zod input**, not its normalized output. Numeric identifiers may still become presentation strings. Zod continues to validate actual responses, defaults and refinements at runtime; TypeScript cannot prove numeric refinements or URL validity. Compile-only negative checks prove nullability, renamed fields and enum drift fail compilation. Existing presentation subsets intentionally ignore unused backend fields.

The application uses TypeScript 6; `openapi-typescript` 7.13 currently declares a TypeScript 5 peer range. A package-specific npm override reuses the application's compiler rather than downgrading it or disabling peer checks globally. Generation, freshness, typecheck and runtime tests cover this combination; remove the override when upstream officially accepts TypeScript 6.

## Explicit limitations

Every controller action declares a successful wire type, but not every response is a closed DTO. Existing `JsonObject`/`JsonNode` responses remain open JSON: generic admin edits, editable resources, recalculation/refresh envelopes, admin Fantasy sync envelopes, rider detail/resolve, round detail/seasons, search, tracks/detail/records, teams, conditions, point systems/classes, legacy live sessions/leaderboard and ChronoRace provider payloads. Typed list envelopes still describe pagination. No fields were invented from sample responses, and provider payloads continue to use their existing Zod parsing.

Rider overview preprocessing and raw legacy rider session data retain their compatibility adapters. Their strongly typed statistics/round/track, team-history and pressure sections are checked separately; dynamic/preprocessed sections are not claimed to have full generated-shape coverage. Enum metadata only narrows verified finite wire values (Fantasy gender, ranking comparison and championship states); open database-driven category codes stay strings. Multipart PDF import keeps its existing JSON-string `request` field and file list.

This stage does not remove all legacy JSON contracts. Converting those requires a separate backend DTO migration verified against real DDL and representative fixtures, not guessed frontend models. Wire format, hooks, query keys and UI are unchanged; nullable team-history names are now accepted as the backend already permits them.
