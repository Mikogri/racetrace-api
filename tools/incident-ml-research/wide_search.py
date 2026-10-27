"""Two-stage predeclared random search; outer tests never select configurations."""
import argparse
import json
import pickle
import random
import time
from pathlib import Path
import numpy as np
from lightgbm import LGBMClassifier
from data import load_dataset,sha
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from objectives import calibration_partition
from hybrid_search import training_weights
from experiment import SEED,evaluation_splits,threshold_for_budget,metrics,write_csv,cluster_interval
from conditional import goal_passed
from target import BUDGETS


def candidate_configs(seed,count):
    rng=random.Random(seed)
    choices=dict(num_leaves=[3,7,15,31,63],min_child_samples=[5,15,35,75],
        learning_rate=[.03,.06,.1],reg_lambda=[0.,1.,5.,20.],reg_alpha=[0.,.5,2.],
        colsample_bytree=[.6,.8,1.],subsample=[.7,.9,1.],positive_mass=[.25,.5,.75],
        hard_multiplier=[1.,3.,10.])
    result,seen=[],set()
    while len(result)<count:
        config={k:rng.choice(v) for k,v in choices.items()}
        key=tuple(sorted(config.items()))
        if key not in seen:
            result.append(config);seen.add(key)
    return result


def fit_model(features,y,hard,indices,config,rounds):
    parameters=config.copy()
    mass=parameters.pop('positive_mass')
    multiplier=parameters.pop('hard_multiplier')
    weights=training_weights(y[indices],hard[indices],multiplier)
    weights[y[indices]]*=2*mass
    weights[~y[indices]]*=2*(1.-mass)
    model=LGBMClassifier(n_estimators=rounds,n_jobs=2,verbosity=-1,force_col_wise=True,
        deterministic=True,random_state=SEED,subsample_freq=1,**parameters)
    model.fit(features[indices],y[indices].astype(int),sample_weight=weights)
    return model


