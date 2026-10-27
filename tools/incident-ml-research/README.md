# Incident ML research (offline only)

No production detector, calculator, API, frontend or database is changed by this tool.
Python 3.10.11, CPU only; no provider calls or external AI inference. Dependency
installation is the only network-dependent step. Use the seven pinned packages
in `requirements.txt`; Pandas, plotting frameworks and neural networks are not needed.

## Reproduce on Windows

From the API repository root:

```powershell
python -m venv artifacts/incident-ml-venv
artifacts/incident-ml-venv/Scripts/python.exe -m pip install -r tools/incident-ml-research/requirements.txt
artifacts/incident-ml-venv/Scripts/python.exe -m unittest discover -s tools/incident-ml-research -v
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/audit_inputs.py
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/experiment.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

Required **existing** local data (not downloaded by this tool):

- `artifacts/incident-multiseason/YEAR/inputs-YEAR.json`, `baseline.json`,
  `conservative.json`, for 2021–2026. These are frozen C# audit exports.
- `artifacts/incident-conservative/labels.csv`: strict C# identity/session matching.
- `artifacts/incident-csharp/sectors.csv`: previous high-recall C# experiment.
- External `RaceTrace_Incidents_2021.md` through `RaceTrace_Incidents_2025.md`.
- Existing sporting-only `tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv`.

Missing inputs fail explicitly. To produce the C# inputs and matching exports,
use the existing audit/study commands in `docs/ALGORITHMS.md`; do not silently
replace this corpus with a new database download. The expected corpus is 20,340
runs and 341 unique matched positives. File hashes are recorded in the manifest.
Repeated source observations become one run label. DNS/DSQ and unscored training
sessions follow the existing C# matching exclusions, not invented negative labels.

## What is learned

Each row is a run, not a sector labelled as a crash. Seventy-one numeric features
summarize leave-one-sector-out timing residuals, field spread, ranks, recovery,
neighbour contrasts, missing data and checkpoint quality. Sector durations are
used, never cumulative splits. Length differences are represented by expected
duration and relative/log losses as well as seconds. Names, IDs, dates, season,
venue, team, category, final time/place and DNF status are **not** predictors.
Context/identity are retained only for splitting, matching and review exports.

Bagged logistic regression, LightGBM and XGBoost distinguish observed positives
from subsamples of the **unlabelled mixture**, with three deterministic bags.
This is a case-control PU ranking baseline, not a noise-corrected estimator of
crash probability: unknown positives contaminate the unlabelled controls and
reporting is not random (SCAR is not assumed). Zero means unlabelled membership
for this surrogate task, never a verified `no_crash` example. Outputs are scores.
A hybrid blends LightGBM score with the original C# flag, with fixed weights.

## Validation and availability

Six leave-whole-season-out splits plus forward tests for 2025 and 2026. Within
each training partition, the latest available year is reserved for calibration
and selecting between two hyperparameter settings. Models are **not fitted** on
that calibration year. This is a three-way fit/calibration/test split, not a
claim that every training-year row was used for estimator fitting. Entire events
and all their sessions/categories remain in one partition. There is no random
run-level split, no test-label tuning and no test-specific top-K selection.

Thresholds target 7.5%, 10%, 12.5%, 15%, 20% on calibration scores only, using strict
`score > threshold`. Ties are conservative. Actual test alarm rates can differ
substantially under distribution shift; a target budget is **not** a hard test
quota. C# budget curves prioritize original flags and then their diagnostic
deviation; these reranked curves are not a change to the fixed C# classifiers.
The previous high-recall baseline has only archived non-normal diagnostic rows,
so unflagged ties may make some budget points unattainable. Fixed C# rates are
also reported separately.

## Optional ONNX demonstration

```powershell
artifacts/incident-ml-venv/Scripts/python.exe -m pip install -r tools/incident-ml-research/requirements-onnx.txt
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/export_onnx.py --documents C:/path/to/incident-documents
dotnet run --project tools/incident-ml-research/onnx-parity -c Release -- artifacts/incident-ml
```

The exporter exits nonzero when native LightGBM scores disagree with converted
ONNX beyond 2e-5. It still saves diagnostics and inputs for the independent C#
demonstration. The console compares **ONNX Python vs ONNX C#**, and separately
reports **native LightGBM vs C# ONNX**. Passing the former does not imply passing
the latter. No API project references this console. C# feature extraction is not
ported here: the demonstration consumes the exact Python feature matrix. A later
integration would also need feature-construction parity and threshold decisions.

This is retrospective, after-run/after-session analysis: other sectors of the
run and finalized same-session field summaries are available. It is **not** an
online predictor before the rider finishes, nor an incident forecast. Contextual
field features may use other riders in the same finalized session without using
their incident labels. Historical years have already influenced detector design;
none is a pristine, never-inspected validation set. Riders may recur across years.

Sector localization is a **separate diagnostic residual ranking**, not supervised
sector prediction. Run labels never label all their sectors positive. Only
explicit sector references in source descriptions / the sporting CSV are used
for evaluation; ambiguous split references are ignored. Missing/compensating
checkpoint data may produce `unknown`. Exact and adjacent-sector hits are reported
both end-to-end and conditional on run detection; localization does not validate
the physical cause or the estimated removable time loss.

## Outputs

All data/model exports remain in ignored `artifacts/incident-ml/`:

- `manifest.json`: seeds, CPU/runtime, input/source hashes, tuning and latency.
- `metrics.csv`, `pooled.csv`, `subgroups.csv`: all models, budgets, seasons,
  categories and statuses; recall, actual alarms, added/lost hits, new alarms per hit.
- `localization.csv`: explicit-sector denominators and separate localization metrics.
- `predictions.csv`: run scores, not crash probabilities.
- `error-analysis.json`: forward-2026 LightGBM misses/new alarms, original times,
  model reference times, sector diagnostics and candidate locations.
- `*-MODEL.pickle`: isolated research models. **Never unpickle untrusted files.**
- `recall-alarm.svg`: out-of-season recall/alarm-rate curve.
- `license-inventory.json`, `licenses/`: installed versions, requirements and notices.

Event-cluster bootstrap intervals describe uncertainty in recall on **observed
positives**, conditional on the fitted model. They do not measure precision or
cover training/label-selection uncertainty. No precision, specificity, FPR or
calibrated physical-crash probability is claimed. Read `report.md` for measured
results and the deployment recommendation. No model is automatically promoted.

## Lower-alarm follow-up

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/focus.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

This separate experiment preserves the first results in `artifacts/incident-ml/`
and writes to `artifacts/incident-ml-focus/`. It tests 1%, 2.5%, 5%, 7.5%, 10%
calibration targets, retraining for lower-budget recall and comparing absolute
context ablation with aligned same-sector time/rank/recovery features. It reuses
the same fit/calibration/test partitions. Because its design follows inspection
of the earlier outer tests, it is exploratory, **not a fresh independent test**.
Original frozen scores at stricter thresholds are reported alongside retrained
variants. No unlabelled run becomes a verified negative and no model is promoted.

## Conditional timing: explicit 90% / 700-alarm acceptance target

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/target.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

This tests a newly agreed goal: **at least 307 of the 341 matched positives with
at most 700 actual alarms** in pooled leave-season-out predictions. Both must
hold. The 700/20,340 calibration fraction is not a hard test quota. Five operating
points (2%, 2.5%, 3%, 700/20,340, 4%) are declared before fitting; there is no
test-top-700 cutoff. Hyperparameters use calibration recall at the target
fraction only. Full results live in ignored `artifacts/incident-ml-target/`.

`conditional.py` builds same-session matched-peer references, excluding the rider
and matching on other sectors only, and masked conditional sector-time contexts
with leave-rider-out field centers. It fits LightGBM median/85th-quantile timing
regressors to the timing mixture, not a known clean population. Three event-group
cross-fitting partitions generate reconstruction-error features for classifier
training; a regressor never scores the rows it fitted when producing that
training feature. Calibration/test residuals use a regressor fitted on the fit
partition only. The run model compares original features with peer errors,
learned reconstruction errors and their combination. Original model scores at
the same calibration targets remain a reference. No new dependency is required.

This remains retrospective after-session research on previously inspected years,
not a fresh independent test. Matched peers are context, not labelled negatives.
Timing residuals and quantile widths are neither crash probabilities nor validated
removable time corrections. No name/ID/label defines a peer distance. Scores and
thresholds do not modify Pace, Consistency, Rating, official results or API code.

## Full-mixture and ranking objectives

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

This isolates the effect of estimator/objective and unlabelled sampling, using
the original 71 features. It compares weighted LightGBM on all fit observations,
ExtraTrees and LambdaRank with whole training seasons as ranking queries. The
year defines training query boundaries only, never a predictor. No session is
excluded for lacking reported positives. Relevance 0 means unlabelled, not
verified clean: pairwise preferences remain contaminated and do not identify
physical-crash probabilities. Hyperparameters use the latest training year's
calibration recall at 700/20,340; the same five predeclared budgets and whole-year
outer tests are retained. No test-top-700 cutoff is applied. Results, models,
subgroup counts and event-cluster recall intervals are written separately to
`artifacts/incident-ml-objectives/`. This is exploratory on previously inspected
years, not a fresh independent validation. No production model is changed.

## Run shape and earlier-round tempo

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation trajectory --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

`trajectory.py` replaces pooled feature extrema with sector-aligned time-loss,
jump, recovery and pre/post pace contrasts. Same-cohort paired sector ranks
avoid position changes caused solely by missing riders. Expected times cannot
be faster than the best measured sector. One variant uses no rider history;
two add earlier different-round, same-category timing estimates (up to eight
past observations, strict earlier date). Rider IDs join sporting history but
are never numeric/categorical predictors. Only the outer **fit** observations
can supply history, excluding calibration/test timings from training features.
The history estimate is a 35th-percentile within-run log relative sector time,
not an official outcome or a detector-corrected time. Missing history is explicit.
Recovery helps characterize a run but is not mandatory: persistent/terminal
incidents remain eligible. No labels select the suspected sector. Diagnostic
losses/localization are not validated pace corrections. The same chronological
and leave-season-out evaluations, calibration budgets and positive-only
limitations apply. Outputs stay in `artifacts/incident-ml-trajectory/`.

## Multi-family sector and kernel searches

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/kernel_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/report.py
```

