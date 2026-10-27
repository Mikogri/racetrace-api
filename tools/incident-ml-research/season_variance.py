"""Draft fit-only season-risk variance objective; not selected for deployment."""
import numpy as np
import lightgbm as lgb
from scipy.special import expit
from threadpoolctl import threadpool_limits
from experiment import SEED


def risk_variance(margins, positive, domains, strength):
    s, y, domains = np.asarray(margins, float), np.asarray(positive, bool), np.asarray(domains)
    if (s.ndim != 1 or y.shape != s.shape or domains.shape != s.shape
            or not np.isfinite(s).all() or not np.isfinite(strength) or strength < 0):
        raise ValueError('Require finite aligned margins/domains and nonnegative strength')
    names, inverse = np.unique(domains, return_inverse=True)
    weight = np.zeros(len(s))
    for group in range(len(names)):
        take = inverse == group
        for value in [False, True]:
            select = take & (y == value)
            if not select.any():
                raise ValueError('Each fit domain must contain both observed groups')
            weight[select] = .5/select.sum()
    probability = expit(s)
    losses = np.where(y, np.logaddexp(0., -s), np.logaddexp(0., s))
    risks = np.bincount(inverse, weights=weight*losses)
    difference = risks-risks.mean()
    coefficient = 1+2*strength*difference[inverse]
    derivative, logistic_curvature = probability-y, probability*(1-probability)
    factor = len(s)/len(names)
    gradient = factor*weight*coefficient*derivative
    coupling = 2*strength*(1-1/len(names))*weight**2*derivative**2
    curvature = factor*(weight*coefficient*logistic_curvature+coupling)
    surrogate = factor*(weight*(1+2*strength*np.abs(difference[inverse]))*logistic_curvature+coupling)
    loss = len(s)*(risks.mean()+strength*np.mean(difference**2))
    return float(loss), gradient, curvature, surrogate, dict(zip(names.tolist(), risks.tolist()))


def fit_season_variance(features, positive, domains, leaves, strength, rounds=300):
    risk_variance(np.zeros(len(positive)), positive, domains, strength)

    def objective(values, dataset):
        _, gradient, _, curvature, _ = risk_variance(values, dataset.get_label().astype(bool), domains, strength)
        return gradient, np.maximum(curvature, 1e-8)

    with threadpool_limits(limits=2):
        model = lgb.train(dict(objective=objective, num_leaves=leaves, learning_rate=.025,
            min_data_in_leaf=35, lambda_l2=5., num_threads=2, deterministic=True,
            force_col_wise=True, verbosity=-1, seed=SEED),
            lgb.Dataset(features, label=np.asarray(positive, int)), num_boost_round=rounds)
    return lgb.Booster(model_str=model.model_to_string())
