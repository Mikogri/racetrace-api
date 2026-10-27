"""Full run-level sector alignment versus aggregation and latent-sector pooling."""
import warnings
import numpy as np
from sklearn.neural_network import MLPClassifier
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import RobustScaler, FunctionTransformer
from threadpoolctl import threadpool_limits
from experiment import SEED
from objectives import balanced_observation_weights


def sector_matrix(features, sorted_losses=False):
    result = np.zeros((5, 17))
    result[:, 12] = 1.
    result[:, 16] = np.arange(5) / 4.
    result[:len(features)] = features
    if sorted_losses:
        # Whole rows move together; independent maxima would break time/rank alignment.
        order = sorted(range(5), key=lambda j: (result[j, 12], -result[j, 0], tuple(result[j])))
        result = result[order]
    return result


def signed_log(values):
    return np.sign(values) * np.log1p(np.abs(values))


def bounded(values):
    return np.clip(values, -10., 10.)


def fit_neural(features, positive, parameter):
    width, activation, alpha = parameter
    model = make_pipeline(FunctionTransformer(signed_log), RobustScaler(),
        FunctionTransformer(bounded), MLPClassifier(hidden_layer_sizes=(width,),
            activation=activation, alpha=alpha, max_iter=150, batch_size=512,
            learning_rate_init=.001, tol=1e-4, n_iter_no_change=12,
            early_stopping=False, random_state=SEED))
    with warnings.catch_warnings(record=True) as caught, threadpool_limits(limits=2):
        warnings.simplefilter('always')
        model.fit(features, positive.astype(int),
                  mlpclassifier__sample_weight=balanced_observation_weights(positive))
    diagnostics = dict(iterations=int(model[-1].n_iter_), loss=float(model[-1].loss_),
                       warnings=[f'{type(w.message).__name__}: {w.message}' for w in caught])
    return model, diagnostics


def neural_scores(model, features):
    with threadpool_limits(limits=2):
        return model.predict_proba(features)[:, 1]
