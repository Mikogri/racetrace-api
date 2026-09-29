# Round analytics v1

`GET /api/v1/analytics/round-profile` includes `analytics` when a specific
`roundId` is requested. Home keeps its existing final-only data scope. The API
loads the final and the last eligible pre-final session in the same ranking
data load; the frontend only formats and plots the returned values.

- **Sector Efficiency:** median adjacent distinct-time rank/time slope, in
  positions per second. Ties use average ranks; at least three timings are
  required. This is a local density estimate, not a guaranteed position gain.
- **Chaos Index:** heuristic 0–100, version `round-analytics-v1`. Weights:
  cumulative checkpoint rank changes 40%, existing incident detection 20%,
  DNF rate 20%, pre-final/final reshuffling 20%. Rank movement uses normalized
  Spearman footrule on common riders. Missing components are omitted and
  remaining weights normalized. No score without timing movement or matched
  pre-final evidence. DNS is excluded from starters.
- **Quali vs Final:** Spearman correlation of matched classified riders,
  including average ranks for ties. At least three pairs and nonzero variance
  are required. The last eligible Quali/Q1/Q2/Semi/Seeding session is selected
  by date and session order within the same round and category.
- **Winning Margin:** P2 sector time minus winner sector time. Negative values
  mean the winner lost time. Complete timings must sum to the finish margin;
  otherwise no decomposition is returned. Percentages may be negative or
  greater than 100; a zero finish margin has no percentage.
- **Run Efficiency:** winner's sector wins, Top 3 sectors, mean sector rank,
  timing coverage, and smallest number of positive sector gains accounting
  for 80% of total positive gains over P2 (before subtracting losses).
  Win and Top 3 percentages use measured sectors, not the full course count;
  no measured sectors produces null percentages.
- **Field Spread:** anchor P3, then 1/6, 1/3 and 2/3 of the classified final
  field, rounded to nearest rank (half up). Remove ranks at/below P3,
  duplicates and ranks beyond the field. Small fields simply have fewer
  blocks; fewer than three classified riders have no podium anchor.
  Winner-to-P3 remains the first block. Existing P10 classification is
  independent of these chart thresholds.
- **Field Progression:** incremental gaps between position thresholds, with
  an optional outlying suffix removed for chart readability only. Normalize
  each block to seconds per position. After at least three valid blocks and
  halfway through the field, hide a suffix only when every remaining block
  exceeds max(4 × earlier median, median + 6 × MAD, 0.001 seconds/position).
  Interior spikes, missing evidence, and ordinary partial final blocks stay.
  `positionSpreadFullPosition` preserves the original extent for disclosure.
  This conservative display heuristic does not change results or Field Spread.
- **Performance Distribution:** existing final-session Pace for classified
  finishers; ten bins `[0,10)` through `[90,100]`, median, and Tukey 1.5-IQR
  outliers. Coverage is explicit. DNF/DNS/DSQ are excluded.

Missing data stays null/empty, not zero or mocked. Missing intermediate splits
do not become combined sectors. Existing timing projection guards apply to
finish-sector reconstruction. No metric calculator, database schema, or
production prediction weights are changed. Upset Index is intentionally absent.
