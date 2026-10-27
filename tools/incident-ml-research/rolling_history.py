"""Label-free rolling sporting history with strict fit/calibration/test scopes."""
import json
from collections import defaultdict
import numpy as np
from trajectory import historical_features


def records_from_frozen(root, meta, sectors):
    dates = {}
    for year in range(2021, 2027):
        bundle = json.loads((root / f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        dates.update({str(s['id']): (s['session_date'] or '')[:10] for s in bundle['Data']['Sessions']})
    records = {}
    for row, values in zip(meta, sectors):
        t, m, b = np.full(5, np.nan), np.zeros(5), np.zeros(5)
        for j, value in enumerate(values):
            if value[12] == 0 and value[13] == 0:
                t[j], m[j], b[j] = row['times'][j], value[11], row['best'][j] or 0.
        sid, rider = row['key'].split('/')
        records[row['key']] = t, m, b, rider, dates[sid], row['group'], row['category']
    return records


def scoped_features(records, meta, fit, cal, test):
    fit, cal, test = (np.asarray(rows, int) for rows in [fit, cal, test])
    all_rows = np.concatenate([fit, cal, test])
    if len(np.unique(all_rows)) != len(all_rows) or np.any((all_rows < 0) | (all_rows >= len(meta))):
        raise ValueError('Fit/calibration/test must be disjoint valid subsets')
    frozen = historical_features(records, meta, fit)
    rolling = frozen.copy()
    # Values observed before a target date may update inference state, never
    # training weights or labels. Calibration cannot access any test row.
    calibration = historical_features(records, meta, np.concatenate([fit, cal]))
    evaluation = historical_features(records, meta, all_rows)
    rolling[cal], rolling[test] = calibration[cal], evaluation[test]
    return frozen, rolling


def placebo_features(history, records, meta):
    values, changed = history.copy(), np.zeros(len(meta), bool)
    buckets = defaultdict(list)
    for i, row in enumerate(meta):
        if history[i, -1] > 0:
            buckets[row['key'].split('/')[0], row['category'], history[i, -1]].append(i)
    for rows in buckets.values():
        for position, i in enumerate(rows):
            rider = meta[i]['key'].split('/')[1]
            candidate = next((rows[(position + offset) % len(rows)] for offset in range(1, len(rows))
                              if meta[rows[(position + offset) % len(rows)]]['key'].split('/')[1] != rider), None)
            if candidate is None:
                continue
            values[i, -3:], changed[i] = history[candidate, -3:], True
            t, m, b, *_ = records[meta[i]['key']]
            valid = np.isfinite(t) & (t > 0) & (m > 0)
            losses = np.zeros((5, 2))
            center, dispersion = values[i, -3:-1]
            losses[valid, 0] = t[valid]-np.maximum(b[valid], m[valid]*np.exp(center))
            losses[valid, 1] = (np.log(t[valid]/m[valid])-center)/max(.02, dispersion)
            values[i, :10] = losses.ravel()
    return values, changed
