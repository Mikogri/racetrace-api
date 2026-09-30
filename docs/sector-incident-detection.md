# Sector incident detection

`Services/SectorIncidentDetector.cs` is shared by raw and preaggregated Pace calculations. Official results are never overwritten. No frontend calculations or new public HTTP endpoint are introduced.

## Signals

- Uses **sector durations**, not cumulative gaps; each session/category supplies its own field median and MAD.
- Expected duration = sector field median × median rider/field ratio on the **three nearest other usable sectors** (ties choose the earlier sector). At least two reference sectors and three field times are required.
- Large-loss gate: **≥4 seconds**, robust log deviation **≥1.5**, deviation plus positive normalized rank deterioration **≥4**, and at least two clean reference sectors. The denominator uses field dispersion, local rider dispersion and a 2% floor. Clean means within 1.5 robust scales of the local baseline.
- Smaller-loss gate: **≥2 seconds**, deviation **≥2**, rank deterioration **≥0.5 of the field**, at least three clean references and a recovery. Both immediately adjacent measured sectors must be clean and ranked within the top 20%; the median of at least three other known ranks must be within the top 15%. These ranked sectors and the tested sector need fields of at least six. Small opening/terminal losses cannot pass this exception.
- Thresholds are conservative evidence gates, **not calibrated crash probabilities**. No name, rider ID or manual incident label is consulted by production code.
- Detects opening and terminal losses as well as losses after recovery. Consecutive damaged sectors do not count as separate incident runs.
- A large local anomaly may be flagged without recovery, but cannot authorize finish reconstruction. A supplementary stable-prefix check labels otherwise-unlocalized persistent slowdown as possible, not strong.
- Non-positive/non-finite or extremely short times are excluded. A compensating long/short adjacent pair is considered suspect only when the short sector is also implausibly fast relative to the field. This is a timing-quality flag, not proof of a faulty checkpoint.

## Metric effects

- Strong incident sectors and suspected invalid timing are excluded from sector Pace.
- Possible incidents retain their low Pace score. Ordinary slow runs are not cleaned.
- Normal sectors after recovery may contribute to Pace, including for slower riders. Confidence still reflects only measured clean evidence, never estimated sectors.
- Only strong incident candidates contribute to `HasIncident`; existing sporting DNF handling remains separate.
- Invalid timing is excluded from sector Consistency comparisons and outlier projection, not counted as a crash or missing attempt. Real incident durations remain in Consistency inputs.
- Session/season weighting, score calibration and rating combination remain unchanged. **Metric outputs change** through incident detection and corrected finish populations.

## Two-pass finish correction

1. Diagnose all runs using the original sector populations for that session/category.
2. For classified complete runs, subtract only positive estimated excess of strong incidents that have recovery or are terminal. At least two clean sectors and at least half the course must remain; possible incidents, invalid timing, incomplete checkpoints or sector/finish sum mismatches prevent finish reconstruction. DNF/DNS/DSQ never get an invented finish.
3. Build one median/MAD/best/second-best finish distribution from the corrected whole field before scoring any rider. Full-run Pace and dominance use that same distribution, including for riders without incidents.
4. Incident sectors do not receive synthetic sector scores. For safely reconstructed runs they are removed from the Pace coverage denominator; the minimum measured-sector evidence rule remains. Published coverage and Confidence still count actual observations. Consistency retains the real incident loss and penalty.

`PacePrecomputedSessionData.PaceFinish` holds an immutable calculation-only reference. `Run.FinishTimeSeconds`, finishing positions, points and displayed result times remain original. Preparation happens before rider/search filtering; filtering cannot shrink the reference field. Partial legacy packets retain the legacy checkpoint fallback rather than pretending a partial population is complete.

The additive migration `UCI_Stats_API/supabase/migrations/20260930212704_pace_incident_field_context.sql` extends `get_rider_detail_data` with compact `i` rows: `[sessionId, riderId, finishSeconds, [[sectorIndex, seconds, rank], ...]]`. Only classified rival timing data is added, without identities/teams/splits or repeated statistics. It reuses the existing SQL CTEs in the same RPC; detection stays in C#, not duplicated in SQL. Full ranking packets already carry this population, so existing preaggregates do not need rebuilding. Detail parsing reuses the selected rider's shared field statistics and returns only the originally selected rider rows.

