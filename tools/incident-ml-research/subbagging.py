"""Fixed positive-label subbagging; no component or seed selected on test."""
import numpy as np
from field_objectives import percentile_scores


def average_scores(component_scores, calibration, normalize=True):
    values, calibration = np.asarray(component_scores, float), np.asarray(calibration, int)
    if (values.ndim != 2 or not len(values) or not np.isfinite(values).all()
            or not len(calibration) or len(np.unique(calibration)) != len(calibration)
            or np.any((calibration < 0) | (calibration >= values.shape[1]))):
        raise ValueError('Require finite component rows and unique valid calibration indices')
    if normalize:
        values = np.array([percentile_scores(row[calibration], row) for row in values])
    return values.mean(axis=0)
