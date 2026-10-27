"""Ablation of derived finish checkpoint changes, without official outcome fields."""
import argparse
import json
import pickle
from pathlib import Path
import numpy as np
from finish_change import movements, summarize
from data import load_dataset, sha
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-finish-change'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    times = np.full((len(meta), 5), np.nan)
    for i, row in enumerate(meta):
        for j, time in enumerate(row['times']):
            if time and sectors[i][j, 12] == 0 and sectors[i][j, 13] == 0:
                times[i, j] = time
    groups = np.array([r['key'].split('/')[0] + '/' + r['category'] for r in meta])
    intermediate, full = np.zeros((len(meta), 5, 7)), np.zeros((len(meta), 5, 7))
    finish_indices = np.zeros(len(meta), int)
    for group in np.unique(groups):
        take = np.flatnonzero(groups == group)
        finish = max(len(sectors[i]) for i in take)-1
        if finish < 0:
            continue
        finish_indices[take] = finish
        intermediate[take] = movements(times[take], finish, False)
        full[take] = movements(times[take], finish, True)
    mid_features, full_features = summarize(intermediate), summarize(full)
    support = full[:, :, [3, 6]].reshape(len(meta), -1)
    np.savez_compressed(out / 'checkpoint-features.npz', keys=np.array([r['key'] for r in meta]),
        times=times, groups=groups, finish=finish_indices, intermediate=intermediate, full=full)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder = root / 'artifacts/incident-ml-field-factor'
    assert json.loads((factor_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    factor_hash = sha(factor_folder / 'field-factors.npz')
    with np.load(factor_folder / 'field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    factor_features = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                           (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, predictions, calibration_scores, subgroups, selections = ([] for _ in range(7))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, factor_features])
        selected, models = {}, {}
        views = {'intermediate-context': np.hstack([context, mid_features]),
                 'all-checkpoints-context': np.hstack([context, full_features]),
                 'movements-only': full_features, 'support-only': support, 'context-reference': context}
        for name, values in views.items():
            best = None
            for leaves in [7, 15]:
                model = train('all-unlabelled', leaves, values[fit], y[fit], years[fit])
                values_score = score(model, values)
                threshold = threshold_for_budget(values_score[cal], 700 / 20340)
                objective = float(np.mean(values_score[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, leaves=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values_score, leaves
                    models[name] = model
            selected[name] = best
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values_score, leaves) in selected.items():
            control = name in ['intermediate-context', 'support-only', 'context-reference']
            selections.append(dict(split=split, model=name, leaves=leaves, calibration_objective=objective))
            for budget in BUDGETS:
                threshold = threshold_for_budget(values_score[cal], budget)
                flagged = values_score[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted({str(meta[i][field]) for i in test}):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values_score[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values_score[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        print(f'{split}: checkpoint-change ablation complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert sha(factor_folder / 'field-factors.npz') == factor_hash
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('finish_change.py'), Path(__file__).with_name('checkpoint_dynamics.py')]},
        component_source_hashes={str((factor_folder / 'field-factors.npz').relative_to(root)): factor_hash},
        selections=selections, derived_finish_changes=True, post_finish_only=True,
        protected_sources_unchanged=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Derived finish-checkpoint movement ablation', '',
        'The earlier checkpoint study intentionally suppressed movement at the final checkpoint. '
        'This separate after-run experiment derives rank CHANGE and gap CHANGE through that checkpoint '
        'from validated raw sector measurements, not official finish-rank/time/status fields. Absolute '
        'prior/final rank and absolute cumulative finish time/gap are not exported as columns. '
        'These changes still contain finish-related physical information and are explicitly tagged; '
        'they cannot be used before the final sector is measured. No later session or incident label '
        'enters the feature computation. An event-completing cohort is fixed before calculating both '
        'ranks, so missing/DNF arrivals do not manufacture movement. A missing prefix invalidates '
        'later cumulative movement rather than filling it with zero time.', '',
        'Intermediate-context and all-checkpoints-context differ only in the finish-change slots. '
        'Both use the same scored-run cohort and quality mask; they are not claimed identical to '
        'the older broad-cohort checkpoint experiment. Movement-only, support-only and unchanged '
        'factor/context are separate controls. Leaves 7/15 use calibration-only selection; all '
        'eight season/forward checks, five budgets and full 20,340/341 evaluation stay unchanged.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Derived checkpoint changes: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Previously inspected years remain exploratory; U is not verified clean. No precision '
              'or crash probabilities are claimed. Production remains unchanged. Artifacts stay in '
              '`artifacts/incident-ml-finish-change/`.', '']
    (out / 'finish-change.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
