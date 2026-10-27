"""Frozen C# heuristic outputs plus ML; hard unlabelled controls are not clean."""
import argparse
import itertools
import json
import pickle
import time
from pathlib import Path
import numpy as np
from lightgbm import LGBMClassifier
from data import load_dataset, sha
from trajectory import prepare as prepare_history, historical_features
from paired_sessions import prepare as prepare_pairs
from objectives import balanced_observation_weights, calibration_partition
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def training_weights(y, hard, multiplier):
    if not np.isfinite(multiplier) or multiplier < 1:
        raise ValueError('The control multiplier must be finite and at least one')
    weights = balanced_observation_weights(y)
    weights[(~y) & hard] *= multiplier
    weights[~y] *= weights[y].sum()/weights[~y].sum()
    return weights


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--calibration', choices=['latest','grouped'], default='grouped')
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-hybrid'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root,args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, records, _ = prepare_history(root,meta)
    paired, availability = prepare_pairs(root,meta,enhanced=True)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    flag_features = np.array([[float(r['production']),float(r['current']),float(r['high_recall']),
        r['production_score'],r['current_score'],r['high_recall_score'],
        sum([r['production'],r['current'],r['high_recall']])] for r in meta])
    hard = flag_features[:,:3].max(axis=1)>0
    rows, predictions, tuning, groups = [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer,years,meta,y,args.calibration)
        history = historical_features(records,meta,fit)
        views = {'physics-hybrid':np.hstack([x,paired,flag_features]),
                 'history-hybrid':np.hstack([x,shape,history,flag_features]),
                 'flags-only':flag_features}
        for name, features in views.items():
            best = None
            for leaves,multiplier in itertools.product([7,15],[1.,3.,10.]):
                weights = training_weights(y[fit],hard[fit],multiplier)
                model = LGBMClassifier(n_estimators=250,num_leaves=leaves,learning_rate=.035,
                    min_child_samples=35,reg_lambda=3.,random_state=SEED,n_jobs=2,
                    deterministic=True,force_col_wise=True,verbosity=-1)
                model.fit(features[fit],y[fit].astype(int),sample_weight=weights)
                calibration = model.booster_.predict(features[cal])
                threshold = threshold_for_budget(calibration,700/20340)
                objective = float(np.mean(calibration[y[cal]]>threshold))
                tuning.append(dict(split=split,model=name,leaves=leaves,hard_control_multiplier=multiplier,
                                   calibration_recall=objective))
                if best is None or objective>best[0]:
                    best = objective,model,leaves,multiplier
            values = best[1].booster_.predict(features)
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(model=best[1],leaves=best[2],hard_control_multiplier=best[3]),handle)
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal],budget)
                flagged = values[test]>threshold
                rows.append(dict(split=split,model=name,budget=budget,threshold=threshold,
                    **metrics(y[test],flagged,baseline[test]),recall_interval=cluster_interval(meta,test,flagged)))
                for field in ['category','status','session','paired_reference']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field])==value for i in test])
                        groups.append(dict(split=split,model=name,budget=budget,field=field,value=value,
                            **metrics(y[test][take],flagged[take],baseline[test][take])))
            predictions.extend(dict(split=split,model=name,key=meta[i]['key'],score=float(values[i])) for i in test)
            print(f'{split}: {name}, 6 configurations evaluated',flush=True)
        write_csv(out/'metrics.csv',rows)
        write_csv(out/'tuning.csv',tuning)
    pooled=[]
    for name in views:
        for budget in BUDGETS:
            take=[r for r in rows if r['split'].startswith('leave-') and r['model']==name and r['budget']==budget]
            hits,alarms=sum(r['hits'] for r in take),sum(r['alarms'] for r in take)
            pooled.append(dict(model=name,budget=budget,hits=hits,positives=int(y.sum()),alarms=alarms,
                               recall=hits/int(y.sum()),goal_passed=goal_passed(hits,int(y.sum()),alarms)))
    for name,values in [('pooled',pooled),('predictions',predictions),('subgroups',groups)]:
        write_csv(out/f'{name}.csv',values)
    protected=json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p)==digest for p,digest in protected.items())
    manifest=dict(input_hashes=hashes,source_hash=sha(Path(__file__)),calibration=args.calibration,
        protected_sources_unchanged=True,elapsed_seconds=time.perf_counter()-started,
        goal_passed=any(r['goal_passed'] for r in pooled),exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    from report import table,pct
    lines=['## Fixed-heuristic hybrid with focused unlabelled controls', '',
        'Inputs add frozen production/current/high-recall C# flags and deviation scores, plus agreement '
        'count. These are heuristic predictions, not manual incident labels. Their rules were developed '
        'on these historical years, so this cannot be claimed as independent validation of the heuristics. '
        '`flags-only` is a control; `physics-hybrid` adds original timing and enhanced same-round paired '
        'profiles; `history-hybrid` adds original timing and fit-sourced earlier-round profile features. '
        'No stage label, rider identity or final outcome is used as an ML predictor.', '',
        'LightGBM leaves 7/15 and hard-control multipliers 1/3/10 are selected on calibration only. '
        'Unlabelled controls flagged by any fixed heuristic receive extra weight; total positive and '
        'unlabelled mass remains balanced. This deliberately changes the contaminated observation-ranking '
        'objective, not verified-clean risk; no crash probabilities or FPR are identified. '
        f'Calibration policy: {args.calibration}. Same eight season tests, five predeclared operating '
        'targets and no test quota. All studies remain exploratory on previously inspected years.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
            [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### Hybrid: {split}', '',table(['Model','Target','Hits','Positives','Alarms'],
            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Full choices, scores and subgroup counts are in "
              f'`{out.relative_to(root)}`. Production code remains unchanged.', '']
    (out/'hybrid.md').write_text('\n'.join(lines),encoding='utf-8')
    eligible=[r for r in pooled if r['alarms']<=700]
    print(json.dumps(dict(best_eligible=max(eligible,key=lambda r:r['hits']) if eligible else None,
                         goal_passed=manifest['goal_passed'],elapsed_seconds=manifest['elapsed_seconds']),indent=2),flush=True)


if __name__=='__main__':
    main()
