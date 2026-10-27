"""After-run derived checkpoint changes; no official finish result is read."""
import numpy as np
from checkpoint_dynamics import checkpoint_features, ranks


def movements(times, finish, include_finish):
    times = np.asarray(times, float)
    if times.ndim != 2 or times.shape[1] != 5 or not 0 <= finish < 5:
        raise ValueError('Expected five sector slots and a valid nominal finish index')
    values = checkpoint_features(times, finish)
    if include_finish and finish > 0:
        measured = np.isfinite(times) & (times > 0)
        complete = np.logical_and.accumulate(measured, axis=1)
        cohort = np.flatnonzero(complete[:, finish])
        if len(cohort) >= 2:
            cumulative = np.cumsum(np.where(measured[cohort], times[cohort], 0.), axis=1)
            before, after = cumulative[:, finish-1], cumulative[:, finish]
            movement = ranks(after) - ranks(before)
            gap_change = (after - after.min()) - (before - before.min())
            center = np.median(movement)
            scale = max(1., 1.4826*np.median(np.abs(movement-center)))
            time_center = np.median(gap_change)
            time_scale = max(.2, 1.4826*np.median(np.abs(gap_change-time_center)))
            values[cohort, finish, 0] = movement
            values[cohort, finish, 1] = movement / max(1, len(cohort)-1)
            values[cohort, finish, 2] = gap_change
            values[cohort, finish, 5] = 1.
            values[cohort, finish, 6] = (movement-center)/scale
            values[cohort, finish, 7] = (gap_change-time_center)/time_scale
            values[cohort, finish, 8] = len(cohort)
    # Only changes/support are exported, not absolute prior/final ranks or time.
    return values[:, :, [0, 1, 2, 5, 6, 7, 8]]


def summarize(values):
    return np.hstack([values.reshape(len(values), -1), values.max(axis=1),
                      np.median(values, axis=1), values.min(axis=1), values.std(axis=1)])
