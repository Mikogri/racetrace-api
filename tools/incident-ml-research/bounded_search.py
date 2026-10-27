"""Asymmetric contaminated-U generalized cross entropy with explicit controls."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from bounded_risk import fit_bounded
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
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-bounded'))
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

    source = json.loads(tracked(parent/'manifest.json').read_text())
    assert source['input_hashes'] == hashes
    for name in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in source[name].items())
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
        with tracked(parent/f'{split}.pickle').open('rb') as handle:
            previous = pickle.load(handle)
        names = sorted({conditions[i] for i in fit})
        assert names == previous['weather_names']
        leaves = previous['models']['quarantine-only']['leaves']
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        models, selected = {}, {'quarantine-reference': reference[split]}
        for name, symmetric, strengths in [('matched-logistic', False, [0.]),
                ('asymmetric-bounded-U', False, [.3, .7]), ('symmetric-bounded', True, [.3, .7])]:
            best = None
            for strength in strengths:
                model = fit_bounded(fitting[eligible], y[eligible], leaves, strength, symmetric)
                values = model.predict(inference, raw_score=True, num_threads=2)
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, strength=strength, symmetric=symmetric,
                    leaves=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, model, strength
            selected[name] = best[1]
            models[name] = dict(model=best[2], strength=best[3], symmetric=symmetric, leaves=leaves)
            selections.append(dict(split=split, model=name, strength=best[3], symmetric=symmetric,
                leaves=leaves, calibration_objective=best[0], eligible_fit_keys=eligible_keys))
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
        print(f'{split}: bounded-U, symmetric and matched logistic fits complete', flush=True)
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
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('bounded_risk.py')]},
        bounded_observed_U_risk=True, selections=selections, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Bounded contaminated-U loss', '',
        'For observed U, generalized cross entropy is (1-c^q)/q, where c is the model '
        'probability of that observed label. q=0 is ordinary log loss. Asymmetric loss retains '
        'ordinary log loss for confirmed P and bounds only U loss, reducing hard-U pressure '
        'without relabelling any U as verified clean/positive. Symmetric loss bounds both '
        'groups as an explicit ablation. q=0.3/0.7 is calibration-selected. A matched q=0 '
        'custom-objective control and frozen strongest reference are retained. Same inputs, '
        'fit quarantine, inherited leaves and 300-tree settings. Gradients are exact; training '
        'uses the positive diagonal surrogate w*e*c^(q+1), omitting the negative nonconvex '
        'curvature term, with 1e-8 floor. Finite-difference tests check the true gradient and '
        'curvature separately. This is an exploratory robust observed-label risk, NOT an '
        'unbiased/identified PU estimator or calibrated crash probability. No original held '
        'labels change; all seasons were inspected previously.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
              [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Bounded-U risk: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-bounded/`. Production unchanged.', '']
    (out/'bounded.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled), flush=True)


if __name__ == '__main__':
    main()
