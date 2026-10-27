"""Exploratory lower-alarm ablation. Never modifies production or prior results."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, FEATURE_NAMES, sha
from experiment import evaluation_splits, train_bags, predict, threshold_for_budget, metrics, write_csv

BUDGETS=[.01,.025,.05,.075,.10]


def relative_columns(names):
    return [i for i,name in enumerate(names) if not (
        name.startswith(('sample_count_', 'duration_', 'rank_percentile_')) or
        name == 'sector_count' or (name.startswith('field_log_ratio_') and name != 'field_log_ratio_std'))]


def sequence_features(features):
    # Unlike independent maxima, time loss and rank contrast here describe the
    # SAME sector. No run/sector label selects the candidate.
    selected=[0,1,2,3,5,6,7,8,9,12,13,15,16]
    valid=np.flatnonzero(features[:,12] == 0)
    ranked=sorted(valid,key=lambda i:(-features[i,0],int(i)))[:2]
    rows=[features[i,selected] for i in ranked]
    rows += [np.zeros(len(selected)) for _ in range(2-len(rows))]
    positive=np.maximum(features[:,1],0)
    concentration=float(np.max(positive)/max(1.,sum(positive))) if len(positive) else 0.
    recovery=features[ranked[0],6] if ranked else 0.
    return np.concatenate(rows+[np.array([concentration,recovery])])


def run():
    parser=argparse.ArgumentParser()
    parser.add_argument('--documents',type=Path,required=True)
    parser.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output',type=Path,default=Path('artifacts/incident-ml-focus'))
    args=parser.parse_args()
    root,out=args.root.resolve(),args.output.resolve()
    out.mkdir(parents=True,exist_ok=True)
    started=time.perf_counter()
    x,meta,sectors,hashes=load_dataset(root,args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    y=np.array([m['positive'] for m in meta])
    years=np.array([m['year'] for m in meta])
    baseline=np.array([m['production'] for m in meta])
    relative=x[:,relative_columns(FEATURE_NAMES)]
    joint=np.hstack([relative,np.array([sequence_features(f) for f in sectors])])
    variants={'retuned-full':x,'relative-only':relative,'relative-sequence':joint}
    rows,tuning,groups,predictions=[],[],[],[]
    for split,train,test in evaluation_splits(years):
        cal_year=max(years[train])
        fit,cal=train[years[train]!=cal_year],train[years[train]==cal_year]
        with (root/f'artifacts/incident-ml/{split}-lightgbm.pickle').open('rb') as handle:
            frozen=pickle.load(handle)['models']
        scores={'original-frozen':predict(frozen,x)}
        for name,features in variants.items():
            best=None
            for leaves in [7,15]:
                models=train_bags('lightgbm',leaves,features[fit],y[fit])
                calibration=predict(models,features[cal])
                objective=np.mean([np.mean(calibration[y[cal]]>threshold_for_budget(calibration,b)) for b in [.025,.05]])
                tuning.append(dict(split=split,variant=name,leaves=leaves,calibration_year=int(cal_year),objective=float(objective)))
                if best is None or objective>best[0]: best=(objective,models,leaves)
            scores[name]=predict(best[1],features)
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(models=best[1],variant=name,relative_columns=relative_columns(FEATURE_NAMES)),handle)
        for name,score in scores.items():
            for budget in BUDGETS:
                threshold=threshold_for_budget(score[cal],budget)
                predicted=score[test]>threshold
                rows.append(dict(split=split,model=name,budget=budget,threshold=threshold,**metrics(y[test],predicted,baseline[test])))
                for field in ['category','status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take=np.array([str(meta[i][field])==value for i in test])
                        groups.append(dict(split=split,model=name,budget=budget,field=field,value=value,
                                           **metrics(y[test][take],predicted[take],baseline[test][take])))
            for i in test: predictions.append(dict(split=split,model=name,key=meta[i]['key'],score=float(score[i])))
        print(f'{split}: fitted lower-alarm variants',flush=True)
    pooled=[]
    for name in ['original-frozen']+list(variants):
        for budget in BUDGETS:
            part=[r for r in rows if r['split'].startswith('leave-') and r['model']==name and r['budget']==budget]
            pooled.append(dict(model=name,budget=budget,hits=sum(r['hits'] for r in part),positives=sum(r['positives'] for r in part),
                               alarms=sum(r['alarms'] for r in part),recall=sum(r['hits'] for r in part)/sum(r['positives'] for r in part),
                               alarm_rate=sum(r['alarms'] for r in part)/sum(r['runs'] for r in part)))
    for file,values in [('metrics.csv',rows),('pooled.csv',pooled),('subgroups.csv',groups),('predictions.csv',predictions)]:
        write_csv(out/file,values)
    protected=json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p)==digest for p,digest in protected.items())
    manifest=dict(seed=20261026,elapsed_seconds=time.perf_counter()-started,inputs=hashes,tuning=tuning,
                  columns={k:v.shape[1] for k,v in variants.items()},protected_sources_unchanged=True,
                  source_hash=sha(Path(__file__)),label_semantics='observed-positive vs unlabelled, no verified negatives')
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    from report import table,pct
    report=['## Lower-alarm exploratory follow-up', '',
            'This follow-up was designed after reviewing the first outer-test results and user feedback. It is **not new independent validation**. '
            'The same whole-year/forward fit–calibration–test partitions are retained; thresholds and two leaf-count choices use calibration data only. '
            'Known recall at 2.5% and 5% calibration alarm targets replaces the previous broader-budget selection objective. '
            'No unlabelled run is asserted to be clean and no production file is changed.', '',
            '`original-frozen` reuses the first LightGBM models at stricter calibration thresholds. `retuned-full` retrains the same '
            'representation for lower-budget concentration. `relative-only` excludes raw sample counts, durations, absolute rank '
            'percentiles and absolute field-relative ability (retaining variability). `relative-sequence` additionally aligns time, '
            'rank and recovery features on the same two unsupervised candidate sectors. This is an ablation, not proof that context features are harmful.', '',
            table(['Variant','Calibration target','Known hits / 341','Recall','Actual alarms','Actual rate'],
                  [(r['model'],pct(r['budget']),f"{r['hits']}/341",pct(r['recall']),r['alarms'],pct(r['alarm_rate'])) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        report += [f'### Follow-up {split}', '',
                   table(['Variant','Target','Hits / labelled','Recall','Alarms','Actual rate'],
                         [(r['model'],pct(r['budget']),f"{r['hits']}/{r['positives']}",pct(r['recall']),r['alarms'],pct(r['alarm_rate']))
                          for r in rows if r['split']==split]), '']
    report += ['Lower thresholds trade recall against alarm count; they do not turn unknown alarms into verified false positives. '
               'No representation is promoted automatically. `artifacts/incident-ml-focus/` contains full predictions, tuning, '
               'season/category/status breakdowns and source hashes. Reliable precision assessment still requires independently adjudicated negatives.', '']
    report += ['### Follow-up conclusion', '',
               '**No material improvement at low alarm rates.** At the 2.5% calibration target, the frozen original model '
               'detects 135/341 known positives with 500 actual alarms. At the 5% target it detects 218 with 1,068 alarms. '
               'Low-budget retuning changes that to 221 with 1,085 alarms, a small trade rather than a breakthrough. '
               'Removing absolute context and adding the tested same-sector sequence representation reduce recall at these '
               'operating points. The hypothesis that these context features were the principal problem was not supported '
               'by this ablation. This follow-up does not meet the requested combination of few alarms and very high recall. '
               'Do not deploy any of these variants as a resolved detector.', '']
    (out/'followup.md').write_text('\n'.join(report),encoding='utf-8')
    print(json.dumps(pooled,indent=2),flush=True)


if __name__ == '__main__':
    run()
