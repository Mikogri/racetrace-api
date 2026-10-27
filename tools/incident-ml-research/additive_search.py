"""Continuous additive risk versus linear and frozen tree controls."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from threadpoolctl import threadpool_limits
from additive_risk import fit_basis, transform_basis, fit_additive
from data import load_dataset, sha
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-additive'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    parent, history = root/'artifacts/incident-ml-reviewed-history', root/'artifacts/incident-ml-rolling-history'
    components = {}

    def tracked(path):
        components[str(path.relative_to(root))] = sha(path)
        return path

    parent_manifest = json.loads(tracked(parent/'manifest.json').read_text())
    assert parent_manifest['input_hashes'] == hashes
    for field in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in parent_manifest[field].items())
    proof = json.loads(tracked(parent/'verification.json').read_text())
    assert proof['original_held_targets_unchanged'] and proof['changed_reviewed_history_decisions'] == 0
    usage = json.loads(tracked(parent/'review-usage.json').read_text())
    index = {r['key']: i for i, r in enumerate(meta)}
    with np.load(tracked(root/'artifacts/incident-ml-field-factor/field-factors.npz')) as pack:
        assert pack['keys'].tolist() == list(index)
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
        (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    reference = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        with tracked(parent/filename).open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'quarantine-only':
                    values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                    assert np.isnan(values[index[row['key']]])
                    values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, selections, predictions, calibration = ([] for _ in range(5))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        eligible_keys = next(r['eligible_fit_keys'] for r in usage
                            if r['split'] == split and r['model'] == 'quarantine-only')
        eligible = np.array([index[k] for k in eligible_keys])
        assert set(eligible) <= set(fit) and not set(eligible) & set(np.r_[cal, test])
        with np.load(tracked(history/f'{split}-history.npz')) as pack:
            assert pack['keys'].tolist() == list(index)
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        models, selected = {}, {'quarantine-reference': reference[split]}
        for name, nonlinear, strong in [('linear-strong', False, True),
                                      ('additive-strong', True, True), ('additive-local', True, False)]:
            f, z = (fitting, inference) if strong else (x, x)
            basis = fit_basis(f[eligible], nonlinear)
            train_x, predict_x = transform_basis(f[eligible], basis), transform_basis(z, basis)
            best = None
            for regularization in [.001, .01, .1]:
                model, diagnostics = fit_additive(train_x, y[eligible], regularization)
                with threadpool_limits(limits=2):
                    values = model.decision_function(predict_x)
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, regularization=regularization,
                    calibration_recall=objective, basis_columns=train_x.shape[1], **diagnostics))
                write_csv(out/'tuning.csv', tuning)
                print(f'{split}/{name}: C={regularization:g}, {diagnostics}', flush=True)
                if diagnostics['convergence_warnings'] == 0 and (best is None or objective > best[0]):
                    best = objective, values, model, regularization, diagnostics
            assert best is not None, (split, name, 'No converged candidate')
            selected[name] = best[1]
            models[name] = dict(model=best[2], basis=basis, regularization=best[3],
                                nonlinear=nonlinear, strong=strong)
            selections.append(dict(split=split, model=name, regularization=best[3], nonlinear=nonlinear,
                strong=strong, calibration_objective=best[0], eligible_fit_keys=eligible_keys, **best[4]))
        with (out/f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, values in selected.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                rows.append(dict(split=split, model=name, control=name == 'quarantine-reference',
                    budget=budget, threshold=threshold, **metrics(y[test], values[test] > threshold, baseline[test])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out/'metrics.csv', rows)
        write_csv(out/'tuning.csv', tuning)
        print(f'{split}: linear/additive causal and local fits complete', flush=True)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    assert all(sha(root/p) == digest for p, digest in components.items())
    manifest = dict(input_hashes=hashes, component_source_hashes=components,
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('additive_risk.py')]},
        continuous_additive_risk=True, selections=selections, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Continuous additive observed-label risk', '',
        'Signed-log features use fit-only median and max(10–90% range, standard deviation, 1e-6) scaling. '
        'Continuous hinge knots are fit 25/50/75% quantiles, unique and strictly interior; '
        'binary columns remain linear and fit-constant columns are omitted. The predictor sums '
        'univariate piecewise-linear contributions without interactions, and extrapolates linearly '
        'in signed-log space beyond terminal knots. Logistic L2 regularization C=0.001/0.01/0.1 '
        'is calibration-selected among converged fits only. All candidate warnings/iterations '
        'are retained. This is balanced observed-positive versus contaminated-U loss, not clean '
        'labels or a calibrated crash probability. Same strongest causal inputs/quarantine are '
        'used; a linear strong-input and local-only additive ablation are retained. '
        'Basis fitting never sees calibration/test measurements. All original held labels remain; '
        'no identifiers, status or official finish fields are predictor columns. '
        'Years have been inspected previously: results are exploratory. The initial q90-q10-only '
        'scaling pilot failed convergence on all three first-fold linear fits; its failure is '
        'retained in unconverged-pilot.json and is not a benchmark.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
              [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Additive risk: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-additive/`. Production unchanged.', '']
    (out/'additive.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled), flush=True)


if __name__ == '__main__':
    main()