`search.py` evaluates 18 sector configurations per outer split: hard latent-sector
assignment, hard-control mining, and direct noisy-OR multiple-instance learning,
with sector-only/context feature views. Explicit sector labels anchor training
bags only; they never enter test inference. Run-level labels do not mark every
sector positive, and other sectors of a positive run are not labelled negative.
The noisy-OR objective uses a finite-difference-tested exact bag gradient with a
positive Gauss-Newton curvature approximation. This remains a contaminated
observed-label objective, not an identified physical-crash probability.

The search also evaluates 15 fixed mixtures of original, shape/history and
calibration-selected MIL scores. Empirical-CDF mapping, model choice and mixture
weights use calibration data only. There is no test-top-700 quota. Source code,
grid, choices, scores, metrics and localization are saved separately under
`artifacts/incident-ml-multiple-instance/`.

`kernel_search.py` independently evaluates 38 configurations per outer split:
RBF SVM and positive-pattern nearest-neighbour/distance-contrast models, with
relative-only and shape/history views. Robust scaling is fitted only on fit
rows. Unlabelled controls are deterministic uniform samples, never a known
clean set. Full results are in `artifacts/incident-ml-kernels/`.

Both searches retain the predeclared five calibration budgets and eight outer
season tests. Cross-family choice is calibrated, but this wider research has
already inspected the same years: comparison winners are exploratory and must
not be presented as an untouched final validation or automatically deployed.

## Residual noise and grouped calibration studies

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation residual --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation trajectory --calibration grouped --output artifacts/incident-ml-grouped-history --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation residual --calibration grouped --output artifacts/incident-ml-grouped-residual --documents C:/path/to/incident-documents
```

`field_residual.py` standardizes within-run sector contrasts against peer
within-run residual noise, not just raw sector timing dispersion. Ridge sector
predictions use exact leave-one-out leverage correction; tests verify that
changing a target time cannot change its own expected value and compare against
explicit refits. Context uses finalized same-session measurements without labels.

Grouped calibration is a separate, predeclared policy: one fixed-seed
four-fold stratified round-group partition of the outer training seasons,
three folds fit and one calibration. Whole outer test seasons remain excluded.
It retains more recent training examples than reserving a whole latest year.
The baseline is retrained on the new fit partition, not reused from a model
that already saw calibration rounds. Grouped history also tests XGBoost.
Both policies remain exploratory; a better inspected-test score is not proof
of generalization. No synthetic benchmark records or production changes occur.

## Session-stage context and reporting-bias diagnostic

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation trajectory --stage-context --output artifacts/incident-ml-stage-history --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation residual --stage-context --output artifacts/incident-ml-stage-residual --documents C:/path/to/incident-documents
```

