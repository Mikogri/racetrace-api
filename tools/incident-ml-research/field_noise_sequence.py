"""Measured field dispersion adapter for the archived positive-delay solver."""
import numpy as np


def scale_observations(observations, sectors, history=None):
    residual, expected, valid = observations
    dispersion = np.full_like(residual, .04)
    for i, values in enumerate(sectors):
        for j, value in enumerate(values):
            mad, count = value[9], value[10]
            if np.isfinite(mad) and mad > 0 and count >= 8:
                dispersion[i, j] = max(.005, mad)
    if history is not None:
        spread = np.where((history[:, -1] > 0) & np.isfinite(history[:, -2]) &
                          (history[:, -2] >= 0), history[:, -2], 0.)
        dispersion = np.sqrt(dispersion**2+spread[:, None]**2)
    scale = dispersion/.04
    # Multiplying the reference clock inversely preserves residual*clock,
    # so the reused ex-Gaussian solver still learns delay means in seconds.
    return residual/scale, expected*scale, valid.copy()
