"""Frozen fit-positive profile routing; no query label or refitting."""
import numpy as np
from threadpoolctl import threadpool_limits


def gated_scores(state, profiles, measured, ranks):
    ranks = np.asarray(ranks, float)
    if ranks.shape != (len(state['groups']), len(profiles)) or len(measured) != len(profiles):
        raise ValueError('Expert scores, query profiles and supports must align')
    result = np.max(ranks, axis=0)
    take = np.flatnonzero(np.asarray(measured) >= 2)
    if state['clusterer'] is not None and len(take):
        with threadpool_limits(limits=2):
            transformed = np.clip(state['scaler'].transform(profiles[take]), -4., 4.)
            chosen = state['clusterer'].predict(transformed)
        result[take] = ranks[chosen, take]
    return result