This adds fit-only one-hot session stage, known before a run, not a final result
or rider identity. Unseen stages get zero context. The timing-only frozen
reference remains unchanged. Treat this primarily as a **bias diagnostic**:
305/341 reference positives are finals, while Quali has 20/8,526 labelled runs
and Q2 has none among 1,706 runs. Stage-aware observed-label recall can improve
by learning reporting propensity rather than better physical incident detection.
It cannot establish that suppressed qualifying alarms are false. No stage
whitelist, test quota, production change or automatic promotion is introduced.

## Nonnegative PU, nested stacking and checkpoint dynamics

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/nnpu_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/stacking_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation dynamics --output artifacts/incident-ml-checkpoints --documents C:/path/to/incident-documents
```

`nnpu_search.py` tests nonnegative PU logistic risk with exact finite-difference-
tested gradients, a positive curvature preconditioner and bounded boosting
updates. The U expectation uses the complete fit population, not a declared
clean-control class. Priors 2/5/10/20% and leaves 7/15 are calibration parameters,
not identified incident prevalence. The observed positives are not known to be
a representative positive sample, so theoretical PU consistency is not claimed.
There are no session-stage predictors. Outputs: `artifacts/incident-ml-nnpu/`.

`stacking_search.py` builds three round-group OOF folds inside the outer fit
partition. Each meta-training row is predicted exactly once by base estimators
that did not see its event. Base models never use outer calibration/test labels
or history. The final bases train on the complete outer fit partition. Meta
LightGBM/logistic choices and thresholds use outer calibration only. A separate
stage-aware stack is a reporting-bias diagnostic, not equivalent evidence of
physical incident detection. Outputs: `artifacts/incident-ml-stacking/`.

`checkpoint_dynamics.py` measures intermediate checkpoint position/gap movement
in matched complete cohorts. Missing checkpoints do not invent later cumulative
times. Cumulative final position, final time and final gap are excluded, including
padding after four-sector layouts. Final-sector rank is a sector measurement,
not final overall position. Outputs: `artifacts/incident-ml-checkpoints/`.

A purposeful 60-row blinded adjudication sample is saved as
`artifacts/incident-ml-multiple-instance/blind-review.csv`; no verdict is filled
automatically. It mixes known hits/misses and unlabelled alarms without exposing
those sample classes in the review file. It is not representative and cannot
estimate precision. Sporting observations only; do not add medical narratives.

## Within-round paired profiles and fixed-detector hybrids

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation paired --output artifacts/incident-ml-paired --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation paired --paired-enhanced --output artifacts/incident-ml-paired-enhanced --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation paired --paired-placebo --output artifacts/incident-ml-paired-placebo --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/hybrid_search.py --calibration grouped --output artifacts/incident-ml-hybrid-grouped --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/hybrid_search.py --calibration latest --output artifacts/incident-ml-hybrid-latest --documents C:/path/to/incident-documents
```

`paired_sessions.py` aligns the same rider across strictly earlier dates in the
same round/course/category and layout. Common peer riders, excluding the target,
adjust per-sector session speed changes. Other sectors adjust the rider's global
pace change; the candidate measurement cannot change its own expected value.
Enhanced features add difference-in-differences peer residual noise and sector-rank
changes. Same-day order is never guessed. Availability-only is a selection-bias
control, not an incident detector. The placebo deliberately substitutes another
prior rider profile while retaining availability and target missingness. It is
an ablation using real measurements, not a fabricated benchmark incident.

`hybrid_search.py` adds frozen C# detector predictions/scores, never manual labels,
and tests extra weight on already suspicious unlabelled controls. Total P/U weight
stays balanced. These controls are not verified clean, and the fixed C# rules were
developed on previously inspected years. A flags-only control is included. The
production detector and calculator implementations remain unchanged.

## Existing speed-trap and round-condition context

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation environment --output artifacts/incident-ml-environment-latest --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/objectives.py --representation environment --calibration grouped --output artifacts/incident-ml-environment-grouped --documents C:/path/to/incident-documents
```

`environment.py` adds measured speed normalized within the session, speed-vs-sector-
tempo residuals, missingness and dynamic round-condition encoding. A speed-unit
conversion cannot change normalized features. Missing speed is not measured zero.
Conditions are round annotations, not verified weather at every session. Fit-only
encoding ignores unseen condition names. Speed-only and availability/weather-only
controls diagnose confounding. No session label, rider identity or final place
enters predictors. All values come from the existing frozen snapshots.

## Wider combined-feature search

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/wide_search.py --documents C:/path/to/incident-documents
```

The seeded search declares 48 distinct configurations before fitting. It screens
100-round models on all fit positives plus up to 6,000 uniformly sampled fit U
rows, then refits the four best calibration configurations on all fit rows for
100/300/600 rounds. It varies leaf size/count, regularization, learning rate,
row/column sampling, observed-positive weight and suspicious-control emphasis.
Selection uses mean calibration recall at three predeclared low-alarm targets;
outer tests never choose parameters, length or thresholds. The larger search can
overfit a small calibration set, so its held-season results remain exploratory.

Combined measurements include timing, enhanced paired profiles, speed, round
conditions and frozen C# predictions. No explicit session stage, rider/event ID
or final classification is used. Absence of a stage column does not establish
absence of reporting/availability bias. Full screen/refinement choices, final
calibration scores and test scores stay in `artifacts/incident-ml-wide/`.

## Normal-tempo covariance versus sparse delays

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/sparse_delay.py --documents C:/path/to/incident-documents
```

`sparse_delay.py` fits robust covariance of field-normalized log-sector profiles,
removing global pace and optionally a smooth linear trend. Covariance is estimated
only on the fit mixture, with 70%/90% trimming and a regularization floor. Trimming
does not turn unknown rows into verified clean examples. Missing measurements use
the marginal covariance; one measurement with a free pace intercept provides no
identifiable local delay, not an assumed clean result.

The alternate model adds a nonnegative delay to one sector or a contiguous episode
of up to three sectors. Gaussian and Student-t residual improvements are compared
directly and as learned features (alone or alongside the original timing features).
All configuration and threshold selection uses grouped calibration, not test years
or test-top-700 quotas. This is a different model family from the tree searches,
but remains exploratory on already inspected years. The scores do not establish
physical crashes, probabilities or counterfactual corrected finish times. Numerical
tests cover uniform pace shifts, smooth trends, partial data and hand-solved delays.
Outputs stay in `artifacts/incident-ml-sparse-delay/`; production remains unchanged.

## Within-field ranking and event-balanced learning

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/field_objectives.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/field_objectives.py --paired-enhanced --output artifacts/incident-ml-field-objectives-enhanced --documents C:/path/to/incident-documents
```

