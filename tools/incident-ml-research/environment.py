"""Existing speed-trap and round-condition context, never final classification."""
import json
from collections import defaultdict
import numpy as np
from field_residual import loo_prediction


def speed_features(speeds, ability):
    valid = np.isfinite(speeds) & (speeds > 0)
    result = np.zeros((len(speeds),8))
    result[:,0] = ~valid
    result[:,1] = valid.mean() if len(valid) else 0.
    result[:,7] = valid.sum()
    take = np.flatnonzero(valid)
    if not len(take):
        return result
    logs = np.log(speeds[take])
    center = np.median(logs)
    scale = max(.005,float(1.4826*np.median(np.abs(logs-center))))
    result[take,2] = logs-center
    result[take,3] = (logs-center)/scale
    result[take,4] = (len(take)-np.searchsorted(np.sort(logs),logs,side='right'))/max(1,len(take)-1)
    design = np.column_stack([np.ones(len(speeds)),np.nan_to_num(ability),(~np.isfinite(ability)).astype(float)])
    predicted = loo_prediction(design[take],logs,ridge=.01)
    residual = logs-predicted
    for k,i in enumerate(take):
        if not np.isfinite(residual[k]):
            continue
        peers = residual[(np.arange(len(take)) != k) & np.isfinite(residual)]
        baseline = np.median(peers) if len(peers) else 0.
        dispersion = max(.005,float(1.4826*np.median(np.abs(peers-baseline))) if len(peers) else 0.)
        result[i,5] = residual[k]
        result[i,6] = (residual[k]-baseline)/dispersion
    return result


def prepare(root,meta):
    features, conditions = {}, {}
    for year in range(2021,2027):
        bundle=json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        lookup={str(s['id']):s for s in bundle['Data']['Sessions']}
        sessions=defaultdict(list)
        for row in bundle['PaceSessions']:
            ability=[]
            for s in row['Sectors']:
                t,m=s['TimeSeconds'],s['Statistics']['Median']
                if t is not None and t>0 and m is not None and m>0:
                    ability.append(np.log(t/m))
            key=f"{row['SessionId']}/{row['Run']['RiderId']}"
            condition=lookup[row['SessionId']]['rounds'].get('conditions') or {}
            conditions[key]=str(condition.get('name') or '__missing__')
            sessions[row['SessionId']].append((key,row['Run'].get('TopSpeed'),np.median(ability) if ability else np.nan))
        for rows in sessions.values():
            speeds=np.array([r[1] for r in rows],float)
            abilities=np.array([r[2] for r in rows],float)
            values=speed_features(speeds,abilities)
            for r,value in zip(rows,values):
                features[r[0]]=value
    return np.array([features[r['key']] for r in meta]),[conditions[r['key']] for r in meta]
