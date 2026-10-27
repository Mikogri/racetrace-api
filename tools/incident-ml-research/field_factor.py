"""Retrospective same-session low-rank tempo plus sparse positive log delays."""
import numpy as np


def field_delays(times, groups, rank, penalty):
    times, groups = np.asarray(times, float), np.asarray(groups)
    if times.ndim != 2 or times.shape[1] != 5 or len(groups) != len(times):
        raise ValueError('Expected five sector slots and one session group per run')
    if rank not in [1, 2] or not np.isfinite(penalty) or penalty <= 0:
        raise ValueError('Expected rank 1/2 and a positive finite penalty')
    output = np.full((*times.shape, 4), np.nan)
    measured = np.isfinite(times) & (times > 0)
    for group in np.unique(groups):
        rows = np.flatnonzero((groups == group) & (measured.sum(axis=1) >= 2))
        columns = np.flatnonzero(measured[rows].sum(axis=0) >= 3)
        if len(columns) < 3:
            continue
        rows = rows[measured[np.ix_(rows, columns)].sum(axis=1) >= 2]
        if len(rows) < 8 or np.any(measured[np.ix_(rows, columns)].sum(axis=0) < 3):
            continue
        raw = times[np.ix_(rows, columns)]
        mask = measured[np.ix_(rows, columns)]
        log_times = np.full(raw.shape, np.nan)
        log_times[mask] = np.log(raw[mask])
        profiles = log_times - np.nanmedian(log_times, axis=0)
        tempo = np.nanmedian(profiles, axis=1)
        profiles -= np.nanmedian(profiles - tempo[:, None], axis=0)
        initial = np.broadcast_to(tempo[:, None], profiles.shape).copy()
        deviation = profiles - initial
        scale = np.maximum(.002, 1.4826 * np.nanmedian(np.abs(deviation - np.nanmedian(deviation, axis=0)), axis=0))
        threshold = penalty * scale
        sparse = np.where(mask, np.maximum(deviation - threshold, 0.), 0.)
        low = initial
        for _ in range(20):
            # Missing entries are imputed from the current model only for SVD;
            # they are never measurements, losses, or exported incident scores.
            completed = np.where(mask, profiles - sparse, low)
            u, singular, v = np.linalg.svd(completed, full_matrices=False)
            low = (u[:, :rank] * singular[:rank]) @ v[:rank]
            sparse = np.where(mask, np.maximum(profiles - low - threshold, 0.), 0.)
        positive = np.maximum(profiles - low, 0.)
        # Diagnostic loss, not an established counterfactual race-time correction.
        seconds = raw * -np.expm1(-positive)
        features = np.stack([positive, seconds, positive / scale, sparse], axis=-1)
        features[~mask] = np.nan
        output[np.ix_(rows, columns)] = features
    return output