This experiment changes the learning objective rather than sector features. Native
LambdaRank and XE-NDCG queries group rows by session or round/category; categories
never mix within a query, and query IDs are not predictors. Uniform-label queries
provide no unequal-label pairs for LambdaRank even though their rows remain present.
An explicit native-library characterization verifies that XE-NDCG nevertheless
produces nonzero scores on all-zero-label queries through tie randomization. Their
counts are exported to expose this reporting-selection limitation. A separate pointwise
model anchors calibration-CDF blends (.25/.5/.75 rank weight), including fields
with no reported incidents. Unknown controls are not verified clean examples.

Event-balanced classification additionally equalizes round/category mass within
each observed class. Timing-only and earlier-session paired/weather views retain
the same held-season tests and grouped calibration. An ordinary paired/weather
classifier provides the within-study reference. Models, blend weights and thresholds
are selected on calibration only; no test quota. This research remains exploratory
on previously inspected years, and a ranking gain cannot establish crash precision.
Outputs stay in `artifacts/incident-ml-field-objectives/`; production is unchanged.
The enhanced run adds paired sector-change/rank features used by the strongest
earlier paired/weather recipe. Its separate output preserves the plain-profile
study as an ablation; timing-only results should remain unchanged.

## Full run-level sector alignment and small neural models

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/aligned_search.py --documents C:/path/to/incident-documents
```

`aligned_runs.py` preserves complete 17-feature sector rows, either in physical
order or sorted by log excess with deterministic ties. Time/rank/recovery features
move together. Missing padding is explicit and never a measured zero. Unlike the
earlier top-two summary or latent-sector MIL, the run-level learner sees all five
sector combinations. Original aggregate and enhanced paired/weather context remain
in all three views (aggregate control, ordered, loss-sorted).

Each view compares the existing tree recipe (leaves 7/15) with small weighted
scikit-learn MLPs (16 tanh or 32 ReLU units, alpha=10, at most 150 Adam epochs).
Signed-log compression and clipping are deterministic; robust scaling is fitted
only on fit rows.
No random validation split is used: stopping depends on training loss, and outer
calibration still selects configurations/thresholds. Iterations and warnings are
retained; reaching the iteration limit is not called convergence. BLAS is limited
to two CPU threads; no dependencies were added. Scores learn observed-positive
versus unknown labels, not verified crash probabilities. Same held-year/grouped
protocol, fixed budgets, bias caveats and production-source protection apply.
Outputs, including the training-only resource pilot, stay in
`artifacts/incident-ml-aligned/`.

## Isolation and local-density novelty without labels in fitting

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/unsupervised_search.py --documents C:/path/to/incident-documents
```

Isolation Forest (300 trees, max-samples 256/1024) and novelty LOF (20/50 neighbors)
fit the full contaminated fit mixture, including observed positives, without their
labels. Robust scaling is fit-only. Native contamination thresholds are not used;
the five existing calibration budgets and strict ties remain unchanged. LOF scores
only disjoint calibration/test rows, not its own fit samples. BLAS/OpenMP uses two
threads and pairwise working memory is 64 MiB. Captured warnings remain in tuning.

Relative timing and one-sided aligned positive loss/rank/recovery profiles compare
general rarity with directed slow-sector anomalies. A quality-only view is an
explicit missingness/checkpoint bias control, not a proposed incident detector.
Parameter selection still uses known-positive calibration recall: estimator fitting
is unsupervised, the full selection protocol is not label-free. Unusual is not the
same as an incident, and unlabelled training examples are not verified clean.
Same held-season/forward protocol and production protections apply. Outputs stay
in `artifacts/incident-ml-unsupervised/`.

## Reviewed annotations and additional controls

`human_review.py` reads the completed review XLSX without modifying it. All 60
case identities and displayed measurements must match the original blind CSV.
Current responses: 46 yes, 14 no, 39 sector annotations. Outcome-only DNF/winner
rationales are flagged, not independent evidence of a crash or clean run.
These are human adjudications, not program-verified video labels.

`review_supervision.py` joins reviews to frozen run keys. Six reviewed negatives
conflict with original positives: quarantine them from fitting, retain the original
341-positive benchmark. Strict additional supervision supplies 21 positive and
eight negative reviews. Only fit-row annotations may alter fitting targets or
weights; calibration/test targets remain original. This selected sample cannot
establish population precision or false-positive rate.

`reviewed_search.py` compares quarantine-only, reviewed-negative weighting and
additional positive supervision using unchanged paired/weather features.
Calibration alone selects leaves/weights and thresholds. Notes, identities,
review membership and sector annotations are not predictors. Explicit sectors
are saved for localization, never assigned to every sector of a positive run.
Artifacts and exact per-fold review usage: `artifacts/incident-ml-reviewed/`.

