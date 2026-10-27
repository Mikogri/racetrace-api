"""Nested fit-label sampling; hidden P is excluded, never relabelled clean."""
import numpy as np


def sample_fit_labels(positive, groups, eligible, fraction, seed, unit):
    positive, groups, eligible = np.asarray(positive, bool), np.asarray(groups), np.asarray(eligible, int)
    if (len(positive) != len(groups) or len(np.unique(eligible)) != len(eligible)
            or np.any((eligible < 0) | (eligible >= len(positive)))):
        raise ValueError('Require aligned labels/groups and unique valid fit indices')
    if not np.isfinite(fraction) or not 0 < fraction <= 1 or unit not in ['run', 'event']:
        raise ValueError('Require a fraction in (0,1] and run/event sampling')
    p = eligible[positive[eligible]]
    if not len(p):
        raise ValueError('No fit positives to sample')
    if fraction == 1:
        return eligible.copy()
    rng = np.random.default_rng(seed)
    if unit == 'run':
        order = rng.permutation(p)
        chosen = order[:max(1, int(np.ceil(fraction*len(order))))]
    else:
        events = rng.permutation(np.unique(groups[p]))
        kept = events[:max(1, int(np.ceil(fraction*len(events))))]
        chosen = p[np.isin(groups[p], kept)]
    return eligible[(~positive[eligible]) | np.isin(eligible, chosen)]
