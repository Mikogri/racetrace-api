"""Retrospective same-session density context; no incident labels are inputs."""
import numpy as np
from sklearn import config_context
from sklearn.neighbors import LocalOutlierFactor
from sklearn.preprocessing import RobustScaler
from threadpoolctl import threadpool_limits
from aligned_runs import signed_log
from experiment import threshold_for_budget


def field_density(features, groups, measured, fraction):
    result = np.full(len(features), np.nan)
    with threadpool_limits(limits=2), config_context(working_memory=64):
        for group in np.unique(groups):
            take = np.flatnonzero((groups == group) & measured)
            if len(take) < 2:
                continue
            # This is a current-field statistic, like a field median, not an
            # inductively trained global model. No other session is consulted.
            values = RobustScaler().fit_transform(signed_log(features[take]))
            neighbors = min(len(take)-1, max(2, int(np.ceil(fraction*len(take)))))
            model = LocalOutlierFactor(n_neighbors=neighbors, novelty=False,
                                      algorithm='brute', n_jobs=2)
            model.fit(values)
            # The public fit-time LOF attribute excludes each point from its
            # own neighbour set. Novelty score_samples on fit rows would not.
            result[take] = -model.negative_outlier_factor_
    return result


def available_threshold(values, budget):
    available = values[np.isfinite(values)]
    if not len(available):
        raise ValueError('No available calibration density scores')
    # Missing scores stay unknown and count in the population, not as fake
    # clean examples. Adjust the available-row budget to the full denominator.
    effective = min(1., budget*len(values)/len(available))
    return threshold_for_budget(available, effective)
