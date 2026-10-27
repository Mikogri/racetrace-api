"""Retrospective same-round sibling profiles, never session-order inference."""
from collections import defaultdict
import numpy as np


def references(profiles, meta, courses):
    profiles = np.asarray(profiles, float)
    if profiles.shape != (len(meta), 5) or len(courses) != len(meta):
        raise ValueError('Expected five profile slots and matching metadata')
    pools = defaultdict(list)
    for i, row in enumerate(meta):
        pools[row['group'], row['category'], courses[i], row['key'].split('/')[1]].append(i)
    reference, counts = np.full_like(profiles, np.nan), np.zeros_like(profiles, int)
    for i, row in enumerate(meta):
        sid, rider = row['key'].split('/')
        other = [j for j in pools[row['group'], row['category'], courses[i], rider]
                 if meta[j]['key'].split('/')[0] != sid]
        for sector in range(5):
            measured = profiles[other, sector]
            measured = measured[np.isfinite(measured)]
            if len(measured):
                reference[i, sector], counts[i, sector] = np.median(measured), len(measured)
    return reference, counts


def placebo(reference, counts, meta, courses):
    """Swap only reference values, matching their support and different rider IDs."""
    result, changed = reference.copy(), np.zeros(len(meta), bool)
    buckets = defaultdict(list)
    for i, row in enumerate(meta):
        if np.isfinite(reference[i]).any():
            buckets[row['group'], row['category'], courses[i], row['key'].split('/')[0], tuple(counts[i]),
                    tuple(np.isfinite(reference[i]))].append(i)
    for rows in buckets.values():
        for position, i in enumerate(rows):
            rider = meta[i]['key'].split('/')[1]
            for offset in range(1, len(rows)):
                j = rows[(position + offset) % len(rows)]
                if meta[j]['key'].split('/')[1] != rider:
                    result[i], changed[i] = reference[j], True
                    break
    return result, changed


def features(profiles, reference, counts, durations):
    profiles, reference, counts, durations = map(np.asarray, [profiles, reference, counts, durations])
    valid = np.isfinite(profiles) & np.isfinite(reference) & (counts > 0) & (durations > 0)
    result = np.zeros((*profiles.shape, 5))
    result[:, :, 3], result[:, :, 4] = counts, ~valid
    for i in range(len(profiles)):
        take = valid[i]
        if not take.any():
            continue
        delta = profiles[i, take] - reference[i, take]
        result[i, take, 0] = delta
        result[i, take, 1] = delta - np.median(delta)
        result[i, take, 2] = durations[i, take] * np.exp(reference[i, take]) * np.expm1(delta)
    return np.hstack([result.reshape(len(profiles), -1), result.max(axis=1),
                      np.median(result, axis=1), result.min(axis=1), result.std(axis=1)])
