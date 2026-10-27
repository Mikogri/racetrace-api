"""Conditional timing research. Neither names nor incident labels define peers."""
import json
from pathlib import Path
import numpy as np
from lightgbm import LGBMRegressor
from sklearn.model_selection import GroupKFold


def goal_passed(hits, positives, alarms):
    return hits >= int(np.ceil(.9*positives)) and alarms <= 700


def masked_context(times, center, spread, counts, sector):
    result=[sector/4.,np.log(max(center[sector],.001)),spread[sector],np.log1p(counts[sector])]
    for j in range(5):
        if j == sector or not np.isfinite(times[j]) or center[j] <= 0:
            result.extend([0.,1.,0.])
        else:
            result.extend([np.log(times[j]/center[j]),0.,np.log(center[j])])
    return np.array(result)


def peer_reference(times, row, sector, k):
    pool=np.flatnonzero(np.isfinite(times[:,sector]) & (times[:,sector]>0) & (np.arange(len(times)) != row))
    if not len(pool):
        return np.nan,0.,np.nan,0,0.,0.
    other=np.arange(times.shape[1]) != sector
    own=times[row,other]
    peers=times[pool][:,other]
    available=np.isfinite(peers) & (peers>0) & np.isfinite(own)[None,:] & (own>0)[None,:]
    with np.errstate(invalid='ignore',divide='ignore'):
        differences=np.where(available,np.abs(np.log(peers/own)),np.nan)
    shared=available.sum(axis=1)
    distance=np.full(len(pool),np.inf)
    comparable=shared>0
    distance[comparable]=np.nanmedian(differences[comparable],axis=1)
    if not np.any(shared):
        distance[:]=0.
    cutoff=np.sort(distance)[min(k,len(pool))-1]
    selected=np.flatnonzero(distance<=cutoff)
    values=times[pool[selected],sector]
    expected=float(np.median(values))
    mad=float(1.4826*np.median(np.abs(np.log(values)-np.log(expected))))
    finite=distance[selected][np.isfinite(distance[selected])]
    return expected,mad,float(np.quantile(values,.9)),len(values),float(np.mean(finite)) if len(finite) else 0.,float(np.mean(shared[selected]))


def summarize(sectors):
    out=[]
    for col in sectors.T:
        finite=col[np.isfinite(col)]
        out.extend([np.max(finite),np.median(finite),np.min(finite),np.std(finite)] if len(finite) else [0.]*4)
    return np.asarray(out)


def prepare(root,meta):
    context=np.zeros((len(meta),5,19))
    target=np.full((len(meta),5),np.nan)
    centers=np.zeros((len(meta),5))
    raw=np.full((len(meta),5),np.nan)
    peer_rows=np.zeros((len(meta),84))
    index={m['key']:i for i,m in enumerate(meta)}
    for year in range(2021,2027):
        bundle=json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        grouped={}
        for run in bundle['PaceSessions']:
            grouped.setdefault(run['SessionId'],[]).append(run)
        for runs in grouped.values():
            times=np.full((len(runs),5),np.nan)
            for i,run in enumerate(runs):
                for s in run['Sectors']:
                    v=s['TimeSeconds']
                    if v is not None and np.isfinite(v) and v>0 and s['SectorIndex']<5:
                        times[i,s['SectorIndex']]=v
            for i,run in enumerate(runs):
                key=f"{run['SessionId']}/{run['Run']['RiderId']}"
                if key not in index: continue
                r=index[key]
                raw[r]=times[i]
                pool=np.arange(len(runs)) != i
                center=np.zeros(5)
                spread=np.zeros(5)
                count=np.zeros(5)
                for j in range(5):
                    values=times[pool,j]
                    values=values[np.isfinite(values)]
                    count[j]=len(values)
                    if len(values):
                        center[j]=np.median(values)
                        spread[j]=1.4826*np.median(np.abs(np.log(values)-np.log(center[j])))
                centers[r]=center
                peer=np.zeros((5,21))
                for j in range(5):
                    context[r,j]=masked_context(times[i],center,spread,count,j)
                    if np.isfinite(times[i,j]) and center[j]>0:
                        target[r,j]=np.log(times[i,j]/center[j])
                    for block,k in enumerate([5,10,20]):
                        expected,mad,upper,n,distance,shared=peer_reference(times,i,j,k)
                        if np.isfinite(times[i,j]) and np.isfinite(expected) and expected>0:
                            excess=np.log(times[i,j]/expected)
                            peer[j,block*7:block*7+7]=[excess,times[i,j]-expected,np.expm1(excess),
                                excess/max(.02,mad),np.log(times[i,j]/upper),np.log1p(n),distance]
                peer_rows[r]=summarize(peer)
        print(f'Prepared matched-peer and masked contexts: {year}',flush=True)
    return context,target,centers,raw,peer_rows


def reconstruction_features(target,median,upper,center,raw):
    rows=[]
    for actual,mid,hi,c,t in zip(target,median,upper,center,raw):
        valid=np.isfinite(actual)
        f=np.full((5,7),np.nan)
        error=actual-mid
        for j in np.flatnonzero(valid):
            later=error[j+1:][np.isfinite(error[j+1:])]
            f[j]=[error[j],t[j]-c[j]*np.exp(mid[j]),np.expm1(error[j]),actual[j]-hi[j],
                  error[j]/max(.02,hi[j]-mid[j]),
                  error[j]-np.min(later) if len(later) else 0.,float(hi[j]<mid[j])]
        rows.append(summarize(f))
    return np.nan_to_num(rows,posinf=1e4,neginf=-1e4)


def fit_predict_reconstruction(context,target,fit,others,groups):
    predictions=np.zeros((len(context),5,2))
    models=[]
    def fit_models(indices):
        xx=context[indices].reshape(-1,19)
        yy=target[indices].reshape(-1)
        valid=np.isfinite(yy)
        fitted=[]
        for quantile in [.5,.85]:
            model=LGBMRegressor(objective='quantile',alpha=quantile,n_estimators=100,num_leaves=15,
                                min_child_samples=100,reg_lambda=3.,learning_rate=.05,n_jobs=2,
                                deterministic=True,force_col_wise=True,verbosity=-1,random_state=20261026)
            model.fit(xx[valid],yy[valid])
            fitted.append(model)
        return fitted
    def apply(fitted,indices):
        for j,model in enumerate(fitted):
            predictions[indices,:,j]=model.booster_.predict(context[indices].reshape(-1,19)).reshape(-1,5)
    for train_local,held_local in GroupKFold(3).split(fit,groups=groups[fit]):
        apply(fit_models(fit[train_local]),fit[held_local])
    models=fit_models(fit)
    apply(models,others)
    return predictions,models
