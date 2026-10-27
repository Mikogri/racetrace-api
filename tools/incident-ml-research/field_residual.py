"""Within-session residual noise and exact leave-one-out ridge sector timing."""
from collections import defaultdict
import json
import numpy as np


def loo_prediction(x, y, ridge=.1):
    if len(y) < 3:
        return np.full(len(y), np.nan)
    penalty = np.eye(x.shape[1])*ridge
    penalty[0, 0] = 0.
    inverse = np.linalg.inv(x.T @ x+penalty)
    fitted = x @ (inverse @ (x.T @ y))
    leverage = np.einsum('ij,jk,ik->i', x, inverse, x)
    return (fitted-leverage*y)/np.maximum(1e-10, 1.-leverage)


def standardized(values, valid):
    result, scales = np.zeros(len(values)), np.zeros(len(values))
    for i in np.flatnonzero(valid):
        other = values[valid & (np.arange(len(values)) != i)]
        center = float(np.median(other)) if len(other) else 0.
        scale = max(.004, float(1.4826*np.median(np.abs(other-center))) if len(other) else 0.)
        result[i], scales[i] = (values[i]-center)/scale, scale
    return result, scales


def session_features(times):
    valid = np.isfinite(times) & (times > 0)
    logs = np.full_like(times, np.nan)
    logs[valid] = np.log(times[valid])
    centers = np.array([np.median(logs[valid[:, j], j]) if valid[:, j].any() else 0. for j in range(5)])
    ratios = logs-centers
    result = np.zeros((len(times), 5, 12))
    for j in range(5):
        for q, offset in [(.5, 0), (.25, 3)]:
            residual = np.zeros(len(times))
            for i in np.flatnonzero(valid[:, j]):
                other = ratios[i, (np.arange(5) != j) & valid[i]]
                anchor = np.quantile(other, q) if len(other) else ratios[i, j]
                residual[i] = ratios[i, j]-anchor
            z, scale = standardized(residual, valid[:, j])
            result[:, j, offset] = residual
            result[:, j, offset+1] = np.where(valid[:, j], np.nan_to_num(times[:, j])*(1.-np.exp(-residual)), 0.)
            result[:, j, offset+2] = z
            if q == .5:
                result[:, j, 9] = scale
        take = np.flatnonzero(valid[:, j])
        others = np.arange(5) != j
        context = ratios[:, others]
        design = np.column_stack([np.ones(len(times)), np.nan_to_num(context), (~valid[:, others]).astype(float)])
        predicted = loo_prediction(design[take], logs[take, j])
        residual = np.zeros(len(times))
        usable = take[np.isfinite(predicted)]
        residual[usable] = logs[usable, j]-predicted[np.isfinite(predicted)]
        z, _ = standardized(residual, np.isin(np.arange(len(times)), usable))
        result[:, j, 6] = residual
        result[:, j, 7] = np.where(valid[:, j], np.nan_to_num(times[:, j])*(1.-np.exp(-residual)), 0.)
        result[:, j, 8] = z
        result[:, j, 10] = len(take)/max(1, len(times))
        result[:, j, 11] = (valid.sum(axis=1)-valid[:, j])/4.
    return np.nan_to_num(result, nan=0., posinf=1e4, neginf=-1e4)


def prepare(root, meta):
    features = {}
    for year in range(2021, 2027):
        bundle = json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        sessions = defaultdict(list)
        for row in bundle['PaceSessions']:
            values = np.full(5, np.nan)
            for sector in row['Sectors']:
                value = sector['TimeSeconds']
                if value is not None and np.isfinite(value) and value > 0:
                    values[sector['SectorIndex']] = value
            key = f"{row['SessionId']}/{row['Run']['RiderId']}"
            sessions[row['SessionId']].append((key, values))
        for rows in sessions.values():
            matrix = session_features(np.array([r[1] for r in rows]))
            for (key, _), item in zip(rows, matrix):
                summary = np.concatenate([item.max(axis=0), np.median(item, axis=0), item.min(axis=0), item.std(axis=0)])
                features[key] = np.concatenate([item.ravel(), summary])
    return np.array([features[row['key']] for row in meta])
