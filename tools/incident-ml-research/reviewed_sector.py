"""Fit-only reviewed sector anchors and selected negative controls; offline only."""
import numpy as np
from lightgbm import LGBMClassifier
from experiment import SEED
from multiple_instance import choose_instances
from review_supervision import training_labels


def prepare_fit(original, known, fit, reviews, use_sectors):
    eligible = np.array([i for i in fit if not reviews.get(int(i), {}).get('conflict', False)], int)
    target, negative = training_labels(original, eligible, reviews, promote=True)
    anchors = [[] for _ in original]
    for i in eligible:
        anchors[i] = list(known[i])
        row = reviews.get(int(i))
        if use_sectors and row and row['strict'] and row['decision'] == 'yes':
            anchors[i] = sorted(set(anchors[i]) | set(row['sectors']))
    return eligible, target, negative, anchors


def sample_controls(valid, positive, negative, multiplier, seed):
    if multiplier < 1 or not np.isfinite(multiplier):
        raise ValueError('Negative-control multiplier must be finite and at least one')
    if np.any(positive & negative):
        raise ValueError('A run cannot be both a positive bag and a reviewed negative')
    coordinates = np.column_stack(np.where(valid & ~positive[:, None]))
    if not len(coordinates):
        raise ValueError('No valid unlabelled controls')
    rng = np.random.default_rng(seed)
    count = min(len(coordinates), max(1000, 20 * np.sum(positive & valid.any(axis=1))))
    sampled = rng.choice(len(coordinates), count, replace=False)
    # Selected clean reviews supplement, never replace, contaminated U controls.
    reviewed = np.flatnonzero(negative[coordinates[:, 0]])
    chosen = np.unique(np.concatenate([sampled, reviewed]))
    selected = coordinates[chosen]
    weights = np.where(negative[selected[:, 0]], multiplier, 1.)
    return selected, weights / weights.sum()


def train_reviewed(features, valid, positive, known, negative, leaves, multiplier):
    p = np.flatnonzero(positive & valid.any(axis=1))
    if not len(p):
        raise ValueError('No positive bag has a valid measured sector')
    initial = np.maximum(features[p, :, 0], 0) * np.maximum(features[p, :, 3], 0)
    assignment = choose_instances(initial, valid[p], [known[i] for i in p])
    for step in range(3):
        controls, control_weights = sample_controls(valid, positive, negative, multiplier, SEED + step)
        pr, ps = np.where(assignment)
        samples = np.vstack([features[p[pr], ps], features[controls[:, 0], controls[:, 1]]])
        target = np.concatenate([np.ones(len(pr)), np.zeros(len(controls))])
        weights = np.concatenate([1. / assignment.sum(axis=1)[pr] / len(p), control_weights])
        weights *= len(target) / 2.
        model = LGBMClassifier(n_estimators=150, num_leaves=leaves, learning_rate=.035,
            min_child_samples=25, reg_lambda=3., random_state=SEED, n_jobs=2,
            deterministic=True, force_col_wise=True, verbosity=-1)
        model.fit(samples, target, sample_weight=weights)
        probabilities = model.booster_.predict(features[p].reshape(-1, features.shape[-1])).reshape(len(p), 5)
        assignment = choose_instances(probabilities, valid[p], [known[i] for i in p])
    return dict(model=model.booster_, offset=0., pooling='max', sigmoid=False)
