"""Checkpoint movement without final position/time-gap predictors."""
import json
from collections import defaultdict
import numpy as np


def ranks(values):
    return np.searchsorted(np.sort(values), values, side='left')+1


def checkpoint_features(times, finish):
    measured = np.isfinite(times) & (times > 0)
    complete = np.logical_and.accumulate(measured, axis=1)
    cumulative = np.cumsum(np.where(measured, times, 0.), axis=1)
    f = np.zeros((len(times), 5, 10))
    for j in range(5):
        if j > finish:
            continue
        actual = np.flatnonzero(measured[:, j])
        if len(actual):
            f[actual, j, 4] = ranks(times[actual, j])
        if j:
            prior = np.flatnonzero(complete[:, j-1])
            if len(prior):
                prior_rank = ranks(cumulative[prior, j-1])
                f[prior, j, 3] = prior_rank
                f[prior, j, 9] = (prior_rank-1)/max(1,len(prior)-1)
        # No cumulative final position, final time or final gap enters X.
        if not j or j >= finish:
            continue
        cohort = np.flatnonzero(complete[:, j] & complete[:, j-1])
        if len(cohort) < 2:
            continue
        before, after = cumulative[cohort, j-1], cumulative[cohort, j]
        movement = ranks(after)-ranks(before)
        gap_change = (after-after.min())-(before-before.min())
        center = np.median(movement)
        scale = max(1., 1.4826*np.median(np.abs(movement-center)))
        time_center = np.median(gap_change)
        time_scale = max(.2, 1.4826*np.median(np.abs(gap_change-time_center)))
        f[cohort, j, 0] = movement
        f[cohort, j, 1] = movement/max(1,len(cohort)-1)
        f[cohort, j, 2] = gap_change
        f[cohort, j, 5] = 1.
        f[cohort, j, 6] = (movement-center)/scale
        f[cohort, j, 7] = (gap_change-time_center)/time_scale
        f[cohort, j, 8] = len(cohort)
    return f


def prepare(root, meta):
    features = {}
    for year in range(2021,2027):
        bundle=json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        sessions=defaultdict(list)
        for row in bundle['PaceSessions']:
            t=np.full(5,np.nan)
            for s in row['Sectors']:
                if s['TimeSeconds'] is not None and s['TimeSeconds']>0:
                    t[s['SectorIndex']]=s['TimeSeconds']
            sessions[row['SessionId']].append((f"{row['SessionId']}/{row['Run']['RiderId']}",t,len(row['Sectors'])))
        for rows in sessions.values():
            finish=max((r[2] for r in rows),default=5)-1
            values=checkpoint_features(np.array([r[1] for r in rows]),finish)
            for (key,_,_), item in zip(rows,values):
                features[key]=np.concatenate([item.ravel(),item.max(axis=0),np.median(item,axis=0),
                                              item.min(axis=0),item.std(axis=0)])
    return np.array([features[r['key']] for r in meta])