`reviewed_sector_search.py` evaluates local sector learning on the same eight
grouped season/forward splits. Original-MIL is the original-label control;
reviewed-runs adds strict reviewed run labels and weighted reviewed negatives;
reviewed-sectors differs only by adding explicit positive sector anchors.
Non-annotated sibling sectors in a positive run are never assumed clean.
All annotations are fit-only, while the original benchmark remains unchanged.
The same 33 local features plus aggregate/paired/weather context are used in all
three modes. Calibration selects leaves 7/15 and a separate 25/50/75% CDF blend
with the existing round/category ranker. No test ranking quota is used.
Exact fit keys, anchors, model files, per-sector scores and separate original/human
localization counts stay in `artifacts/incident-ml-reviewed-sectors/`.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/reviewed_sector_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-reviewed-sectors --documents C:/path/to/incident-documents
```

`local_density_search.py` tests same-session transductive density context and
availability controls; missing density stays unknown. `nested_selection.py` tests
three-fold inner event-group selection inside outer-fit data, leaving outer
calibration for thresholds only. Separate artifacts retain failed candidates.
`verify_saved_study.py` independently recounts saved predictions, calibration
thresholds, split keys and pooled counts, and verifies frozen/protected hashes.

## Same-session factor decomposition

`field_factor_search.py` compares a session-local rank-1/2 tempo model with sparse
positive residuals against the existing paired/weather classifier. It alternates
20 SVD projections and positive soft-threshold updates at penalties 1.5/3 robust
residual scales. Fixed field-column offsets are removed first. This is unlabelled
retrospective/transductive field context, not prior-only forecasting or a verified
counterfactual corrected time. Current-field observations can affect their own
fitted reference; separate sessions/categories never share this local fit.

Missing observations use model completion only internally. Exported local scores
remain unknown; fields need eight usable rows and three measured columns, and a
rider needs two measurements. Direct thresholds retain the complete calibration
population in their denominator. Factor-only and factor/context learned variants
encode missingness explicitly. Context-reference and availability-only are controls.
No unlabelled run becomes a verified clean label. Same eight grouped season/forward
tests and five budgets; calibration alone selects configurations and thresholds.
Artifacts: `artifacts/incident-ml-field-factor/`.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/field_factor_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-field-factor --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/repeat_field_factor.py --documents C:/path/to/incident-documents
```

`factor_blend.py` tests complementarity of the frozen factor/context and within-
round/category rank scores, without refitting their models. Only calibration CDFs
are used for scaling. Three weighted means, minimum and maximum compete on
calibration recall; the selected recipe is checked on all five existing budgets.
The full 20,340/341 population and eight season/forward splits remain unchanged.
Saved component hashes, exact key checks, score replay and separate disagreement
counts distinguish combination gains from altered source scores or test quotas.
Artifacts: `artifacts/incident-ml-factor-blend/`; reproduce with the usual
`--documents` argument and recount with `verify_saved_study.py`.

## Post-round sibling-run context

`sibling_search.py` deliberately tests a broader retrospective measurement scope:
other scored sessions of the same rider, round, course, category and layout,
including later or unordered sessions. It does not infer same-day order or claim
causal/live performance. Whole rounds remain together in the eight season/forward
splits; incident labels never enter reference construction. Scored-source-only
coverage is reported, not silently expanded with new downloads.

Median field-normalized profiles retain both raw multi-sector changes and centered
local changes. Self/same-session references are excluded. A different-rider placebo
matches the current session, course/category and exact support/missingness pattern;
unswappable references stay unchanged and their count is exported. Support-only
and unchanged factor/context controls isolate availability and identity effects.
Leaves 7/15 and thresholds use calibration only. Production and the original
20,340/341 benchmark stay unchanged. Artifacts: `artifacts/incident-ml-siblings/`.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/sibling_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-siblings --documents C:/path/to/incident-documents
```

## Derived finish-checkpoint changes

`finish_search.py` isolates the earlier checkpoint model's deliberate exclusion of
finish movement. It compares identical intermediate-only and finish-enabled change
slots using scored-run cohorts and checkpoint-quality masks. Only rank/gap changes,
their robust deviations and support are exported: absolute before/after rank and
absolute finish time/gap are omitted. Official finish rank/time/status fields are
not read. Nevertheless this is finish-related physical information, available only
after finalized session measurements, and the report tags that distinction.

The same completing cohort supplies before/after order; missing prefixes cannot
fabricate cumulative movement. Four-sector finishes and missing padding are tested.
Controls are intermediate/context, unchanged factor/context and support-only;
the standalone movement learner is an ablation. Existing eight grouped season/
forward splits, five budgets and calibration-only leaves 7/15 remain unchanged.
Artifacts: `artifacts/incident-ml-finish-change/`. Run `finish_search.py --documents`
and then `verify_saved_study.py` with that folder; no production code changes.

## Causal raw-history updates within a held season

`rolling_search.py` preserves the existing history formula but varies its source
scope. Fitting rows use fit measurements only. Calibration may add earlier
calibration measurements, never test rows. Test inference may use earlier
calibration/test measurements. Strictly earlier calendar dates, same rider/category
and different rounds are mandatory; no same-day stage/ID ordering is invented.
Held labels never enter state updates or learner fitting. Test covariates are a
causal stream, not independently sampled static rows; this distinction is tagged.
Causal refers to history-input availability; leave-year-out may still train on
later seasons, while the separate forward checks train on earlier years only.
Forward scopes intentionally omit later, unused years. Disjoint role validation
permits these omissions but rejects overlap and out-of-range indices. An initial
complete-partition guard failure and its partial results are preserved separately.

Frozen/rolling modes share identical fit matrices and per-candidate fitted weights.
The unchanged formula takes per-run 35% log-relative pace and the median/MAD of
the last eight eligible prior runs. Checkpoint-quality-invalid inputs are omitted.
Diagnostic unavailable losses use the legacy zero feature encoding, not zero
measured times. Context views retain the original timing-quality flags; the
standalone history ablation retains the compact legacy representation.

A different-rider, same-session/category/history-count placebo recomputes target
losses from swapped historical center/dispersion. Support-only and unchanged
factor/context controls isolate coverage and source drift. Scopes, all arrays and
swap counts are retained per split. Existing eight season/forward splits, five
budgets, calibration-only leaves and the 20,340/341 benchmark stay unchanged.
Artifacts: `artifacts/incident-ml-rolling-history/`.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/rolling_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-rolling-history --documents C:/path/to/incident-documents
```

## Joint causal time/rank history

`rank_history_search.py` adds prior sector-percentile history to the unchanged causal
time/context learner. Percentiles are rebuilt from raw measured sector rank/count,
not the generic midpoint fallback: invalid/missing ranks remain unknown. Current
deficits, MAD-standardized deficits (floor .05), explicit missing flags and prior
statistics supplement the same per-run 35% / last-eight-history policy. Fit, cal
and test input scopes remain unchanged; no official finish fields or labels enter
rank construction. Unassigned future years remain outside forward source scopes.

Time-only is the exact reference; rank/context is an ablation. The placebo swaps
rank reference only within current session/category/history count, keeping time
history fixed. Past-ability-only and support-only are reporting/availability-bias
controls, not proposed detectors. Source hashes cover cached time histories; role,
date, round/category and reference parity are audited independently. Same eight
splits, five budgets and calibration-only leaves 7/15; no test quota. Artifacts:
`artifacts/incident-ml-rank-history/`. Run `rank_history_search.py --documents`, then
`verify_saved_study.py` on that folder. No production changes or new dependencies.

