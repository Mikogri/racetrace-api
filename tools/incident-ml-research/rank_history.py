"""Causal historical sector-percentile references; identities are joins only."""
from collections import defaultdict
import json
import numpy as np
from trajectory import history_reference


def profiles_from_frozen(root, meta, sectors):
    index = {r['key']: i for i, r in enumerate(meta)}
    profiles = np.full((len(meta), 5), np.nan)
    for year in range(2021, 2027):
        bundle = json.loads((root / f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        for run in bundle['PaceSessions']:
            i = index.get(f"{run['SessionId']}/{run['Run']['RiderId']}")
            if i is None:
                continue
            for sector in run['Sectors']:
                j, rank = sector['SectorIndex'], sector['Rank']
                count = sector['Statistics']['Count'] or 0
                if (0 <= j < min(5, len(sectors[i])) and sectors[i][j, 12] == 0 and sectors[i][j, 13] == 0
                        and rank is not None and count >= 3 and np.isfinite(rank) and 1 <= rank <= count):
                    profiles[i, j] = (rank-1)/(count-1)
    return profiles


def _references(profiles, records, meta, allowed):
    histories = defaultdict(list)
    for i in allowed:
        _, _, _, rider, date, round_id, category = records[meta[i]['key']]
        values = profiles[i, np.isfinite(profiles[i])]
        if len(values) >= 2 and date:
            histories[rider].append((date, round_id, category, float(np.quantile(values, .35))))
    result = np.zeros((len(meta), 3))
    for i, row in enumerate(meta):
        _, _, _, rider, date, round_id, category = records[row['key']]
        if date:
            result[i] = history_reference(histories[rider], date, round_id, category)
    return result


def scoped_references(profiles, records, meta, fit, cal, test):
    fit, cal, test = (np.asarray(rows, int) for rows in [fit, cal, test])
    all_rows = np.concatenate([fit, cal, test])
    if len(np.unique(all_rows)) != len(all_rows) or np.any((all_rows < 0) | (all_rows >= len(meta))):
        raise ValueError('Fit/calibration/test must be disjoint valid subsets')
    frozen = _references(profiles, records, meta, fit)
    rolling = frozen.copy()
    calibration = _references(profiles, records, meta, np.concatenate([fit, cal]))
    evaluation = _references(profiles, records, meta, all_rows)
    rolling[cal], rolling[test] = calibration[cal], evaluation[test]
    return frozen, rolling


def encode(profiles, reference):
    profiles, reference = np.asarray(profiles, float), np.asarray(reference, float)
    if profiles.ndim != 2 or profiles.shape[1] != 5 or reference.shape != (len(profiles), 3):
        raise ValueError('Expected five sector percentiles and three history statistics')
    valid = np.isfinite(profiles) & (reference[:, 2:3] > 0)
    values = np.zeros((len(profiles), 5, 2))
    delta = profiles-reference[:, :1]
    values[:, :, 0] = np.where(valid, delta, 0.)
    values[:, :, 1] = np.where(valid, delta / np.maximum(.05, reference[:, 1:2]), 0.)
    return np.hstack([values.reshape(len(profiles), -1), (~valid).astype(float), reference])


def swap_references(reference, meta):
    swapped, changed = reference.copy(), np.zeros(len(meta), bool)
    buckets = defaultdict(list)
    for i, row in enumerate(meta):
        if reference[i, 2] > 0:
            buckets[row['key'].split('/')[0], row['category'], reference[i, 2]].append(i)
    for rows in buckets.values():
        for position, i in enumerate(rows):
            rider = meta[i]['key'].split('/')[1]
            other = next((rows[(position+offset) % len(rows)] for offset in range(1, len(rows))
                          if meta[rows[(position+offset) % len(rows)]]['key'].split('/')[1] != rider), None)
            if other is not None:
                swapped[i], changed[i] = reference[other], True
    return swapped, changed
