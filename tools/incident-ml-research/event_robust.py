"""Positive-event soft worst-risk objective on observed P/contaminated U."""
import numpy as np
import lightgbm as lgb
from scipy.special import expit, logsumexp, softmax
from threadpoolctl import threadpool_limits
from experiment import SEED


def event_risk(scores, positive, events, strength):
    scores, positive, events = np.asarray(scores, float), np.asarray(positive, bool), np.asarray(events)
    if (scores.ndim != 1 or positive.shape != scores.shape or events.shape != scores.shape
            or not np.isfinite(scores).all() or not positive.any() or positive.all()
            or not np.isfinite(strength) or strength < 0):
        raise ValueError('Require finite aligned margins, P/U labels, event IDs and nonnegative strength')
    _, inverse, counts = np.unique(events[positive], return_inverse=True, return_counts=True)
    losses = np.bincount(inverse, weights=np.logaddexp(0., -scores[positive]))/counts
    mean_loss = float(losses.mean())
    if strength == 0:
        q, risk = np.full(len(counts), 1/len(counts)), mean_loss
    else:
        q = softmax(strength*losses)
        centred = strength*(losses-mean_loss)
        if np.max(np.abs(centred)) < .5:
            risk = mean_loss+float(np.log1p(np.mean(np.expm1(centred))))/strength
        else:
            risk = float(logsumexp(strength*losses)-np.log(len(counts)))/strength
    probability, factor = expit(scores), .5*len(scores)
    weights = q[inverse]/counts[inverse]
    p, unknown = probability[positive], probability[~positive]
    gradient, curvature = np.empty(len(scores)), np.empty(len(scores))
    gradient[positive] = factor*weights*(p-1)
    curvature[positive] = factor*(weights*p*(1-p) +
        strength*q[inverse]*(1-q[inverse])*(1-p)**2/counts[inverse]**2)
    gradient[~positive] = factor*unknown/len(unknown)
    curvature[~positive] = factor*unknown*(1-unknown)/len(unknown)
    return factor*(risk+float(np.logaddexp(0., scores[~positive]).mean())), gradient, curvature


def fit_event_robust(features, positive, events, leaves, strength, rounds=300):
    events = np.asarray(events)
    event_risk(np.zeros(len(positive)), positive, events, strength)

    def objective(values, dataset):
        _, gradient, curvature = event_risk(values, dataset.get_label().astype(bool), events, strength)
        return gradient, np.maximum(curvature, 1e-8)

    with threadpool_limits(limits=2):
        model = lgb.train(dict(objective=objective, num_leaves=leaves, learning_rate=.025,
            min_data_in_leaf=35, lambda_l2=5., num_threads=2, deterministic=True,
            force_col_wise=True, verbosity=-1, seed=SEED),
            lgb.Dataset(features, label=np.asarray(positive, int)), num_boost_round=rounds)
    return lgb.Booster(model_str=model.model_to_string())
