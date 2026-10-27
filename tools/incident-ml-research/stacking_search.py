"""Nested event-cross-fitted ensemble; stage-aware branch is a bias diagnostic."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from sklearn.model_selection import GroupKFold
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import RobustScaler
from sklearn.pipeline import make_pipeline
from data import load_dataset, FEATURE_NAMES, sha
from trajectory import prepare, historical_features
from multiple_instance import instance_features, train_hard, instance_scores, bag_scores
from objectives import train, score, calibration_partition, balanced_observation_weights, session_context
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval, SEED
from conditional import goal_passed
from target import BUDGETS


def meta_features(values):
    clipped = np.clip(values, 1e-6, 1.-1e-6)
    return np.log(clipped)-np.log1p(-clipped)


def base_fit_predict(x, shape, records, local, valid, meta, y, fit, targets):
    history = historical_features(records, meta, fit)
    joint = np.hstack([shape, history])
    model_a = train('all-unlabelled', 15, x[fit], y[fit], None)
    model_b = train('all-unlabelled', 15, joint[fit], y[fit], None)
    model_c = train('xgboost', 3, joint[fit], y[fit], None)
    history_sectors = np.concatenate([history[:, :10].reshape(-1, 5, 2),
        np.repeat(history[:, None, 10:], 5, axis=1)], axis=2)
    instances = np.concatenate([local, history_sectors, shape[:, -10:].reshape(-1, 5, 2),
                               np.repeat(x[:, None, :], 5, axis=1)], axis=2)
    model_d = train_hard(instances[fit], valid[fit], y[fit],
                         [meta[i]['known_sectors'] for i in fit], 15, 3, mine=True)
    predictions = np.column_stack([score(model_a, x[targets]), score(model_b, joint[targets]),
        score(model_c, joint[targets]), bag_scores(instance_scores(model_d, instances[targets]),
                                                 valid[targets], 'max')])
    return predictions, [model_a, model_b, model_c, model_d]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-stacking'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, records, _ = prepare(root, meta)
    local, valid = instance_features(x, sectors, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    raw_names = ['excess_seconds_max','log_excess_max','robust_deviation_max','rank_drop_max',
                 'recovery_max','neighbour_contrast_max','profile_spread_max','field_spread_max',
                 'sample_ratio_min','measured_fraction','largest_second_ratio']
    raw = x[:, [FEATURE_NAMES.index(name) for name in raw_names]]
    rows, tuning, predictions, subgroups = [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        groups = np.array([meta[i]['group'] for i in fit])
        oof = np.full((len(fit), 4), np.nan)
        coverage = np.zeros(len(fit), int)
        for fold, (inner, held) in enumerate(GroupKFold(n_splits=3).split(fit, y[fit], groups)):
            assert not set(groups[inner]) & set(groups[held])
            assert not {meta[i]['group'] for i in fit[inner]} & {meta[i]['group'] for i in np.concatenate([cal, test])}
            oof[held], _ = base_fit_predict(x, shape, records, local, valid, meta, y, fit[inner], fit[held])
            coverage[held] += 1
            print(f'{split}: base OOF fold {fold+1}/3 complete', flush=True)
        assert np.all(coverage == 1) and np.isfinite(oof).all()
        targets = np.concatenate([cal, test])
        external, models = base_fit_predict(x, shape, records, local, valid, meta, y, fit, targets)
        np.savez_compressed(out/f'{split}-oof.npz', fit=fit, base_scores=oof, coverage=coverage)
        with (out/f'{split}-base.pickle').open('wb') as handle:
            pickle.dump(models, handle)
        fit_features = np.hstack([meta_features(oof), raw[fit]])
        external_features = np.hstack([meta_features(external), raw[targets]])
        stages, stage_names = session_context(meta, fit)
        views = {'timing-stack': (fit_features, external_features),
                 'stage-stack': (np.hstack([fit_features, stages[fit]]),
                                 np.hstack([external_features, stages[targets]]))}
        for name, (training, unseen) in views.items():
            best = None
            for kind, parameter in [('lightgbm', 7), ('lightgbm', 15), ('logistic', .1),
                                    ('logistic', 1.), ('logistic', 10.)]:
                if kind == 'lightgbm':
                    model = train('all-unlabelled', parameter, training, y[fit], None)
                    values = score(model, unseen)
                else:
                    model = make_pipeline(RobustScaler(), LogisticRegression(C=parameter,
                        max_iter=2000, random_state=SEED))
                    model.fit(training, y[fit], logisticregression__sample_weight=balanced_observation_weights(y[fit]))
                    values = model.predict_proba(unseen)[:, 1]
                threshold = threshold_for_budget(values[:len(cal)], 700/20340)
                objective = float(np.mean(values[:len(cal)][y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, kind=kind, parameter=parameter,
                                   calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, model, values, kind, parameter
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(model=best[1], kind=best[3], parameter=best[4], stage_names=stage_names), handle)
            calibration, values = best[2][:len(cal)], best[2][len(cal):]
            for budget in BUDGETS:
                threshold = threshold_for_budget(calibration, budget)
                flagged = values > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category','status','session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field,
                            value=value, **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value))
                               for i, value in zip(test, values))
        write_csv(out/'metrics.csv', rows)
        write_csv(out/'tuning.csv', tuning)
        print(f'{split}: both stacks evaluated', flush=True)
    pooled = []
    for name in views:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                               recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('pooled', pooled), ('predictions', predictions), ('subgroups', subgroups)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hash=sha(Path(__file__)),
        goal_passed=any(r['goal_passed'] for r in pooled), protected_sources_unchanged=True,
        elapsed_seconds=time.perf_counter()-started, exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Nested cross-fitted stacking', '',
        'Four fixed base recipes: original LightGBM, shape/history LightGBM, shape/history XGBoost, '
        'and contextual hard-MIL with unlabelled-control mining. Three round-group inner folds build '
        'out-of-fold predictions for each outer-fit row exactly once. Inner estimators never see the '
        'held row/event, outer calibration/test labels or calibration/test history. Final base estimators '
        'fit all outer-fit rows, then predict outer calibration/test rows. The meta learner is trained '
        'only on OOF base logits plus 11 measurement summaries. Five meta recipes (two LightGBM leaf '
        'counts and three logistic C values) are selected using outer calibration only. OOF coverage '
        'and group-disjointness are asserted and saved.', '',
        '`timing-stack` excludes stage. `stage-stack` additionally uses fit-only one-hot session stage '
        'known before a run. Stage-aware gains can exploit reporting propensity: 305/341 annotated '
        'positives are finals. They do not prove improved physical incident detection. Unlabelled '
        'controls are not verified clean; no precision/FPR/crash probability is identified. No rider '
        'identity, final outcome or label enters predictors.', '',
        'Same eight outer season tests, grouped outer calibration, five predeclared budgets, no '
        'test-top-700 threshold. Earlier test inspection makes this exploratory, not final independent '
        'validation. No synthetic incidents, database changes or production modifications.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
            [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### Stacking: {split}', '', table(['Model','Target','Hits','Positives','Alarms'],
            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Full OOF matrices, choices and predictions are in "
              '`artifacts/incident-ml-stacking/`. Production remains unchanged.', '']
    (out/'stacking.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700]
    print(json.dumps(dict(best_eligible=max(eligible,key=lambda r:r['hits']) if eligible else None,
                          goal_passed=manifest['goal_passed'], elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
