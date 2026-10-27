"""Bounded observed-U loss, not an identified unbiased PU estimator."""
import numpy as np
import lightgbm as lgb
from scipy.special import expit
from threadpoolctl import threadpool_limits
from experiment import SEED
from objectives import balanced_observation_weights


def observed_loss(margins, positive, strength, symmetric=False):
    s, y = np.asarray(margins, float), np.asarray(positive, bool)
    if (s.ndim != 1 or y.shape != s.shape or not np.isfinite(s).all()
            or not y.any() or y.all() or not np.isfinite(strength) or not 0 <= strength <= 1):
        raise ValueError('Require finite aligned margins, both observed groups and q in [0,1]')
    sign = np.where(y, 1., -1.)
    t = sign*s
    correct, error = expit(t), expit(-t)
    log_correct = -np.logaddexp(0., -t)
    q = np.where(y & ~np.full(len(y), symmetric, bool), 0., strength)
    weight = balanced_observation_weights(y)
    power = np.exp(q*log_correct)
    losses = -log_correct.copy()
    use = q > 0
    losses[use] = -np.expm1(q[use]*log_correct[use])/q[use]
    gradient = -weight*sign*error*power
    curvature = weight*error*power*(correct-q*error)
    # Positive diagonal surrogate omits the negative nonconvex q term.
    fisher = weight*error*power*correct
    return float(np.dot(weight, losses)), gradient, curvature, fisher


def fit_bounded(features, positive, leaves, strength, symmetric, rounds=300):
    observed_loss(np.zeros(len(positive)), positive, strength, symmetric)

    def objective(margins, dataset):
        _, gradient, _, fisher = observed_loss(margins, dataset.get_label().astype(bool), strength, symmetric)
        return gradient, np.maximum(fisher, 1e-8)

    with threadpool_limits(limits=2):
        model = lgb.train(dict(objective=objective, num_leaves=leaves, learning_rate=.025,
            min_data_in_leaf=35, lambda_l2=5., num_threads=2, deterministic=True,
            force_col_wise=True, verbosity=-1, seed=SEED),
            lgb.Dataset(features, label=np.asarray(positive, int)), num_boost_round=rounds)
    return lgb.Booster(model_str=model.model_to_string())
