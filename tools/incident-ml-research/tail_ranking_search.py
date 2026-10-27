"""Low-alarm-tail observed-label ranking on the strongest frozen causal view."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from tail_ranking import fit_tail, tail_scores
from data import load_dataset, sha
from objectives import calibration_partition
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-tail-ranking'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder, history_folder = root / 'artifacts/incident-ml-field-factor', root / 'artifacts/incident-ml-rolling-history'
    for folder in [factor_folder, history_folder]:
        assert json.loads((folder / 'manifest.json').read_text())['input_hashes'] == hashes
    source = factor_folder / 'field-factors.npz'
    components = {str(source.relative_to(root)): sha(source)}
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index, reference = {r['key']: i for i, r in enumerate(meta)}, {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = history_folder / filename
        components[str(source.relative_to(root))] = sha(source)
        with source.open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'history-rolling':
                    values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                    assert np.isnan(values[index[row['key']]])
                    values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups = ([] for _ in range(7))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        source = history_folder / f'{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        selected, models = {'time-reference': (0., reference[split])}, {}
        for name, fraction in [('upper-tail', 700/20340), ('full-pair-control', 1.)]:
            best = None
            for leaves in [7, 15]:
                start = time.perf_counter()
                model = fit_tail(fitting[fit], y[fit], leaves, fraction)
                values = tail_scores(model, inference)
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, leaves=leaves, fraction=fraction,
                    calibration_recall=objective, elapsed_seconds=time.perf_counter()-start,
                    trees=model.num_trees()))
                if best is None or objective > best[0]:
                    best = objective, values, model, leaves
            selected[name], models[name] = best[:2], best[2]
            selections.append(dict(split=split, model=name, leaves=best[3], fraction=fraction,
                                   calibration_objective=best[0]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values) in selected.items():
            control = name != 'upper-tail'
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'session', 'status']:
                    for value in sorted({str(meta[i][field]) for i in test}):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        print(f'{split}: upper-tail/full-pair objectives complete', flush=True)
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
    assert all(sha(root / p) == digest for p, digest in components.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('tail_ranking.py')]}, component_source_hashes=components,
        selections=selections, upper_tail_observed_ranking=True, protected_sources_unchanged=True,
        causal_unlabelled_history_inputs=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Upper-tail observed-label pair ranking', '',
        'The representation is identical to the strongest causal tree. The objective changes to '
        'N/(P*k) sum_P sum_U w_u softplus(score_u-score_p), where the top ceil(700/20340*n_U) '
        'fit U scores have weight 1, and boundary ties share the remaining weight symmetrically. '
        'Tail membership updates on FIT only each boosting iteration. Every observed P contributes. '
        'A full-pair control uses all U. This is observed-label discrimination, not an identified '
        'physical crash risk: U is contaminated, reporting is not SCAR, and penalizing high-score U '
        'may also penalize unreported real incidents.', '',
        'Analytical gradients and diagonal curvature are checked against finite differences away '
        'from cutoff ties. The native LightGBM learner uses the same 300-tree/.025/min-leaf35/L2=5 '
        'settings as the cached cross-entropy reference; leaves 7/15 are calibration-selected. '
        'Inactive U has zero gradient, numerical Hessian floor 1e-8. Training tail fraction is NOT '
        'a test selection/quota: held alarms use calibration-only thresholds and can exceed 700. '
        'No test labels, session status, rider identity, official finish rank/time or stage feature '
        'enters training. The fit-U tail is not labelled verified clean.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Tail ranking: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['All years remain exploratory. No precision/FPR or crash-probability claims. '
              'Artifacts: `artifacts/incident-ml-tail-ranking/`. Production unchanged.', '']
    (out / 'tail-ranking.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