Fast-sector selection rounds only its sorting key to nine decimal places, preventing ~1e-12 transport differences from selecting differently weighted tied sectors. Calculations retain full precision.

## Read-only season audit

From the API repository root:

```powershell
dotnet run --project tools/UCI_Stats_API.PodiumBacktest -c Release -- --incident-audit --api UCI_Stats_API --year 2026 --output artifacts/incident-audit
```

The tool reads development Supabase configuration locally (credentials are not exported), fetches session preaggregations and aborts if a session package is missing. `--offline` reuses `inputs-2026.json`; `--name baseline` can capture the old calculator **before changing code**. It writes JSON calculator results, `incidents-2026.csv` and a Markdown report. The CSV includes identifiers, estimated loss, reason and an empty `Verification` column. Artifacts are ignored by Git.

The audit also compares anomaly classifications using recalculated field statistics versus preaggregations, failing if classifications diverge. This checks computational consistency, not whether crashes actually occurred.

`--confirmed tools/UCI_Stats_API.PodiumBacktest/Fixtures/incidents-2026.csv` evaluates the user-supplied labelled positives. `--baseline before-csv.json` compares the saved previous detector on identical runs. `--verify-details` also makes five read-only detail RPC checks against the frozen ranking inputs (Pierron, Williams, Bruni and two Höll sessions). Raw/preaggregated Pace must match to 1e-7, not only incident classifications. Name aliases live only in this audit, never in the production detector.

## Historical validation snapshot (2026-09-30, superseded detector)

- 109 supplied records: 86 eligible mapped runs, 4 DNS/DSQ, 19 training/unspecified-session records.
- On those same 86 runs: 37 strong indications before the rank-corroborated rule, 48 after; 7 additional possible indications and 31 unlocalized incidents. This is run-level detection, not independently verified sector localization.
- All 3,664 frozen run inputs: identical incident classifications and Pace (tolerance 1e-7) between raw and preaggregated computation. Five separate live detail RPC checks also matched.
- All official runs in the snapshot: 272 → 361 strong indications. The 89 added indications include 11 supplied positives; the other 78 require review, **not** assumed false or true positives.
- Mona Yongpyong final, Amaury Pierron: S4 41.601 s; expected 39.157082 s; estimated excess 2.443918 s. Official finish 165.364 s is preserved; analytical finish 162.920082 s; session Pace 92.935136 (highest in this field), Confidence 40.870311. Jordan Williams remains unlocalized.
- Detailed per-record validation: `docs/incident-validation-2026.md`. Full candidate CSV and frozen inputs are generated under ignored `artifacts/incident-audit/`.

## Promoted conservative detector (2026-10-01)

- The production port matches the approved research candidate on all 3,486 official runs: identical flagged sectors and estimated losses (1e-7 tolerance).
- 347 flagged runs; 54 of 86 eligible supplied positives detected. Raw/preaggregated parity: 3,664 runs with zero differences. Five read-only rider-detail checks also match ranking inputs.
- Pierron, Mona Yongpyong final: S4 excess 2.532056 s; analytical finish 162.831944 s; session Pace 93.153853. Official finish remains 165.364 s.
- Small terminal losses discussed for Pontvianne, Shaw and Kolb are no longer flagged; Alran's Les Gets Q1 loss is detected. The known Nicole opening-sector loss remains a miss; do not present the detector as perfect.
- Full TOP 20 Rating/Pace/Consistency comparison for 2026 and 2025: `docs/ranking-incident-comparison-2025-2026.md`. Earlier experiment documents are historical, not current production specifications.

## Limits / manual validation

No crash, mechanical fault or weather cause can be confirmed from splits alone. Historical runs and starting-order/weather correction are **not yet used**: the detector currently has a deterministic per-run input shared by ranking and rider details, without a reliable starter-time cohort. Missing/nonmonotonic raw splits already sanitized upstream cannot be retrospectively identified as a specific timing fault here. A run damaged from the start or with too few clean sectors may escape localization. One bad sector among only two measured sectors cannot be reliably isolated.

Validate true/false positives and missed known incidents by category, session and course before tuning thresholds. A larger number of flags is not evidence of improved accuracy. This CSV is positive-only and was used during development, not a held-out benchmark: it cannot establish precision or unbiased generalization. No rider name, ID, round or CSV label affects production detection. Restart the API to load changed calculator code; apply the additive detail-context migration before deployment. There is no need to rebuild existing full ranking preaggregates.
