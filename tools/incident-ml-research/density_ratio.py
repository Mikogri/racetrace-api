"""Nonnegative kernel ratio of observed-positive to unlabelled pattern density."""
import numpy as np
from scipy.optimize import nnls
from scipy.spatial.distance import pdist
from sklearn.decomposition import PCA
from sklearn.metrics.pairwise import rbf_kernel
from sklearn.preprocessing import RobustScaler
from threadpoolctl import threadpool_limits
from aligned_runs import signed_log
from experiment import SEED


def solve_nonnegative(hessian, linear):
    # min .5*a'H*a - h'a, a>=0, transformed exactly to nonnegative LS.
    lower = np.linalg.cholesky(hessian)
    return nnls(lower.T, np.linalg.solve(lower, linear), maxiter=1000)[0]


def prepare_ratio(features, observed, components):
    features, observed = np.asarray(features, float), np.asarray(observed, bool)
    if features.ndim != 2 or not np.isfinite(features).all() or features.shape[1] == 0:
        raise ValueError('Expected finite feature rows')
    if observed.shape != (len(features),) or not observed.any() or observed.all():
        raise ValueError('Observed-positive and unlabelled fit rows are required')
    with threadpool_limits(limits=2):
        scaler = RobustScaler().fit(signed_log(features))
        transformed = np.clip(scaler.transform(signed_log(features)), -8., 8.)
        projection = None
        if components is not None and np.var(transformed, axis=0).sum() > 0:
            projection = PCA(n_components=min(components, transformed.shape[1], len(transformed)-1), svd_solver='full')
            transformed = projection.fit_transform(transformed)
        rng = np.random.default_rng(SEED)
        positives = np.flatnonzero(observed)
        landmarks = rng.choice(positives, min(64, len(positives)), replace=False)
        sample = rng.choice(len(transformed), min(512, len(transformed)), replace=False)
        distances = pdist(transformed[sample])
        positive_distances = distances[distances > 0]
        bandwidth = float(np.median(positive_distances)) if len(positive_distances) else 1.
        prepared = dict(scaler=scaler, projection=projection, centres=transformed[landmarks].copy(),
                        landmark_rows=landmarks, median_bandwidth=bandwidth)
    return prepared, transformed


def fit_prepared(prepared, transformed, observed, multiplier, ridge):
    if not np.isfinite(multiplier) or multiplier <= 0 or not np.isfinite(ridge) or ridge <= 0:
        raise ValueError('Positive finite bandwidth multiplier and regularization are required')
    observed = np.asarray(observed, bool)
    bandwidth = prepared['median_bandwidth'] * multiplier
    gamma = 1. / (2. * bandwidth**2)
    with threadpool_limits(limits=2):
        kernel = rbf_kernel(transformed, prepared['centres'], gamma=gamma)
        unknown = kernel[~observed]
        hessian = unknown.T @ unknown / len(unknown) + ridge*np.eye(kernel.shape[1])
        linear = kernel[observed].mean(axis=0)
        coefficients = solve_nonnegative(hessian, linear)
        gradient = hessian @ coefficients - linear
        active = coefficients > 1e-10
        residual = max(float(np.max(np.abs(gradient[active]))) if active.any() else 0.,
                       max(0., float(-np.min(gradient[~active]))) if (~active).any() else 0.)
        if residual > 1e-7:
            raise ArithmeticError(f'Nonnegative ratio solver failed KKT check: {residual}')
    return dict(prepared, gamma=gamma, coefficients=coefficients, multiplier=multiplier,
                ridge=ridge, kkt_residual=residual)


def ratio_scores(model, features):
    features = np.asarray(features, float)
    if not np.isfinite(features).all():
        raise ValueError('Feature missingness must be explicitly encoded before scoring')
    with threadpool_limits(limits=2):
        transformed = np.clip(model['scaler'].transform(signed_log(features)), -8., 8.)
        if model['projection'] is not None:
            transformed = model['projection'].transform(transformed)
        values = rbf_kernel(transformed, model['centres'], gamma=model['gamma']) @ model['coefficients']
    return values