def calibration_objective(values,positive):
    return float(np.mean([np.mean(values[positive]>threshold_for_budget(values,b))
                          for b in [.025,.03,700/20340]]))


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--documents',type=Path,required=True)
    parser.add_argument('--output',type=Path,default=Path('artifacts/incident-ml-wide'))
    args=parser.parse_args()
    root,out=Path(__file__).resolve().parents[2],args.output.resolve()
    out.mkdir(parents=True,exist_ok=True)
    started=time.perf_counter()
    x,meta,sectors,hashes=load_dataset(root,args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired,_=prepare_pairs(root,meta,enhanced=True)
    speed,conditions=prepare_environment(root,meta)
    flags=np.array([[float(r['production']),float(r['current']),float(r['high_recall']),
        r['production_score'],r['current_score'],r['high_recall_score']] for r in meta])
    hard=flags[:,:3].max(axis=1)>0
    y=np.array([r['positive'] for r in meta]);years=np.array([r['year'] for r in meta])
    baseline=np.array([r['production'] for r in meta])
    configs=candidate_configs(SEED,48)
    rows,tuning,predictions,calibration_scores,subgroups,selections=[],[],[],[],[],[]
    for split,outer,test in evaluation_splits(years):
        fit,cal=calibration_partition(outer,years,meta,y,'grouped')
        assert not {meta[i]['group'] for i in fit}&{meta[i]['group'] for i in np.concatenate([cal,test])}
        weather_names=sorted({conditions[i] for i in fit})
        weather=np.array([[float(c==name) for name in weather_names] for c in conditions])
        features=np.hstack([x,paired,speed,flags,weather])
        rng=np.random.default_rng(SEED)
        positives=fit[y[fit]];unknown=fit[~y[fit]]
        screen=np.concatenate([positives,rng.choice(unknown,min(6000,len(unknown)),replace=False)])
        ranked=[]
        for trial,config in enumerate(configs):
            model=fit_model(features,y,hard,screen,config,100)
            values=model.booster_.predict(features[cal])
            objective=calibration_objective(values,y[cal])
            ranked.append((objective,trial))
            tuning.append(dict(split=split,stage='screen',trial=trial,rounds=100,objective=objective,
                               parameters=json.dumps(config,sort_keys=True)))
            if (trial+1)%12==0:
                print(f'{split}: screened {trial+1}/48 configurations',flush=True)
        best=None
        # Ties prefer earlier predeclared grid entries, not test results.
        for _,trial in sorted(ranked,key=lambda r:(-r[0],r[1]))[:4]:
            for rounds in [100,300,600]:
                model=fit_model(features,y,hard,fit,configs[trial],rounds)
                values=model.booster_.predict(features[cal])
                objective=calibration_objective(values,y[cal])
                tuning.append(dict(split=split,stage='full-refit',trial=trial,rounds=rounds,objective=objective,
                                   parameters=json.dumps(configs[trial],sort_keys=True)))
                if best is None or objective>best[0]:best=objective,model,trial,rounds
        model=best[1];values=model.booster_.predict(features)
        with (out/f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(model=model,parameters=configs[best[2]],rounds=best[3],weather_names=weather_names),handle)
        selections.append(dict(split=split,trial=best[2],rounds=best[3],calibration_objective=best[0],
                               fit_rows=len(fit),calibration_rows=len(cal),calibration_positives=int(y[cal].sum())))
        name='wide-no-stage'
        for budget in BUDGETS:
            threshold=threshold_for_budget(values[cal],budget)
            flagged=values[test]>threshold
            rows.append(dict(split=split,model=name,budget=budget,threshold=threshold,
                **metrics(y[test],flagged,baseline[test]),recall_interval=cluster_interval(meta,test,flagged)))
            for field in ['category','status','session','paired_reference']:
                for value in sorted(set(str(meta[i][field]) for i in test)):
                    take=np.array([str(meta[i][field])==value for i in test])
                    subgroups.append(dict(split=split,model=name,budget=budget,field=field,value=value,
                        **metrics(y[test][take],flagged[take],baseline[test][take])))
        predictions.extend(dict(split=split,model=name,key=meta[i]['key'],score=float(values[i])) for i in test)
        calibration_scores.extend(dict(split=split,model=name,key=meta[i]['key'],score=float(values[i])) for i in cal)
        write_csv(out/'metrics.csv',rows);write_csv(out/'tuning.csv',tuning)
        print(f'{split}: full-data refinement and outer evaluation complete',flush=True)
    pooled=[]
    for budget in BUDGETS:
        take=[r for r in rows if r['split'].startswith('leave-') and r['budget']==budget]
        hits,alarms=sum(r['hits'] for r in take),sum(r['alarms'] for r in take)
        pooled.append(dict(model='wide-no-stage',budget=budget,hits=hits,positives=int(y.sum()),alarms=alarms,
            recall=hits/int(y.sum()),goal_passed=goal_passed(hits,int(y.sum()),alarms)))
    for name,content in [('pooled',pooled),('predictions',predictions),('calibration-scores',calibration_scores),('subgroups',subgroups)]:
        write_csv(out/f'{name}.csv',content)
    protected=json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p)==digest for p,digest in protected.items())
    manifest=dict(input_hashes=hashes,source_hash=sha(Path(__file__)),grid=configs,selections=selections,
        protected_sources_unchanged=True,elapsed_seconds=time.perf_counter()-started,
        goal_passed=any(r['goal_passed'] for r in pooled),exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    from report import table,pct
    lines=['## Wide combined-feature hyperparameter search', '',
        'A fixed seed declares 48 distinct LightGBM configurations before fitting. Dimensions include '
        'leaves 3/7/15/31/63, minimum leaf samples 5/15/35/75, learning rate .03/.06/.1, L2 0/1/5/20, '
        'L1 0/.5/2, column fraction .6/.8/1, row fraction .7/.9/1, observed-positive weight mass '
        '.25/.5/.75, and suspicious-unlabelled multiplier 1/3/10. Screening uses 100 rounds on all '
        'fit positives and a deterministic sample of up to 6,000 fit unlabelled rows. The top four '
        'calibration configurations are retrained on all fit rows for 100/300/600 rounds.', '',
        'Selection objective is mean known-positive calibration recall at the predeclared 2.5%,3%, '
        '700/20,340 targets. Outer test labels/scores never select parameters, training length or '
        'thresholds. Repeated calibration search can overfit its small positive set; outer tests '
        'remain exploratory because these years were inspected in earlier research.', '',
        'Combined inputs: original measurements, enhanced earlier-session paired profiles, normalized '
        'speed, dynamic fit-only round-condition encoding and frozen C# heuristic predictions. There '
        'is no session label, rider ID, event ID or final outcome predictor. These inputs can still '
        'encode selection/reporting effects; no precision, FPR or calibrated crash probability is '
        'identified from positive-only labels. Fixed C# rules were previously developed on these years.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
            [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### Wide search: {split}', '',table(['Target','Hits','Positives','Alarms'],
            [(pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Full choices, final calibration scores and "
              'test predictions are in `artifacts/incident-ml-wide/`. Production remains unchanged.', '']
    (out/'wide.md').write_text('\n'.join(lines),encoding='utf-8')
    eligible=[r for r in pooled if r['alarms']<=700]
    print(json.dumps(dict(best_eligible=max(eligible,key=lambda r:r['hits']) if eligible else None,
        goal_passed=manifest['goal_passed'],elapsed_seconds=manifest['elapsed_seconds']),indent=2),flush=True)


if __name__=='__main__':main()
