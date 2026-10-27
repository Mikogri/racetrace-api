"""Positive/unlabelled multiple-instance experiments; no production dependency."""
import numpy as np
import lightgbm as lgb
from scipy.special import expit
from experiment import SEED


def choose_instances(scores, valid, known):
    selected = np.zeros_like(valid)
    for i, locations in enumerate(known):
        explicit = [j-1 for j in locations if 0 < j <= valid.shape[1] and valid[i, j-1]]
        if explicit:
            selected[i, explicit] = True
        elif valid[i].any():
            selected[i, np.argmax(np.where(valid[i], scores[i], -np.inf))] = True
    return selected


def bag_scores(probabilities, valid, pooling='noisy-or'):
    masked = np.where(valid, probabilities, 0.)
    return masked.max(axis=1) if pooling == 'max' else 1.-np.prod(1.-masked, axis=1)


def noisy_or_loss_gradient(raw, bags, positive):
    p = expit(raw)
    log_q = np.bincount(bags, weights=-np.logaddexp(0., raw), minlength=len(positive))
    q = np.exp(log_q)
    event = np.maximum(-np.expm1(log_q), 1e-15)
    loss = np.where(positive, -np.log(event), -log_q)
    scale = q[bags]/event[bags]
    gradient = np.where(positive[bags], -scale*p, p)
    # Positive Gauss-Newton diagonal, not the possibly negative exact Hessian.
    # The bag gradient is exact; unlabelled controls are NOT verified clean.
    hessian = np.where(positive[bags], scale*p*p, p*(1.-p))
    return float(loss.sum()), gradient, np.maximum(hessian, 1e-8)


def instance_features(x, sector_data, meta):
    from trajectory import run_shape
    local = np.zeros((len(meta), 5, 33))
    valid = np.zeros((len(meta), 5), bool)
    for i, f in enumerate(sector_data):
        n = len(f)
        if not n:
            continue
        local[i, :n, :17] = f
        local[i, :n, 17:] = run_shape(meta[i]['times'], f[:, 11],
            [0. if value is None else value for value in meta[i]['best']])
        valid[i, :n] = (f[:, 12] == 0) & (f[:, 13] == 0)
    return local, valid


def train_hard(features, valid, positive, known, leaves, iterations, mine=False):
    p = np.flatnonzero(positive & valid.any(axis=1))
    u = np.flatnonzero(~positive & valid.any(axis=1))
    # Initial candidate depends on measurements, never names/known run labels.
    initial = np.maximum(features[p, :, 0], 0)*np.maximum(features[p, :, 3], 0)
    assignment = choose_instances(initial, valid[p], [known[i] for i in p])
    model = None
    controls = np.column_stack(np.where(valid[u]))
    controls[:, 0] = u[controls[:, 0]]
    for step in range(iterations):
        rng = np.random.default_rng(SEED+step)
        chosen = rng.choice(len(controls), min(len(controls), max(1000, 20*len(p))), replace=False)
        if mine and model is not None:
            values = model.booster_.predict(features[controls[:, 0], controls[:, 1]])
            hard = np.argsort(values)[-max(1, len(chosen)//2):]
            chosen = np.unique(np.concatenate([chosen[:len(chosen)//2], hard]))
        pr, ps = np.where(assignment)
        positive_x = features[p[pr], ps]
        negative_x = features[controls[chosen, 0], controls[chosen, 1]]
        target = np.concatenate([np.ones(len(pr)), np.zeros(len(chosen))])
        weights = np.concatenate([1./assignment.sum(axis=1)[pr]/len(p),
                                  np.full(len(chosen), 1./len(chosen))]) * len(target)/2
        model = lgb.LGBMClassifier(n_estimators=150, num_leaves=leaves, learning_rate=.035,
            min_child_samples=25, reg_lambda=3., random_state=SEED, n_jobs=2,
            deterministic=True, force_col_wise=True, verbosity=-1)
        model.fit(np.vstack([positive_x, negative_x]), target, sample_weight=weights)
        probabilities = model.booster_.predict(features[p].reshape(-1, features.shape[-1])).reshape(len(p), 5)
        assignment = choose_instances(probabilities, valid[p], [known[i] for i in p])
    return dict(model=model.booster_, offset=0., pooling='max', sigmoid=False)


def train_soft(features, valid, positive, known, leaves, anchor_strength):
    p = np.flatnonzero(positive & valid.any(axis=1))
    u = np.flatnonzero(~positive & valid.any(axis=1))
    rng = np.random.default_rng(SEED)
    take = np.concatenate([p, rng.choice(u, min(len(u), max(1000, 20*len(p))), replace=False)])
    bag, sector = np.where(valid[take])
    labels = positive[take]
    bag_weight = np.where(labels, .5/len(p), .5/(len(take)-len(p)))*len(take)
    anchors = np.array([sector[j]+1 in known[take[bag[j]]] and labels[bag[j]] for j in range(len(bag))])
    data = lgb.Dataset(features[take[bag], sector], label=np.zeros(len(bag)))
    def objective(raw, dataset):
        _, gradient, hessian = noisy_or_loss_gradient(raw-3., bag, labels)
        probability = expit(raw-3.)
        gradient += anchor_strength*anchors*(probability-1.)
        hessian += anchor_strength*anchors*probability*(1.-probability)
        return gradient*bag_weight[bag], hessian*bag_weight[bag]
    model = lgb.train(dict(objective=objective, metric='None', num_leaves=leaves,
        learning_rate=.035, min_data_in_leaf=35, lambda_l2=3., num_threads=2,
        deterministic=True, force_col_wise=True, verbosity=-1, seed=SEED), data, num_boost_round=200)
    # Inference does not need the closure holding training labels.
    model.params['objective'] = 'none'
    return dict(model=model, offset=-3., pooling='noisy-or', sigmoid=True)


def instance_scores(model, features):
    raw = model['model'].predict(features.reshape(-1, features.shape[-1])) + model['offset']
    return (expit(raw) if model['sigmoid'] else raw).reshape(features.shape[:2])
