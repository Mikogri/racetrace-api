"""Same-session factor decomposition with held-season calibration; offline only."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from field_factor import field_delays
from local_density import available_threshold
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from target import BUDGETS
from conditional import goal_passed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-field-factor'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    times = np.full((len(meta), 5), np.nan)
    for i, row in enumerate(meta):
        for j, value in enumerate(row['times']):
            if value and sectors[i][j, 12] == 0 and sectors[i][j, 13] == 0:
                times[i, j] = value
    groups = np.array([r['key'].split('/')[0] + '/' + r['category'] for r in meta])
    configurations = [(rank, penalty) for rank in [1, 2] for penalty in [1.5, 3.]]
    factors = [field_delays(times, groups, rank, penalty) for rank, penalty in configurations]
    direct = []
    for values in factors:
        valid = np.isfinite(values[:, :, 2])
        raw = np.where(valid, values[:, :, 2], -np.inf).max(axis=1)
        raw[~valid.any(axis=1)] = np.nan
        direct.append(raw)
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    availability = (~np.isfinite(factors[0][:, :, 0])).astype(float)
    np.savez_compressed(out / 'field-factors.npz', keys=np.array([r['key'] for r in meta]),
                        **{f'configuration-{j}': f for j, f in enumerate(factors)})
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, selections, predictions, calibration_scores, subgroups = ([] for _ in range(6))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather])
        selected, models = {}, {}
        candidates = []
        for number, values in enumerate(direct):
            threshold = available_threshold(values[cal], 700 / 20340)
            objective = float(np.mean(values[cal][y[cal]] > threshold))
            tuning.append(dict(split=split, model='direct-factor', parameter=number, calibration_recall=objective))
            candidates.append((objective, values, number))
        selected['direct-factor'] = max(candidates, key=lambda item: item[0])
        for name, features in {'factor-only': local, 'factor-context': np.hstack([context, local]),
                               'context-reference': context, 'availability-only': availability}.items():
            best = None
            for leaves in [7, 15]:
                model = train('all-unlabelled', leaves, features[fit], y[fit], years[fit])
                values = score(model, features)
                threshold = threshold_for_budget(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, parameter=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, leaves
                    models[name] = model
            selected[name] = best
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values, parameter) in selected.items():
            control = name in ['context-reference', 'availability-only']
            selections.append(dict(split=split, model=name, parameter=parameter, calibration_objective=objective))
            for budget in BUDGETS:
                threshold = (available_threshold(values[cal], budget) if name == 'direct-factor'
                             else threshold_for_budget(values[cal], budget))
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'session', 'status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'],
                score=float(values[i]) if np.isfinite(values[i]) else '') for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'],
                score=float(values[i]) if np.isfinite(values[i]) else '') for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        print(f'{split}: completed', flush=True)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions),
                          ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('field_factor.py')]}, selections=selections,
        current_session_transductive_context=True, protected_sources_unchanged=True,
        elapsed_seconds=time.perf_counter()-started, exploratory_reuse_of_previously_inspected_years=True,
        configurations=configurations, goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Same-session low-rank tempo and sparse positive delay', '',
        'Within each session/category, log-sector times are centered by field columns and robust row '
        'tempo. A fixed column offset is removed before alternating rank-1/2 SVD and nonnegative '
        'soft-threshold delay for 20 steps. Penalties are 1.5/3 times robust residual scales, floored '
        'at .002 log units. Missing entries use model completion only inside SVD and remain unknown '
        'in exported scores. Fields require eight usable riders and three usable sector columns; '
        'each rider needs two measurements. No labels, names, status or other sessions enter this '
        'retrospective/transductive field fit. It is not a prior-only or live forecasting model.', '',
        'Direct standardized positive residual, learned factor-only features and factor-augmented '
        'paired/weather context use the same eight season/forward tests and five calibration budgets. '
        'Context-reference and availability-only are explicit controls. Calibration selects rank/penalty '
        'or leaves 7/15; test labels never select thresholds or quotas. Unknown density/field scores '
        'count in the whole population. Decomposed losses are not validated counterfactual corrected '
        'race times or crash probabilities. U remains contaminated, not verified clean.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    lines += ['All scores, subgroups, models and source hashes remain in `artifacts/incident-ml-field-factor/`. '
              'Production is unchanged; previously inspected years make this exploratory.', '']
    (out / 'field-factor.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds'])), flush=True)


if __name__ == '__main__':
    main()