## Nonnegative observed-pattern density ratio

`density_ratio_search.py` changes the learner rather than the input feature set.
A nonnegative RBF expansion minimizes `.5*mean_U(r^2)-mean_observedP(r)+ridge*||a||^2/2`.
U is the contaminated unlabelled population, not a clean class. Cholesky converts
the regularized quadratic exactly to NNLS; every fit enforces a 1e-7 KKT residual
check. This estimates an approximate observed-pattern ratio, not crash probability,
incidence prevalence or a reporting-bias-corrected posterior.

Signed-log, RobustScaler, clipping and optional PCA(12) fit only fitting rows.
Up to 64 centres are fit positives; bandwidth uses at most 512 fit-mixture rows.
Calibration alone selects multiplier .5/1/2 and ridge .01/.1. Full/PCA causal-history
views share the strongest time/context inputs; support-only and frozen time scores
are controls. Solver unit tests cover a literal constrained optimum, input-only
preprocessing and tied degenerate profiles. No new dependency or GPU is needed.
The verifier refits all selected models, checks landmarks/source roles, replays
saved scores at 1e-12 and requires retrain difference <=1e-10 with no changed
decisions. Existing eight tests/five budgets and the 20,340/341 benchmark remain.
Artifacts: `artifacts/incident-ml-density-ratio/`.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/density_ratio_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-density-ratio --documents C:/path/to/incident-documents
```

## Exact-path positive-delay sector sequence

`sector_sequence_search.py` fits a two-state, five-sector weak-bag sequence model.
Normal residuals are Gaussian; the delayed state adds a positive exponential delay
in seconds. Exact summation over all 32 paths retains isolated and persistent
patterns. EM learns emissions and transitions from fit rows only. Positive bags
require at least one delayed measured sector; unlabelled bags allow all paths,
not a forced clean state. Original one-based explicit sector labels anchor only
their fit sectors. Missing measurements are marginalized, not filled with zero;
all-missing scores remain unknown and stay in benchmark denominators.

The history view reuses the previously audited role/date/category-safe history
scopes. A within-run reference and independent-state ablation check dependence
on history and sector ordering. Calibration chooses natural/equal-total-mass
observed-P/U weights, never test alarms. Sequence likelihood log-odds are model
scores, not calibrated crash probabilities. Reporting bias remains unidentified.
Unit checks include independent numerical density convolution, analytical
gradient finite differences, weak bag/sector constraints and missing observations.
The verifier independently reconstructs reference clocks, replays saved scores,
checks fit-only anchors, refits all selected models and requires <=1e-10 score
difference with zero changed decisions. No new dependency or production change.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/sector_sequence_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-sector-sequence --documents C:/path/to/incident-documents
```

Artifacts stay in `artifacts/incident-ml-sector-sequence/`. An initial resource
pilot used an unverified label-index version and is retained separately as
`resource-pilot-unverified-anchor-index.json`; it is not benchmark evidence.

## Field-dispersion sequence ablation

`field_noise_search.py` tests a concrete failure hypothesis of the common-noise
sequence model: moderate losses on tight sectors may be absorbed by a normal SD
learned across very different sectors. It reuses the archived solver unchanged.
For each observed sector, `s=max(.005, fieldLogMAD)/.04`, `r_new=r/s`,
`referenceClock_new=referenceClock*s`. This preserves diagnostic loss seconds
while making normal noise heteroscedastic. Fewer than eight field samples or
missing/nonpositive dispersion uses neutral scale 1. A separate ablation adds
supported prior-history MAD in quadrature; another omits the prior tempo centre.
Natural versus balanced bag weights remain calibration-only choices. No new
learner grid, test quota, production change or dependency is introduced.

The verifier independently reconstructs scaling, checks preserved seconds and
missing masks, refits all 24 selected models, and exactly compares cached tree
and common-noise sequence controls. Unknown U is still not clean; scores are
not physical crash probabilities. All years remain exploratory.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/field_noise_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-field-noise --documents C:/path/to/incident-documents
```

## Upper-tail observed-label ranking

`tail_ranking_search.py` changes the learned objective, not the strongest causal
input representation. `tail_ranking.py` implements pairwise logistic ranking of
every observed P against the highest-scoring `ceil(700/20340*n_U)` fitting U rows.
Boundary ties share fractional weight, so identity/row order cannot select them.
The fit-only tail updates every boosting step. A full-pair control includes all U;
the earlier cross-entropy scores are retained unchanged as another control.

This is observed-label discrimination, not a true incident-risk estimator: hard
U can include unreported incidents, reporting bias remains, and no U is verified
clean. Fit-only tail size is not a held-test alarm quota. Thresholds and leaves
7/15 use calibration only. Same 300 trees, learning rate .025, min leaf 35, L2=5
and fixed CPU seed. Tests check analytical gradients/diagonal curvature, symmetric
ties, permutation/translation invariance and actual native-tree train/reload.
The verifier refits all selected models and checks score replay/decisions and
fit-only objective invariants. All years remain exploratory, not a new external
benchmark. Production and dependencies remain unchanged.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/tail_ranking_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-tail-ranking --documents C:/path/to/incident-documents
```

The actual first tail-ranking sweep was stopped on a negative target-impossibility
bound: completed held-year misses already prevented 307/341 hits even if every
remaining original positive were detected. Completed native models/outputs were
moved recoverably to `artifacts/incident-ml-tail-ranking-pruned/`. This partial
trial is NOT a completed six-year/chronological study and is excluded from pooled
scoreboards. `verify_pruned_tail.py --documents ...` replays its completed native
models and calibration thresholds, exports partial scores and reconstructs the
optimistic bound for every predeclared budget. It does not retrain the partial
models or claim unrun forward tests. The stopping decision adjusts no model or
threshold; it establishes failure of this candidate, not success of another.

## Coordinate-monotone loss trees

`monotone_search.py` tests a structural hypothesis motivated by large timing
anomalies among missed original positives. It keeps the strongest causal tree
inputs and settings, but constrains nondecreasing scores in aggregate maximum
log loss, seconds loss, relative loss and robust deviation. A second variant adds
max sector rank drop. All remaining inputs are unconstrained. This is a partial
coordinate guarantee **with other inputs fixed**, not a guarantee along a real
run where multiple derived features change simultaneously.

