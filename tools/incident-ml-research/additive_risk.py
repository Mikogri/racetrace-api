"""Fit-only continuous hinge basis and regularized observed-label logistic risk."""
import warnings
import numpy as np
from sklearn.exceptions import ConvergenceWarning
from sklearn.linear_model import LogisticRegression
from threadpoolctl import threadpool_limits


def fit_basis(features, nonlinear=True):
    x = np.asarray(features, float)
    if x.ndim != 2 or not len(x) or not np.isfinite(x).all():
        raise ValueError('Require a nonempty finite fit matrix')
    x = np.sign(x)*np.log1p(np.abs(x))
    basis = []
    for column, values in enumerate(x.T):
        unique = np.unique(values)
        if len(unique) < 2:
            continue
        lower, centre, upper = np.quantile(values, [.1, .5, .9])
        scale = max(float(upper-lower), float(np.std(values)), 1e-6)
        knots = np.unique(np.quantile(values, [.25, .5, .75])) if nonlinear and len(unique) > 2 else []
        knots = [float((k-centre)/scale) for k in knots if unique[0] < k < unique[-1]]
        basis.append(dict(column=column, centre=float(centre), scale=scale, knots=knots))
    if not basis:
        raise ValueError('No varying fit measurement')
    return basis


def transform_basis(features, basis):
    x = np.asarray(features, float)
    if x.ndim != 2 or not np.isfinite(x).all():
        raise ValueError('Require a finite feature matrix')
    x = np.sign(x)*np.log1p(np.abs(x))
    columns = []
    for entry in basis:
        values = (x[:, entry['column']]-entry['centre'])/entry['scale']
        columns.append(values)
        columns.extend(np.maximum(0., values-knot) for knot in entry['knots'])
    return np.asarray(columns).T.copy()


def fit_additive(features, positive, regularization):
    x, y = np.asarray(features, float), np.asarray(positive, bool)
    if (x.ndim != 2 or y.shape != (len(x),) or not np.isfinite(x).all()
            or not y.any() or y.all() or not np.isfinite(regularization) or regularization <= 0):
        raise ValueError('Require finite fit features, both observed classes and positive C')
    model = LogisticRegression(C=regularization, class_weight='balanced', solver='lbfgs',
                               max_iter=2000, tol=1e-6)
    with threadpool_limits(limits=2), warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter('always', ConvergenceWarning)
        model.fit(x, y)
    diagnostics = dict(iterations=int(model.n_iter_[0]), convergence_warnings=sum(
        issubclass(w.category, ConvergenceWarning) for w in caught))
    return model, diagnostics
