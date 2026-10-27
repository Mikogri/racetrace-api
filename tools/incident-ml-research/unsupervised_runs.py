"""Unlabelled-mixture novelty models; an outlier is not a verified incident."""
import numpy as np
from sklearn import config_context
from sklearn.ensemble import IsolationForest
from sklearn.neighbors import LocalOutlierFactor
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import RobustScaler, FunctionTransformer
from threadpoolctl import threadpool_limits
from aligned_runs import sector_matrix, signed_log
from experiment import SEED


def positive_profile(sectors):
    rows = sector_matrix(sectors, sorted_losses=True)
    valid = rows[:, 12] == 0
    losses = np.maximum(rows[:, [0, 1, 3, 5, 6, 7]], 0.)
    losses[~valid] = 0.
    quality = [float(valid.mean()), float(rows[:, 13].max()),
               float(rows[valid, 15].min()) if valid.any() else 0.]
    return np.concatenate([losses.ravel(), quality])


def fit_anomaly(features, kind, parameter):
    if kind == 'isolation':
        estimator = IsolationForest(n_estimators=300, max_samples=parameter,
                                    contamination='auto', n_jobs=2, random_state=SEED)
    elif kind == 'density':
        estimator = LocalOutlierFactor(n_neighbors=parameter, novelty=True,
                                      algorithm='brute', contamination='auto', n_jobs=2)
    else:
        raise ValueError(f'Unknown novelty model: {kind}')
    model = make_pipeline(FunctionTransformer(signed_log), RobustScaler(), estimator)
    with threadpool_limits(limits=2), config_context(working_memory=64):
        model.fit(features)
    return model


def anomaly_scores(model, new_features):
    # Both native score_samples methods are low for outliers. Never apply LOF
    # novelty inference to its own fit rows or use its contamination threshold.
    with threadpool_limits(limits=2), config_context(working_memory=64):
        return -model.score_samples(new_features)
