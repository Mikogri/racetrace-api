"""Offline observed-positive versus upper-tail-U ranking, not clean/crash risk."""
import numpy as np
import lightgbm as lgb
from scipy.special import expit
from threadpoolctl import threadpool_limits
from experiment import SEED


def tail_objective(scores, positive, fraction):
    scores, positive = np.asarray(scores, float), np.asarray(positive, bool)
    if scores.ndim != 1 or positive.shape != scores.shape or not np.isfinite(scores).all():
        raise ValueError('Require finite aligned one-dimensional scores and labels')
    if not 0 < fraction <= 1 or not positive.any() or positive.all():
        raise ValueError('Require positive and unlabelled rows and a fraction in (0,1]')
    p, u = np.flatnonzero(positive), np.flatnonzero(~positive)
    count = max(1, int(np.ceil(fraction*len(u))))
    cutoff = np.partition(scores[u], len(u)-count)[len(u)-count]
    above, tied = scores[u] > cutoff, scores[u] == cutoff
    weights = above.astype(float)+(count-int(above.sum()))/int(tied.sum())*tied
    active = weights > 0
    u, weights = u[active], weights[active]
    gradient, curvature = np.zeros(len(scores)), np.zeros(len(scores))
    loss, factor = 0., len(scores)/(len(p)*count)
    # Chunking limits the full-AUC control to O(P*256) temporary storage.
    for start in range(0, len(u), 256):
        take, w = u[start:start+256], weights[start:start+256]
        differences = scores[take][None, :]-scores[p][:, None]
        slope = expit(differences)*w[None, :]
        bend = expit(differences)*(1-expit(differences))*w[None, :]
        loss += float(np.sum(np.logaddexp(0., differences)*w[None, :]))
        gradient[p] -= factor*slope.sum(axis=1)
        gradient[take] += factor*slope.sum(axis=0)
        curvature[p] += factor*bend.sum(axis=1)
        curvature[take] += factor*bend.sum(axis=0)
    return factor*loss, gradient, curvature


def fit_tail(features, positive, leaves, fraction, rounds=300):
    def objective(values, dataset):
        _, gradient, curvature = tail_objective(values, dataset.get_label().astype(bool), fraction)
        return gradient, np.maximum(curvature, 1e-8)

    with threadpool_limits(limits=2):
        model = lgb.train(dict(objective=objective, num_leaves=leaves, learning_rate=.025,
            min_data_in_leaf=35, lambda_l2=5., num_threads=2, deterministic=True,
            force_col_wise=True, verbosity=-1, seed=SEED),
            lgb.Dataset(features, label=np.asarray(positive, int)), num_boost_round=rounds)
    # Persist only native trees, not the locally scoped training callback.
    return lgb.Booster(model_str=model.model_to_string())


def tail_scores(model, features):
    with threadpool_limits(limits=2):
        return model.predict(features, raw_score=True)
