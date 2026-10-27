"""Read-only corpus audit; outcome fields here never become predictors."""
import argparse
import json
from collections import Counter
from pathlib import Path
from itertools import zip_longest


def same_measurements(left, right):
    return all(a == b for a,b in zip_longest(left, right, fillvalue=None))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml/input-audit.json'))
    args = parser.parse_args()
    counts, seen = Counter(), set()
    for year in range(2021, 2027):
        folder = args.root/f'artifacts/incident-multiseason/{year}'
        bundle = json.loads((folder/f'inputs-{year}.json').read_text(encoding='utf-8-sig'))
        scored = {f"{r['SessionId']}/{r['RiderId']}" for r in json.loads((folder/'baseline.json').read_text(encoding='utf-8-sig'))}
        sessions = {str(s['id']): s for s in bundle['Data']['Sessions']}
        for run in bundle['PaceSessions']:
            key = f"{run['SessionId']}/{run['Run']['RiderId']}"
            if key not in scored:
                continue
            counts['runs'] += 1
            counts['duplicate_run_keys'] += int(key in seen)
            seen.add(key)
            counts['category_mismatches'] += int(str(sessions[run['SessionId']]['category_id']) != run['CategoryId'])
            s = sorted(run['Sectors'], key=lambda v:v['SectorIndex'])
            counts['noncontiguous_sector_indices'] += int([v['SectorIndex'] for v in s] != list(range(len(s))))
            times = [v['TimeSeconds'] for v in s]
            counts['different_missing_padding'] += int(times != run['Run']['SectorTimes'] and same_measurements(times, run['Run']['SectorTimes']))
            counts['run_sector_time_mismatches'] += int(not same_measurements(times, run['Run']['SectorTimes']))
            counts['empty_runs'] += int(not times)
            counts['varying_field_sample_count'] += int(len(set(v['Statistics']['Count'] for v in s)) > 1)
            counts['nonpositive_measured_sectors'] += sum(t is not None and t <= 0 for t in times)
            counts['missing_sectors'] += sum(t is None for t in times)
            finish = run['Run'].get('FinishTimeSeconds')
            if finish is not None and times and all(t is not None and t > 0 for t in times):
                counts['finish_sum_checked'] += 1
                counts['finish_sum_mismatch_over_10ms'] += int(abs(sum(times)-finish) > .01)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(counts, indent=2), encoding='utf-8')
    print(json.dumps(counts, indent=2))
    for required in ['duplicate_run_keys','category_mismatches','noncontiguous_sector_indices','run_sector_time_mismatches']:
        if counts[required]:
            raise ValueError(f'Input identity/sector semantics failed: {required}')


if __name__ == '__main__':
    main()
