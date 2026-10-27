"""Fit-only kernel ratio estimation on the strongest causal-history input view."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from density_ratio import prepare_ratio, fit_prepared, ratio_scores
from data import load_dataset, FEATURE_NAMES, sha
from objectives import calibration_partition
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-density-ratio'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder, time_folder = root / 'artifacts/incident-ml-field-factor', root / 'artifacts/incident-ml-rolling-history'
    for source in [factor_folder, time_folder]:
        assert json.loads((source / 'manifest.json').read_text())['input_hashes'] == hashes
    components = {str((factor_folder / 'field-factors.npz').relative_to(root)): sha(factor_folder / 'field-factors.npz')}
    with np.load(factor_folder / 'field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index = {r['key']: i for i, r in enumerate(meta)}
    reference = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = time_folder / filename
        components[str(source.relative_to(root))] = sha(source)
        with source.open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'history-rolling':
                    values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                    i = index[row['key']]
                    assert np.isnan(values[i])
                    values[i] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups = ([] for _ in range(7))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        assert set(np.flatnonzero(np.isfinite(reference[split]))) == set(np.concatenate([cal, test]))
        time_file = time_folder / f'{split}-history.npz'
        components[str(time_file.relative_to(root))] = sha(time_file)
        with np.load(time_file) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        support_base = x[:, [FEATURE_NAMES.index('measured_fraction'), FEATURE_NAMES.index('sector_count')]]
        support_fit, support_eval = np.hstack([support_base, frozen[:, -1:]]), np.hstack([support_base, rolling[:, -1:]])
        selected, models = {'time-reference': (0., reference[split])}, {}
        for name, fit_values, query_values, dimensions in [
                ('ratio-pca12', fitting, inference, 12), ('ratio-full', fitting, inference, None),
                ('support-control', support_fit, support_eval, None)]:
            prepared, transformed = prepare_ratio(fit_values[fit], y[fit], dimensions)
            best = None
            for multiplier in [.5, 1., 2.]:
                for ridge in [.01, .1]:
                    model = fit_prepared(prepared, transformed, y[fit], multiplier, ridge)
                    values = ratio_scores(model, query_values)
                    objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700 / 20340)))
                    tuning.append(dict(split=split, model=name, multiplier=multiplier, ridge=ridge,
                        calibration_recall=objective, kkt_residual=model['kkt_residual']))
                    if best is None or objective > best[0]:
                        best = objective, values, model
            selected[name] = best[:2]
            models[name] = best[2]
            selections.append(dict(split=split, model=name, multiplier=best[2]['multiplier'], ridge=best[2]['ridge'],
                calibration_objective=best[0], landmark_keys=[meta[fit[j]]['key'] for j in best[2]['landmark_rows']]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values) in selected.items():
            control = name in ['time-reference', 'support-control']
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
        print(f'{split}: nonnegative kernel-ratio models complete', flush=True)
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
        [Path(__file__), Path(__file__).with_name('density_ratio.py'), Path(__file__).with_name('aligned_runs.py')]},
        component_source_hashes=components, selections=selections, nonnegative_kernel_ratio=True,
        protected_sources_unchanged=True, causal_unlabelled_history_inputs=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Nonnegative observed-pattern kernel density ratio', '',
        'A different learner fits r(x)=sum alpha_k RBF(x,center_k), alpha>=0, minimizing '
        '0.5*mean_U(r²)-mean_observedP(r)+lambda*||alpha||²/2. U is the contaminated unlabelled '
        'fit population, not a verified clean class. Cholesky converts the strictly regularized '
        'quadratic exactly to NNLS; every fit checks active/inactive KKT residual <=1e-7. '
        'This is an approximate observed-pattern density ratio, NOT a decontaminated incident '
        'posterior, prevalence estimate or calibrated crash probability. Scores need not be <=1.', '',
        'Signed-log, RobustScaler, clipping +/-8 and optional full-SVD PCA(12) fit only fit rows. '
        'Up to 64 centres come only from fit positives. Base bandwidth is median positive pair '
        'distance among at most 512 fit-mixture rows; an all-identical mixture uses a unit bandwidth '
        'and remains tied. Calibration selects bandwidth multipliers .5/1/2 and ridge .01/.1. '
        'PCA/full-space models use the identical strongest causal time/context feature inputs. '
        'A support-only ratio and the frozen time learner are controls. No new dependencies, '
        'GPU, test scaling, test quota or held labels in fitting. Same eight splits/five budgets.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Kernel ratio: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Source selection bias and previously inspected years remain. U is not verified clean; '
              'precision/FPR cannot be inferred. Artifacts stay in `artifacts/incident-ml-density-ratio/`; '
              'production remains unchanged.', '']
    (out / 'density-ratio.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
