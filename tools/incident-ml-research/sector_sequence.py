"""Offline weak-bag normal/positive-delay sequence likelihood; not crash probabilities."""
import itertools
import numpy as np
from scipy.optimize import minimize
from scipy.special import log_ndtr, logsumexp
from threadpoolctl import threadpool_limits

PATHS = np.array(list(itertools.product([0., 1.], repeat=5)))


def anchor_mask(meta):
    anchors = np.zeros((len(meta), 5), bool)
    for i, row in enumerate(meta):
        for number in row['known_sectors']:
            if not 1 <= number <= 5:
                raise ValueError('Sector labels must be one-based, between 1 and 5')
            anchors[i, number-1] = True
    return anchors


def observations(records, meta, history=None):
    residuals, expected, masks = [], [], []
    for i, row in enumerate(meta):
        t, median, *_ = records[row['key']]
        valid = np.isfinite(t) & (t > 0) & np.isfinite(median) & (median > 0)
        center = float(np.quantile(np.log(t[valid]/median[valid]), .35)) if valid.any() else 0.
        if history is not None and history[i, -1] > 0:
            center = float(history[i, -3])
        clock = np.where(valid, median*np.exp(center), 1.)
        residuals.append(np.divide(t, clock, out=np.ones(5), where=valid)-1.)
        expected.append(clock)
        masks.append(valid)
    return np.array(residuals), np.array(expected), np.array(masks)


def emissions(r, expected, theta):
    mu, log_sigma, log_tau = theta
    sigma, tau = np.exp(log_sigma), np.exp(log_tau)/expected
    x = r-mu
    a = x/sigma-sigma/tau
    log_cdf = log_ndtr(a)
    mills = np.exp(-.5*a*a-.5*np.log(2*np.pi)-log_cdf)
    normal = -.5*(x/sigma)**2-log_sigma-.5*np.log(2*np.pi)
    incident = -np.log(tau)+.5*(sigma/tau)**2-x/tau+log_cdf
    zero = np.zeros_like(r)
    gn = np.stack([x/sigma**2, -1+(x/sigma)**2, zero], axis=-1)
    gi = np.stack([1/tau-mills/sigma,
        (sigma/tau)**2+mills*(-x/sigma-sigma/tau),
        -1-(sigma/tau)**2+x/tau+mills*sigma/tau], axis=-1)
    return normal, incident, gn, gi


def log_prior(transitions):
    q, enter, persist = transitions
    values = PATHS[:, 0]*np.log(q)+(1-PATHS[:, 0])*np.log1p(-q)
    for j in range(1, 5):
        p = np.where(PATHS[:, j-1] > 0, persist, enter)
        values += PATHS[:, j]*np.log(p)+(1-PATHS[:, j])*np.log1p(-p)
    return values


def posterior(normal, incident, valid, transitions, positive=None, anchors=None):
    likelihood = np.where(valid, normal, 0.).sum(axis=1)[:, None]
    log_weights = likelihood + np.where(valid, incident-normal, 0.) @ PATHS.T + log_prior(transitions)
    if positive is not None:
        any_observed_incident = valid.astype(float) @ PATHS.T > 0
        log_weights[np.asarray(positive, bool)[:, None] & ~any_observed_incident] = -np.inf
    if anchors is not None:
        # Explicit fit anchors require only those observed sectors; other sectors remain latent.
        forbidden = (np.asarray(anchors, bool) & valid).astype(float) @ (1-PATHS).T > 0
        log_weights[forbidden] = -np.inf
    normalizer = logsumexp(log_weights, axis=1)
    return np.exp(log_weights-normalizer[:, None]), normalizer


def fit_sequence(r, expected, valid, positive, balanced=False, independent=False, anchors=None, iterations=20):
    available = valid.any(axis=1)
    r, expected, valid, positive = r[available], expected[available], valid[available], np.asarray(positive, bool)[available]
    anchors = np.asarray(anchors, bool)[available] if anchors is not None else None
    if not len(r) or not positive.any() or positive.all():
        raise ValueError('Need measured positive and unlabelled fitting runs')
    weights = np.ones(len(r))
    if balanced:
        weights[positive] = len(r)/(2*positive.sum())
        weights[~positive] = len(r)/(2*(~positive).sum())
    theta, transitions = np.array([0., np.log(.04), np.log(5.)]), np.array([.03, .03, .6])
    trace = []
    with threadpool_limits(limits=2):
        for _ in range(iterations):
            normal, incident, *_ = emissions(r, expected, theta)
            path_weights, ll = posterior(normal, incident, valid, transitions, positive, anchors)
            gamma = path_weights @ PATHS
            normal_weight, incident_weight = weights[:, None]*valid*(1-gamma), weights[:, None]*valid*gamma
            total = float(weights.sum())

            def objective(parameters):
                n, inc, gn, gi = emissions(r, expected, parameters)
                loss = -float(np.sum(normal_weight*n+incident_weight*inc))/total
                gradient = -np.sum(normal_weight[:, :, None]*gn+incident_weight[:, :, None]*gi, axis=(0, 1))/total
                return loss, gradient

            optimum = minimize(objective, theta, jac=True, method='L-BFGS-B',
                bounds=[(-.3, .3), (np.log(.002), np.log(.5)), (np.log(.1), np.log(300.))],
                options=dict(maxiter=60, ftol=1e-10, gtol=1e-6))
            if not np.isfinite(optimum.fun):
                raise ValueError('Nonfinite sequence emission fit')
            theta = optimum.x
            counts = weights @ path_weights
            initial = float(counts @ PATHS[:, 0])
            if independent:
                rate = (float(np.sum(counts[:, None]*PATHS))+1)/(5*total+2)
                transitions = np.full(3, rate)
            else:
                pairs = [(PATHS[:, :-1] == state).astype(float) for state in [0., 1.]]
                transitions = np.array([(initial+1)/(total+2)] + [
                    (float(np.sum(counts[:, None]*pair*PATHS[:, 1:]))+1)/
                    (float(np.sum(counts[:, None]*pair))+2) for pair in pairs])
            trace.append(dict(log_likelihood=float(weights @ ll/total), optimizer_success=bool(optimum.success),
                              optimizer_message=str(optimum.message), emission_objective=float(optimum.fun)))
    return dict(theta=theta, transitions=transitions, trace=trace, balanced=balanced, independent=independent)


def sequence_scores(model, r, expected, valid):
    normal, incident, *_ = emissions(r, expected, model['theta'])
    base = np.where(valid, normal, 0.).sum(axis=1)[:, None]
    values = base+np.where(valid, incident-normal, 0.) @ PATHS.T+log_prior(model['transitions'])
    event = valid.astype(float) @ PATHS.T > 0
    result = logsumexp(np.where(event, values, -np.inf), axis=1)-logsumexp(np.where(~event, values, -np.inf), axis=1)
    result[~valid.any(axis=1)] = np.nan
    return result