Basic native LightGBM constraints, same 300 trees, .025 learning rate, min leaf 35,
L2=5, CPU seed and observed-label weights; leaves 7/15 remain calibration-only.
The cached unconstrained tree is the control. Tests fit actual native models and
check nontrivial monotonic predictions. Saved held-row probes vary only one
coordinate over fit quantiles; they are property tests, not new measured runs.
The verifier rebuilds the exact probes, checks constraints and recipe selection,
refits all 16 selected models and compares saved scores/decisions and cached
controls. No test quota, new dependency or production change. U is still not
verified clean and predictions are not calibrated crash probabilities.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/monotone_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-monotone --documents C:/path/to/incident-documents
```

## Human reviews on strongest causal inputs

`reviewed_history_search.py` applies the same audited 60 workbook decisions to the
strongest causal-history/factor view, rather than the weaker paired/weather view
used by the initial review study. Existing matching, label rules, class-mass
weights and learner are reused unchanged. Six conflicting original positives are
excluded from supervised fit only; held labels remain the original 341-positive
benchmark. Strict additional positives and reviewed negatives can supervise only
fit rows, never calibration/test rows of that fold. Outcome-only notes remain
ineligible. Unknown U is not verified clean.

Quarantine-only is a control; negative-only upweights verified review negatives
10/50, and reviewed-labels additionally uses strict positive reviews. Leaves 7/15
and weights are calibration-selected. All label-free sporting-history inputs and
fit weather vocabulary remain identical to the reference, including measurements
whose conflicting labels were quarantined. No review status/note/membership or
identity becomes a predictor. The read-only workbook hash is checked before and
after fitting. The verifier audits exact review-use keys, refits all 24 selected
models, checks balanced class mass, unchanged held targets, numerical replay and
the cached reference. Previously inspected years and purposeful review selection
remain limitations; no precision/FPR or crash-probability claim is justified.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/reviewed_history_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-reviewed-history --documents C:/path/to/incident-documents
```

`held_review_audit.py --documents ...` separately evaluates added strict positive,
strict negative and disputed-original-positive reviews in their held year only.
No original primary label or denominator changes. Auxiliary scores are not used
for recipe/threshold selection and cannot estimate population precision/FPR.
The UTF-8 annotation reader is explicit, avoiding Windows cp1250 name corruption.
`held-review-audit.json` protects hashes of all primary study files and records
the audit source hash; `held-review-outcomes.csv` retains per-fold counts.
The label-quarantine control remains a valid full prediction pipeline and may
appear in the descriptive best-point scoreboard, explicitly marked as a control,
without claiming a new scoring algorithm or statistically established gain.

## Fit-positive profile specialists

`specialist_search.py` checks whether heterogeneous observed-positive patterns
benefit from separate discriminators. KMeans fits up to three groups of FIT P
with at least two quality-valid sectors: five ordered log-relative times centred
within the run plus five explicit missing masks. RobustScaler fits those P only;
grouping values are clipped +/-4, not the prediction inputs. Groups carry no
asserted causal names. A missing-mask-only grouping is the availability control.
Low-support positives are shared across experts, not discarded or given invented
timings. Each expert sees its P group plus all eligible U; other known P are
excluded, never labelled negative. Human-No/original-P conflicts are quarantined
from fit only, matching the cached 235/653 reference pipeline.

Each expert retains the strongest causal input view and original learner settings.
Leaves 7/15 are calibration-selected jointly. Native scores use calibration-only
empirical midrank CDFs and fixed maximum pooling, not held-test CDFs or quotas.
Scores are not crash probabilities. The verifier independently rebuilds profiles
from measured ratios, audits exact fit-positive groups/U/shared rows, repeats
fit-only preprocessing/clustering, refits all selected components, checks pooled
scores/decisions and frozen controls. Tests include held-data/label isolation and
explicit low-support handling. Previously inspected years/reporting bias remain.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/specialist_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-specialists --documents C:/path/to/incident-documents
```

## Matched-component specialist routing

`gated_specialist_search.py` changes only the aggregation of the preceding saved
specialists. A query with two valid sectors uses the nearest frozen fit-positive
centroid (the same scaler and clipping); its selected expert supplies the score.
Low-support queries retain max pooling. No new model, cluster, leaf selection or
supervision is fitted. Inherited leaves were selected for max pooling, not retuned
to the gate. Shape/max and availability/max controls use exactly the same native
components and calibration CDFs. No test labels, test CDF or held alarm quota.

Tests exercise actual fitted centroids, wrong-expert/max selection, low support,
single-cluster routing and immutable input ranks. The verifier checks parent
model provenance, replays all frozen scores and independently reconstructs nearest
centroid distances without using sklearn prediction for that check. Numerical
near-ties are reported, not silently turned into different groups. This is a
matched-component ablation, not new evidence that clusters are causal mechanisms.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/gated_specialist_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-gated-specialists --documents C:/path/to/incident-documents
```

## Structural source-label association check

The existing independent matcher can be run without fitting or changing labels:

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-multiseason.py --input artifacts/incident-multiseason --documents C:/path/to/incident-documents --output artifacts/incident-label-rematch --match-only
```

The report compares its 2021–2025 run assignments to the frozen C# mapping and
checks generic qualifying labels against scored phase alternatives in all frozen
years. This is metadata association only, not independent confirmation that a
reported incident happened. It never changes the original 341-positive benchmark
or resolves disagreements with human reviews. Source/mapping hashes are recorded.

## Fixed-learner label quantity/event-diversity diagnostic

`label_efficiency_search.py` keeps the strongest 235/653 full-label reference,
same features/U population and inherited full-label-selected leaves. Nested
25/50/75% positive-fit subsets are sampled by run or whole positive-bearing
event with two fixed seeds. Hidden known P is excluded from supervised fitting,
never relabelled U/clean. Timing measurements/history remain available without
their labels. All original 341 held-positive targets and calibration data remain.
Thus fractions measure fit P labels, not total annotation cost. Removing hidden P
instead of leaving it in U is an optimistic controlled ablation, not a realistic
missing-label process. No seed/fraction is selected as a new detector.

Training P recall uses the same calibration threshold as held recall. Exact
retained P/event/U counts are exported; training rows across year folds overlap
and are not treated as independent new cases. The verifier independently rebuilds
104 fit samples, checks held exclusion/fixed U, refits 96 reduced-label models,
replays eight full references and verifies training/held score diagnostics.
Two seeds are sensitivity checks, not confidence intervals or an extrapolation
guarantee. These diagnostic curves are excluded from detector scoreboards.
Counts of model fits are operations, not independent replications: in this frozen
corpus leave-2026-out and chronological-2026 share the same outer split. Held-year
and chronological tables remain separate; pooled coverage counts only six
leave-year tests, once per original run.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/label_efficiency_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-label-efficiency --documents C:/path/to/incident-documents
```

