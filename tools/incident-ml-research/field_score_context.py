"""Label-free within-field score location/scale, not a field alarm quota."""
import numpy as np
from scipy.special import logit


def adjust_field_scores(probabilities, groups, calibration, strength, studentize=False):
    p, groups, calibration = np.asarray(probabilities, float), np.asarray(groups), np.asarray(calibration, int)
    if (p.ndim != 1 or groups.shape != p.shape or not np.isfinite(p).all()
            or np.any((p < 0) | (p > 1)) or not len(calibration)
            or len(np.unique(calibration)) != len(calibration)
            or np.any((calibration < 0) | (calibration >= len(p)))
            or not np.isfinite(strength) or not 0 <= strength <= 1):
        raise ValueError('Require aligned probabilities/groups, valid calibration and strength in [0,1]')
    margins = logit(np.clip(p, 1e-12, 1-1e-12))
    centre = np.median(margins[calibration])
    scale = max(.1, 1.4826*np.median(np.abs(margins[calibration]-centre)))
    output = margins.copy()
    for group in np.unique(groups):
        indices = np.flatnonzero(groups == group)
        if len(indices) < 8:
            continue
        # Leave-one-out prevents a candidate's own score shifting its reference.
        for i in indices:
            others = margins[indices[indices != i]]
            local = np.median(others)
            local_scale = max(.1, 1.4826*np.median(np.abs(others-local)))
            shrink = len(others)/(len(others)+20)
            residual = margins[i]-local
            if studentize:
                residual *= np.clip(scale/local_scale, .5, 2.)
            adjusted = centre+residual
            output[i] += strength*shrink*(adjusted-margins[i])
    return output
