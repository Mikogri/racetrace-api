"""Offline coordinate-monotone observed-label trees, not crash probabilities."""
import numpy as np
from lightgbm import LGBMClassifier
from threadpoolctl import threadpool_limits
from data import FEATURE_NAMES
from objectives import balanced_observation_weights
from experiment import SEED


def constraints(dimensions, include_rank):
    if dimensions < len(FEATURE_NAMES):
        raise ValueError('Input must start with the archived aggregate feature vector')
    result = np.zeros(dimensions, dtype=int)
    names = ['log_excess_max', 'excess_seconds_max', 'relative_excess_max', 'robust_deviation_max']
    if include_rank:
        names.append('rank_drop_max')
    result[[FEATURE_NAMES.index(name) for name in names]] = 1
    return result


def fit_monotone(features, positive, leaves, include_rank, rounds=300):
    model = LGBMClassifier(n_estimators=rounds, num_leaves=leaves, learning_rate=.025,
        min_child_samples=35, reg_lambda=5., n_jobs=2, deterministic=True,
        force_col_wise=True, verbosity=-1, random_state=SEED,
        monotone_constraints=constraints(features.shape[1], include_rank).tolist(),
        monotone_constraints_method='basic')
    with threadpool_limits(limits=2):
        model.fit(features, np.asarray(positive, int), sample_weight=balanced_observation_weights(positive))
    return model.booster_