## Positive-event robust-risk experiment

`event_robust_search.py` tests equal positive-event loss (strength 0) against
soft worst-positive-event loss (strength 1/5, calibration selected). Unknown-run
loss stays uniform and contaminated, not a verified-clean class. Event IDs group
fit losses only; they are not predictor inputs. The strongest causal context,
fit-only disputed-label quarantine and previously selected leaves are fixed.
All original held-positive targets remain unchanged. Native LightGBM uses exact
gradients/diagonal curvature with a training-only 1e-8 curvature floor. Returned
raw margins are not crash probabilities. Finite-difference, stable-limit,
group-weight and native reload unit tests cover the objective. The study verifier
rebuilds roles/context and refits all 16 selected models; the frozen quarantine
reference is replayed separately. This remains exploratory reuse of inspected
seasons, not an independent final validation or a guarantee of event invariance.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/event_robust_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-event-robust --documents C:/path/to/incident-documents
```

## Frozen positive-label subbagging

`subbagging_search.py` reuses all twelve reduced-label components from the verified
label-efficiency diagnostic. Fixed six-run, six-event and twelve-component means
test variance reduction, not selection of a favourable seed/fraction. Calibration
midrank normalization is compared with an all-component raw-score mean. This is
exploratory because earlier component results were already inspected. No model is
trained, hidden P is not reclassified clean, and held targets remain unchanged.
The verifier checks parent component/refit-proof hashes and independently repeats
the fixed membership, calibration-only CDF arithmetic and reference-score replay.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/subbagging_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-subbagging --documents C:/path/to/incident-documents
```

## Within-field score location/scale

`field_score_search.py` keeps the strongest model frozen and adjusts its logits
using OTHER scored runs in the same completed session/category. At least eight
runs are required; reference size n gives shrinkage n/(n+20). Global median/MAD
come from calibration only. Studentization (optional) clips the global/local MAD
ratio to 0.5–2, with a 0.1 floor. Strength 0.5/1 is calibration-selected. This is
explicitly field-transductive, not an online predictor, and never sets a local
percentile alarm threshold or quota. A frozen no-adjustment reference remains.
The independent verifier repeats leave-one-out arithmetic, role/field separation
and calibration choices. No new models, physical crash-probability claims or
changes to held labels/production code.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/field_score_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-field-score --documents C:/path/to/incident-documents
```

## Continuous additive observed-label risk

`additive_search.py` replaces tree interactions with a sum of continuous univariate
piecewise-linear terms. Signed-log transformation precedes fit-only median and
max(10–90% quantile range, standard deviation, 1e-6) scaling. The standard-deviation
floor prevents rare nonzero columns with zero central quantile width from being
amplified by 1e6; the failed initial scaling pilot is retained explicitly, without
invented unsaved metrics. Unique interior 25/50/75% fit quantiles form
hinge knots; binary columns stay linear and fit constants are omitted. Outside
the terminal knots each contribution extends linearly in signed-log space. A
linear strong-context ablation and local-only additive model distinguish the
basis effect from context. All use balanced observed-P/contaminated-U logistic
loss, no claims of clean labels or crash probabilities. C=0.001/0.01/0.1 is selected
on calibration recall among converged fits; warnings/iterations for every trial
are retained. The strongest tree/quarantine reference stays frozen. No held data
fit the basis and no original held labels change. The verifier independently
checks each basis column and refits the 24 selected models, including saved-model
reload, strict threshold/decision replay and 61,644 frozen reference scores.
This remains exploratory reuse of previously inspected seasons.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/additive_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-additive --documents C:/path/to/incident-documents
```

## Bounded contaminated-U loss

`bounded_search.py` tests generalized cross entropy `(1-c^q)/q` on observed U,
where c denotes probability of that observed label. q=0 is log loss. The asymmetric
variant keeps P log loss intact while reducing high-loss U influence without
reclassifying those runs as verified positive/clean. Symmetric bounding and a
matched custom-objective q=0 model are explicit controls. q=0.3/0.7 is selected on
calibration only; inherited leaves, strongest causal inputs and fit-only quarantine
stay fixed. Gradients are exact. True curvature can be negative, so boosting uses
the positive surrogate `w*e*c^(q+1)` with a 1e-8 floor; true gradient/curvature are
independently finite-difference tested. This is robust observed-label risk, NOT an
identified/unbiased PU estimator or a calibrated crash posterior. No held labels
change, and no precision claim is made. The verifier refits/reloads 24 selected
native models and checks controls, thresholds and 61,644 reference scores.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/bounded_search.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/verify_saved_study.py artifacts/incident-ml-bounded --documents C:/path/to/incident-documents
```

## Native C# comparison and deployment-readiness benchmark

`deployment_benchmark.py` applies the best frozen quarantine/history models and
measures real offline feature reconstruction separately from prepared scoring.
`complete_benchmark_inputs.py` rebuilds a full workload from identical exported
context columns plus the exact chronological history scope. `native-parity/`
executes the exported native binary trees without ONNX or new package dependencies.
It verifies all 61,644 calibration/test scores/decisions and compares detector CPU
cost against a separately built committed production API assembly. It does NOT
activate a model in the API or substitute heuristic time corrections.

`production_http_benchmark.py` makes three ordinary public GETs per existing
ranking/overview route, without cache purge/restart/admin operations. Its network
measurements are not a new-model A/B or a DB-time breakdown. `deployment_report.py`
renders accuracy, forward tests, CPU/input/HTTP timings and remaining integration
requirements into the existing research report. The original model is run-level:
sector localization and removable-time corrections still require independent
validation, and the 343-input constructor still needs a production port/cache.
The draft `season_variance.py` has math/unit checks only; no trained study or
deployment result is claimed for it.

```powershell
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/deployment_benchmark.py --documents C:/path/to/incident-documents
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/complete_benchmark_inputs.py
dotnet run --project tools/incident-ml-research/native-parity/NativeParity.csproj -c Release -- artifacts/incident-deployment /path/to/separately-built-production/racetrace-api.dll
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/production_http_benchmark.py
artifacts/incident-ml-venv/Scripts/python.exe tools/incident-ml-research/deployment_report.py
```
