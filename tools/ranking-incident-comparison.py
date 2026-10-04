"""Report frozen before/after ranking-service output; no calculator reimplementation."""
import csv
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'artifacts/ranking-incident-comparison'
METRICS = ('rating', 'pace', 'consistency')


def read(name):
    return json.loads((DATA / name).read_text(encoding='utf-8-sig'))


def main():
    lines = ['# Ranking — conservative incident detector comparison', '',
             'Snapshot: 2026-10-01. Elite Men (API category=ME, canonical EM), season 2026 / 2025,',
             'all competition classes and sessions, rating descending, no search or metric thresholds.',
             'Before = running API and local calculator BEFORE promotion; NOT the previous research conservative candidate.',
             'The live TOP 20 in both seasons matched the local baseline exactly for Rating, Pace and Consistency.',
             'After = the actual RankingService replayed offline against the SAME cached Supabase responses.',
             'Whole rankings were calculated (231 / 222 riders), not a field restricted to the displayed TOP 20.', '',
             'Each metric cell is **before → after (delta)**. Values retain API precision (2 decimal places).',
             'Official points, identities and populations must remain unchanged; the script asserts this.',
             'This is an impact comparison, not proof that the new detector identifies true crashes.', '']
    csv_rows = []
    for year in (2026, 2025):
        before, after = read(f'before-{year}.json'), read(f'after-{year}.json')
        old, new = ({str(r['riderId']): r for r in rows} for rows in (before, after))
        assert old.keys() == new.keys()
        for rid, row in old.items():
            assert row['totalPoints'] == new[rid]['totalPoints']
        live = read(f'live-before-{year}.json')['items']
        assert [r['riderId'] for r in live] == [r['riderId'] for r in before[:20]]
        assert all(all(r[m] == old[str(r['riderId'])][m] for m in METRICS) for r in live)
        old_top, new_top = ({str(r['riderId']) for r in rows[:20]} for rows in (before, after))
        entering, leaving = new_top - old_top, old_top - new_top
        lines += [f'## {year}', '',
                  f'{len(before)} riders. TOP 20 membership: {len(entering)} entering, {len(leaving)} leaving.', '',
                  '| Rider | Position before → after | Rating | Pace | Consistency |',
                  '|---|---:|---:|---:|---:|']
        displayed = before[:20] + [r for r in after[:20] if str(r['riderId']) in entering]
        for b in displayed:
            rid = str(b['riderId'])
            b, a = old[rid], new[rid]
            cells = [f'{b[m]:.2f} → {a[m]:.2f} ({a[m]-b[m]:+.2f})' for m in METRICS]
            name = b['fullName'] + (' (enters TOP 20)' if rid in entering else '')
            lines.append(f"| {name} | {b['position']} → {a['position']} | " + ' | '.join(cells) + ' |')
        for rid, b in old.items():
            a = new[rid]
            csv_rows.append([year, rid, b['fullName'], b['position'], a['position'],
                             *[v for m in METRICS for v in (b[m], a[m],
                                 round(a[m]-b[m], 2) if b[m] is not None and a[m] is not None else None)]])
        lines += ['', 'Input/output fingerprints (SHA-256):', '']
        for prefix in ('live-before', 'before', 'after'):
            path = DATA / f'{prefix}-{year}.json'
            lines.append(f'- `{path.name}`: `{hashlib.sha256(path.read_bytes()).hexdigest()}`')
        lines.append('')
    lines += ['## Interpretation and safeguards', '',
              '- Pace can change for an unflagged rider: corrected finish times rebuild the reference distribution for the entire session/category before anyone is scored.',
              '- More incident detections can raise Pace but lower Consistency: the incident sector is excluded from Pace while actual instability remains penalized by Consistency. Rating combines both; its weights were not changed.',
              '- Example: Amaury Pierron 2026 has higher Pace but lower Consistency and Rating; the detector was not tuned to force a particular rider to lead the season ranking.',
              '- No categories are mixed. No points, official finish times or sporting classifications are edited.',
              '- Large local loss: at least 4 seconds with robust evidence. The smaller-loss exception requires top-field pace on both adjacent measured sectors, recovery and a rank collapse; it cannot rescue a small terminal-sector loss.',
              '- Detected sustained losses without recovery do not authorize reconstructing the finish time.',
              '- 2026 validation: 347 flagged official runs; 54/86 supplied eligible positives. These positives informed development and are not an independent precision benchmark.',
              '- Raw versus preaggregated calculations: 3664 runs, zero differing classifications or Pace values (tolerance 1e-7).', '',
              '## Reproduce', '', 'From repository root, before modifying the algorithm:', '', '```powershell',
              'dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --ranking-incident-comparison --api racetrace-api --name before',
              '# After changing the calculator, replay without database requests:',
              'dotnet run --project tools/racetrace-api.PodiumBacktest -c Release -- --ranking-incident-comparison --api racetrace-api --name after --offline',
              'python tools/ranking-incident-comparison.py', '```', '',
              'The report additionally checks authenticated live-before-2026.json / live-before-2025.json snapshots captured before editing.',
              'Raw response snapshots and all-rider CSV remain in ignored artifacts/ranking-incident-comparison; no credentials are stored.',
              'Loading the new calculator in a running API requires restart/deployment. Full preaggregation rebuild is not required.', '']
    (ROOT / 'docs/ranking-incident-comparison-2025-2026.md').write_text('\n'.join(lines), encoding='utf-8')
    with (DATA / 'comparison.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(['season', 'rider_id', 'rider', 'position_before', 'position_after',
                         *[f'{m}_{suffix}' for m in METRICS for suffix in ('before', 'after', 'delta')]])
        writer.writerows(csv_rows)
    print('\n'.join(lines))


if __name__ == '__main__':
    import sys
    sys.stdout.reconfigure(encoding='utf-8')
    main()
