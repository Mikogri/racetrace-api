# Ranking — conservative incident detector comparison

Snapshot: 2026-10-01. Elite Men (API category=ME, canonical EM), season 2026 / 2025,
all competition classes and sessions, rating descending, no search or metric thresholds.
Before = running API and local calculator BEFORE promotion; NOT the previous research conservative candidate.
The live TOP 20 in both seasons matched the local baseline exactly for Rating, Pace and Consistency.
After = the actual RankingService replayed offline against the SAME cached Supabase responses.
Whole rankings were calculated (231 / 222 riders), not a field restricted to the displayed TOP 20.

Each metric cell is **before → after (delta)**. Values retain API precision (2 decimal places).
Official points, identities and populations must remain unchanged; the script asserts this.
This is an impact comparison, not proof that the new detector identifies true crashes.

## 2026

231 riders. TOP 20 membership: 0 entering, 0 leaving.

| Rider | Position before → after | Rating | Pace | Consistency |
|---|---:|---:|---:|---:|
| Loic BRUNI | 1 → 1 | 96.71 → 96.71 (+0.00) | 91.71 → 91.72 (+0.01) | 100.00 → 100.00 (+0.00) |
| Asa VERMETTE | 2 → 2 | 93.86 → 94.35 (+0.49) | 93.23 → 92.89 (-0.34) | 74.52 → 80.81 (+6.29) |
| Jordan WILLIAMS | 3 → 3 | 93.37 → 93.40 (+0.03) | 88.88 → 88.94 (+0.06) | 85.21 → 85.21 (+0.00) |
| Jackson GOLDSTONE | 4 → 4 | 92.72 → 92.73 (+0.01) | 88.10 → 88.13 (+0.03) | 77.90 → 77.90 (+0.00) |
| Finn ILES | 5 → 6 | 91.39 → 91.21 (-0.18) | 87.90 → 88.63 (+0.73) | 77.16 → 72.12 (-5.04) |
| Max ALRAN | 6 → 5 | 91.39 → 91.45 (+0.06) | 86.76 → 86.90 (+0.14) | 81.32 → 81.32 (+0.00) |
| Amaury PIERRON | 7 → 7 | 91.24 → 90.69 (-0.55) | 87.80 → 89.21 (+1.41) | 77.67 → 66.98 (-10.69) |
| Andreas KOLB | 8 → 8 | 90.25 → 90.58 (+0.33) | 86.42 → 85.71 (-0.71) | 67.55 → 73.06 (+5.51) |
| Ryan PINKERTON | 9 → 9 | 89.60 → 89.64 (+0.04) | 82.51 → 82.58 (+0.07) | 85.82 → 85.82 (+0.00) |
| Lachlan STEVENS-MCNAB | 10 → 10 | 88.85 → 88.42 (-0.43) | 85.14 → 86.50 (+1.36) | 68.43 → 59.97 (-8.46) |
| Luca SHAW | 11 → 11 | 88.05 → 88.14 (+0.09) | 80.54 → 80.73 (+0.19) | 78.22 → 78.22 (+0.00) |
| Till ALRAN | 12 → 12 | 86.89 → 86.84 (-0.05) | 78.74 → 78.66 (-0.08) | 83.78 → 83.78 (+0.00) |
| Troy BROSNAN | 13 → 13 | 86.36 → 86.54 (+0.18) | 80.23 → 80.53 (+0.30) | 78.04 → 78.04 (+0.00) |
| Loris VERGIER | 14 → 14 | 86.28 → 86.36 (+0.08) | 79.53 → 79.67 (+0.14) | 79.56 → 79.56 (+0.00) |
| Jakob JEWETT | 15 → 15 | 85.77 → 85.76 (-0.01) | 80.22 → 80.21 (-0.01) | 65.48 → 65.48 (+0.00) |
| Antoine PIERRON | 16 → 17 | 85.52 → 85.07 (-0.45) | 77.70 → 78.30 (+0.60) | 72.84 → 67.45 (-5.39) |
| Martin MAES | 17 → 16 | 85.27 → 85.31 (+0.04) | 80.07 → 80.13 (+0.06) | 71.20 → 71.20 (+0.00) |
| Henri KIEFER | 18 → 18 | 84.72 → 84.89 (+0.17) | 82.25 → 82.57 (+0.32) | 59.88 → 59.88 (+0.00) |
| Nathan PONTVIANNE | 19 → 19 | 84.34 → 84.64 (+0.30) | 82.98 → 81.67 (-1.31) | 53.11 → 61.33 (+8.22) |
| Juan MUÑOZ | 20 → 20 | 83.85 → 83.85 (+0.00) | 78.57 → 78.71 (+0.14) | 54.68 → 54.16 (-0.52) |

Input/output fingerprints (SHA-256):

- `live-before-2026.json`: `a5bd7da5ea5488af33f59dab674f47e7b1db748908ec0dc1753401c2b96ef1ad`
- `before-2026.json`: `d69489e1b739f8b5158a92ddbfc668d0ba1d028842d5036f4e5eac408ec26ea4`
- `after-2026.json`: `977655f17ffc8f2b30f5226384076a9f12204a0b993a4c639c9944dd609ceb91`

## 2025

222 riders. TOP 20 membership: 1 entering, 1 leaving.

