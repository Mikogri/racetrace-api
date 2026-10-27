"""Predeclared >=90% observed recall AND <=700 alarms acceptance experiment."""
import argparse
import hashlib
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset,sha
from experiment import evaluation_splits,train_bags,predict,threshold_for_budget,metrics,write_csv
from conditional import prepare,fit_predict_reconstruction,reconstruction_features,goal_passed

BUDGETS=[.02,.025,.03,700/20340,.04]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[2])
    parser.add_argument('--documents',type=Path,required=True)
    parser.add_argument('--output',type=Path,default=Path('artifacts/incident-ml-target'))
    args=parser.parse_args()
    root,out=args.root.resolve(),args.output.resolve()
    out.mkdir(parents=True,exist_ok=True)
    started=time.perf_counter()
    x,meta,sectors,hashes=load_dataset(root,args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    digest=hashlib.sha256((json.dumps(hashes,sort_keys=True)+sha(Path(__file__).with_name('conditional.py'))).encode()).hexdigest()
    cache=out/'contexts.npz'
    if cache.exists() and (out/'contexts.sha256').read_text()==digest:
        arrays=np.load(cache)
        context,target,centers,raw,peers=(arrays[k] for k in ['context','target','centers','raw','peers'])
    else:
        context,target,centers,raw,peers=prepare(root,meta)
        np.savez_compressed(cache,context=context,target=target,centers=centers,raw=raw,peers=peers)
        (out/'contexts.sha256').write_text(digest)
    y=np.array([m['positive'] for m in meta])
    years=np.array([m['year'] for m in meta])
    groups=np.array([m['group'] for m in meta])
    baseline=np.array([m['production'] for m in meta])
    rows,predictions,tuning,subgroups,latency=[],[],[],[],[]
    for split,train,test in evaluation_splits(years):
        cal_year=max(years[train])
        fit,cal=train[years[train]!=cal_year],train[years[train]==cal_year]
        print(f'{split}: cross-fitting conditional sector predictions',flush=True)
        tick=time.perf_counter()
        reconstructed,regressors=fit_predict_reconstruction(context,target,fit,np.concatenate([cal,test]),groups)
        residual=reconstruction_features(target,reconstructed[:,:,0],reconstructed[:,:,1],centers,raw)
        latency.append(dict(split=split,reconstruction_seconds=time.perf_counter()-tick))
        with (out/f'{split}-reconstruction.pickle').open('wb') as handle:
            pickle.dump(regressors,handle)
        with (root/f'artifacts/incident-ml/{split}-lightgbm.pickle').open('rb') as handle:
            old=pickle.load(handle)['models']
        scores={'original-frozen':predict(old,x)}
        for name,features in [('matched-peers',np.hstack([x,peers])),
                              ('learned-reconstruction',np.hstack([x,residual])),
                              ('combined-conditional',np.hstack([x,peers,residual]))]:
            best=None
            for leaves in [7,15]:
                models=train_bags('lightgbm',leaves,features[fit],y[fit])
                calibration=predict(models,features[cal])
                threshold=threshold_for_budget(calibration,700/20340)
                objective=float(np.mean(calibration[y[cal]]>threshold))
                tuning.append(dict(split=split,model=name,leaves=leaves,calibration_year=int(cal_year),
                                   calibration_recall=objective,feature_count=features.shape[1]))
                if best is None or objective>best[0]: best=(objective,models,leaves)
            scores[name]=predict(best[1],features)
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(models=best[1],feature_count=features.shape[1]),handle)
        for name,score in scores.items():
            for budget in BUDGETS:
                threshold=threshold_for_budget(score[cal],budget)
                predicted=score[test]>threshold
                rows.append(dict(split=split,model=name,budget=budget,threshold=threshold,**metrics(y[test],predicted,baseline[test])))
                for field in ['category','status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take=np.array([str(meta[i][field])==value for i in test])
                        subgroups.append(dict(split=split,model=name,budget=budget,field=field,value=value,
                                              **metrics(y[test][take],predicted[take],baseline[test][take])))
            for i in test:
                predictions.append(dict(split=split,model=name,key=meta[i]['key'],score=float(score[i])))
        # Per-fold checkpoints retain completed measurements if a later fold fails.
        write_csv(out/'metrics.csv',rows)
        print(f'{split}: completed candidate evaluation',flush=True)
    pooled=[]
    for name in ['original-frozen','matched-peers','learned-reconstruction','combined-conditional']:
        for budget in BUDGETS:
            part=[r for r in rows if r['split'].startswith('leave-') and r['model']==name and r['budget']==budget]
            hits=sum(r['hits'] for r in part)
            positives=sum(r['positives'] for r in part)
            alarms=sum(r['alarms'] for r in part)
            pooled.append(dict(model=name,budget=budget,hits=hits,positives=positives,alarms=alarms,
                               recall=hits/positives,alarm_rate=alarms/len(meta),goal_passed=goal_passed(hits,positives,alarms)))
    for filename,values in [('pooled.csv',pooled),('predictions.csv',predictions),('subgroups.csv',subgroups)]:
        write_csv(out/filename,values)
    protected=json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p)==digest for p,digest in protected.items())
    manifest=dict(seed=20261026,goal=dict(minimum_hits=307,positives=341,maximum_alarms=700),
                  goal_passed=any(r['goal_passed'] for r in pooled),input_hashes=hashes,
                  source_hashes={p.name:sha(p) for p in [Path(__file__),Path(__file__).with_name('conditional.py')]},
                  tuning=tuning,latency=latency,elapsed_seconds=time.perf_counter()-started,
                  protected_sources_unchanged=True,exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    from report import table,pct
    report=['## Conditional timing experiment: 307/341 with at most 700 alarms', '',
            'The goal is conjunctive: at least 90% of the 341 known-positive runs (307), with **at most 700 actual alarms** '
            'in pooled leave-season-out predictions. A calibration target of 700/20,340 is not a hard test quota. '
            'Five targets (2%, 2.5%, 3%, 700/20,340, 4%) were fixed before this run. No test-top-700 cutoff or identity-specific rule is used.', '',
            'This is further exploratory development on already inspected years, not a fresh independent holdout. '
            'Each outer training partition reserves its latest year for calibration/hyperparameter selection. '
            'The conditional timing regressor additionally cross-fits training-event groups before its errors are used '
            'by the run classifier; it never predicts its own training rows for that training feature.', '',
            '`matched-peers` derives expected sector timing from 5/10/20 nearest same-session riders, matched on other '
            'sectors only, excluding the target rider. Ties include all equally close peers. Both the query sector time '
            'and finish outcomes are excluded from matching. `learned-reconstruction` uses LightGBM conditional median '
            'and 85th-quantile sector-time predictions with the target measurement masked. Field centers exclude the '
            'target rider. It learns from the timing mixture, not a verified clean-run set. Quantiles are not calibrated '
            'crash intervals. `combined-conditional` includes both representations. The original model is a fixed reference.', '',
            table(['Model','Calibration target','Known hits / 341','Recall','Actual alarms','Actual rate','Goal passed'],
                  [(r['model'],pct(r['budget']),f"{r['hits']}/341",pct(r['recall']),r['alarms'],pct(r['alarm_rate']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        report += [f'### Conditional timing: {split}', '',
                   table(['Model','Calibration target','Known hits / labelled','Recall','Actual alarms','Actual rate'],
                         [(r['model'],pct(r['budget']),f"{r['hits']}/{r['positives']}",pct(r['recall']),r['alarms'],pct(r['alarm_rate']))
                          for r in rows if r['split']==split]), '']
    report += ['### Acceptance result', '',
               '**'+('A candidate met the numerical exploratory goal; independent validation is still required.' if manifest['goal_passed'] else
                    'No tested candidate met both requirements. The 307/341 and <=700 goal was not achieved.')+'**', '',
               'No model is promoted and no production file is changed. Positive-only labels still prevent precision/FPR '
               'claims. Exact data, predictions, fits, calibration choices and per-category/status breakdowns are in '
               '`artifacts/incident-ml-target/`. Model score is not a physical-crash probability. '
               f"This run took {manifest['elapsed_seconds']:.2f} seconds on CPU.", '']
    (out/'target.md').write_text('\n'.join(report),encoding='utf-8')
    print(json.dumps(pooled,indent=2),flush=True)


if __name__ == '__main__':
    main()
