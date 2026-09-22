# Historical podium predictor experiments

Offline-first, read-only comparison of six fixed models using the existing API
Pace/Consistency/Rating calculators and sector incident detector. This executable
does **not** change the production predictor, database, API contracts or frontend.

## Run

From the API repository root, with the existing local Supabase configuration:

```powershell
dotnet run --project tools/UCI_Stats_API.PodiumBacktest -- --api UCI_Stats_API --output artifacts/podium-backtest --iterations 50000
```

The first run retrieves rounds, sessions and the read-only `get_ranking_data` RPC,
sequentially, once per round. Raw JSON responses are cached under the ignored
`artifacts/` directory. Credentials are read from the ignored
`UCI_Stats_API/appsettings.Development.json`; authorization headers are not saved.
No service-role credential is needed. Mutation requests are explicitly blocked.

Repeat the exact snapshot without accessing the database:

```powershell
dotnet run --project tools/UCI_Stats_API.PodiumBacktest -- --api UCI_Stats_API --output artifacts/podium-backtest --iterations 50000 --offline
```

Use a **new output directory** for a fresh database snapshot. `--download-only`
downloads/caches data without running the models. A missing required development
sample or 2024 round-4 final fails the run instead of reporting success with zeros.

Outputs:

- `report.md`: aggregate scores and Val di Sole 2024 round-4 predictions.
- `results.json`: per-event results, top ten and complete evaluation population.
- `cache/*.json`: local snapshot, not intended for Git.

## Experiment protocol

- Available history begins in 2021; only data **before the target round starts**
  is available to each prediction. Both round dates and session dates are checked.
  The whole target weekend is excluded, including qualifying and semifinals.
- World Cup rounds 2 onward in 2022–2023 are the development set; 2024 is holdout.
  Historical World Championships are included in prior evidence, not test targets.
- Elite Men and Elite Women are separate fields. The same historical top-30 cohort
  (current-season confidence >=35, ordered by rating) is used for all variants.
  The actual final start list does **not** filter predictions. Missing actual podium
  riders are added to evaluation with probability zero; non-attendees remain misses.
- Model selection uses development recall@3, then Brier as tie-break. Do not choose
  again using the 2024 results. Fixed coefficients are experimental assumptions,
  not fitted/validated production parameters.
- Data are a present-day snapshot of historical races, not an archived as-of feed.
  Subsequent result corrections and the current metric formulas are therefore used.

## Models

| Model | Description |
|---|---|
| production-equations | Existing feature builder, calibration and Monte Carlo simulator; current/prior-season history and latest historical track metrics. No target qualifying/weather. This is a pre-weekend benchmark, not an exact live endpoint replay. |
| rating-only | Current rating as mean; same production uncertainty and DNF penalty as baseline. |
| pace-only | Current pace as mean; same production uncertainty and DNF penalty as baseline. |
| clean-pace | All 2021+ clean session Pace observations, exponential half-life 180 days; no separate failure penalty. |
| clean-pace-risk | Same clean pace and variance, plus separate finishing-incident and DNF outcomes. |
| clean-pace-track-risk | Previous model plus a shrunk same-track adjustment. |

Clean observations exclude detected incidents and unfinished runs. Relative stage
weights come from the existing `SessionWeightOptions`, normalized to one final.
Variance uses a five-final-equivalent prior with standard deviation 5, bounded to
1.25–12. With no clean observations, the current Pace is a fallback. Incident and
DNF priors use eight final-equivalents with probabilities 8% and 3%, respectively.
They are mutually exclusive: a DNF is not charged again as a finishing incident.
The sampled DNF cannot win or reach the podium; a finishing incident applies an
experimental performance loss. Composite Consistency is **not** also used as
clean-run variance in these variants. All RNG seeds are fixed for reproducibility.

## Reading the scores

- Winner accuracy: rider with the highest **win** probability won.
- Recall@3/@5: fraction of actual podium found in the predicted first 3/5 riders,
  ordered by podium probability. 66.7% means two out of three, not a 66.7% chance
  of predicting the complete podium.
- Brier/log loss: probability quality; lower is better. These also score riders
  who did not reach the podium, so inspect recall and per-event predictions too.
- NDCG@3: quality of the predicted order, rewarding higher actual podium positions.

No entry-list/injury model is included. Probabilities are conditional on historical
candidates participating; they are not guaranteed real-world podium chances.
Forty category-events are a small sample, with correlated men's/women's race
conditions. A better Brier score alone is not sufficient reason to replace the
production predictor. The executable deliberately does not auto-promote a model.

## Tests

```powershell
dotnet test UCI_Stats_API.Tests
```

Tests cover cutoff leakage, invalid dates, clean variance vs incidents, mutually
exclusive DNF handling, deterministic simulation, excluded podium riders, winner
scoring, and blocking database mutations. Core calculator formulas are unchanged.