| Rider | Position before → after | Rating | Pace | Consistency |
|---|---:|---:|---:|---:|
| Jackson GOLDSTONE | 1 → 1 | 95.61 → 96.26 (+0.65) | 93.40 → 92.95 (-0.45) | 82.41 → 91.58 (+9.17) |
| Loic BRUNI | 2 → 2 | 93.65 → 93.70 (+0.05) | 90.04 → 90.13 (+0.09) | 84.19 → 84.19 (+0.00) |
| Henri KIEFER | 3 → 5 | 90.81 → 90.77 (-0.04) | 88.96 → 88.98 (+0.02) | 66.80 → 66.36 (-0.44) |
| Amaury PIERRON | 4 → 3 | 90.63 → 91.23 (+0.60) | 89.84 → 88.72 (-1.12) | 68.08 → 78.06 (+9.98) |
| Luca SHAW | 5 → 4 | 90.59 → 91.11 (+0.52) | 84.54 → 84.76 (+0.22) | 82.61 → 86.08 (+3.47) |
| Loris VERGIER | 6 → 6 | 89.86 → 89.89 (+0.03) | 83.59 → 83.65 (+0.06) | 81.08 → 81.08 (+0.00) |
| Ronan DUNNE | 7 → 7 | 89.47 → 89.28 (-0.19) | 86.32 → 87.39 (+1.07) | 70.49 → 64.59 (-5.90) |
| Troy BROSNAN | 8 → 8 | 89.17 → 89.13 (-0.04) | 83.89 → 83.80 (-0.09) | 77.94 → 77.94 (+0.00) |
| Dylan MAPLES | 9 → 9 | 87.87 → 87.89 (+0.02) | 80.93 → 80.97 (+0.04) | 75.02 → 75.02 (+0.00) |
| Nathan PONTVIANNE | 10 → 10 | 87.06 → 87.09 (+0.03) | 80.36 → 80.43 (+0.07) | 76.40 → 76.40 (+0.00) |
| Jordan WILLIAMS | 11 → 11 | 86.84 → 86.86 (+0.02) | 79.05 → 79.00 (-0.05) | 74.37 → 74.70 (+0.33) |
| Luke MEIER-SMITH | 12 → 13 | 85.62 → 85.67 (+0.05) | 81.35 → 81.45 (+0.10) | 68.97 → 68.97 (+0.00) |
| Ryan PINKERTON | 13 → 14 | 85.36 → 85.56 (+0.20) | 82.50 → 82.84 (+0.34) | 64.30 → 64.30 (+0.00) |
| Andreas KOLB | 14 → 12 | 85.06 → 86.26 (+1.20) | 84.54 → 85.64 (+1.10) | 66.20 → 65.82 (-0.38) |
| Thibaut DAPRELA | 15 → 15 | 84.84 → 84.65 (-0.19) | 82.96 → 83.98 (+1.02) | 68.02 → 62.00 (-6.02) |
| Dakotah NORTON | 16 → 16 | 83.67 → 83.68 (+0.01) | 77.13 → 77.27 (+0.14) | 68.44 → 67.97 (-0.47) |
| Martin MAES | 17 → 18 | 83.65 → 83.35 (-0.30) | 78.44 → 79.38 (+0.94) | 69.85 → 64.05 (-5.80) |
| Antoine PIERRON | 18 → 17 | 83.37 → 83.49 (+0.12) | 78.33 → 78.65 (+0.32) | 65.79 → 65.39 (-0.40) |
| Richard RUDE JR | 19 → 19 | 82.65 → 82.65 (+0.00) | 82.78 → 82.78 (+0.00) | 56.44 → 56.44 (+0.00) |
| Oisin O CALLAGHAN | 20 → 22 | 82.61 → 82.22 (-0.39) | 77.63 → 78.15 (+0.52) | 73.99 → 69.13 (-4.86) |
| Simon CHAPELET (enters TOP 20) | 22 → 20 | 82.00 → 82.48 (+0.48) | 74.68 → 71.81 (-2.87) | 63.10 → 74.97 (+11.87) |

Input/output fingerprints (SHA-256):

- `live-before-2025.json`: `b06f3e9bc1b25d6b95eb7138aa985db409853d876be67750c1553d05c7205b2a`
- `before-2025.json`: `f02c5bb1365363b8f4d2e5c6e37ae47aa865f9448b4306a45d135fdb3478be9c`
- `after-2025.json`: `a904a9eb319c93256e47ca3506eb6f65dd0c22fee9a7c3ca4a2cbfc65affce40`

## Interpretation and safeguards

- Pace can change for an unflagged rider: corrected finish times rebuild the reference distribution for the entire session/category before anyone is scored.
- More incident detections can raise Pace but lower Consistency: the incident sector is excluded from Pace while actual instability remains penalized by Consistency. Rating combines both; its weights were not changed.
- Example: Amaury Pierron 2026 has higher Pace but lower Consistency and Rating; the detector was not tuned to force a particular rider to lead the season ranking.
- No categories are mixed. No points, official finish times or sporting classifications are edited.
- Large local loss: at least 4 seconds with robust evidence. The smaller-loss exception requires top-field pace on both adjacent measured sectors, recovery and a rank collapse; it cannot rescue a small terminal-sector loss.
- Detected sustained losses without recovery do not authorize reconstructing the finish time.
- 2026 validation: 347 flagged official runs; 54/86 supplied eligible positives. These positives informed development and are not an independent precision benchmark.
- Raw versus preaggregated calculations: 3664 runs, zero differing classifications or Pace values (tolerance 1e-7).

## Reproduce

From repository root, before modifying the algorithm:

```powershell
dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --ranking-incident-comparison --api racetrace-api --name before
# After changing the calculator, replay without database requests:
dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --ranking-incident-comparison --api racetrace-api --name after --offline
python tools/ranking-incident-comparison.py
```

The report additionally checks authenticated live-before-2026.json / live-before-2025.json snapshots captured before editing.
Raw response snapshots and all-rider CSV remain in ignored artifacts/ranking-incident-comparison; no credentials are stored.
Loading the new calculator in a running API requires restart/deployment. Full preaggregation rebuild is not required.
