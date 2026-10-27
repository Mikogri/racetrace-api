"""Fit-only positive-event robust risk; fixed quarantine/history reference."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv
from conditional import goal_passed
from target import BUDGETS
from event_robust import fit_event_robust


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-event-robust'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    parent = root/'artifacts/incident-ml-reviewed-history'
    history = root/'artifacts/incident-ml-rolling-history'
    components = {}

    def tracked(path):
        components[str(path.relative_to(root))] = sha(path)
        return path

    for folder in [parent, history, root/'artifacts/incident-ml-field-factor']:
        assert json.loads(tracked(folder/'manifest.json').read_text())['input_hashes'] == hashes
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
    y = np.array([r['positive'] for r in meta])
    years, events = np.array([r['year'] for r in meta]), np.array([r['group'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, selections, predictions, calibration = ([] for _ in range(5))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        eligible_keys = next(r['eligible_fit_keys'] for r in usage
                            if r['split'] == split and r['model'] == 'quarantine-only')
        eligible = np.array([index[k] for k in eligible_keys])
        assert set(eligible) <= set(fit) and not set(events[fit]) & set(events[np.r_[cal, test]])
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
        for name, strengths in [('static-positive-events', [0.]), ('soft-worst-events', [1., 5.])]:
            best = None
            for strength in strengths:
                model = fit_event_robust(fitting[eligible], y[eligible], events[eligible], leaves, strength)
                values = model.predict(inference, raw_score=True, num_threads=2)
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, strength=strength, leaves=leaves,
                                   calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, model, strength
            selected[name] = best[1]
            models[name] = dict(model=best[2], strength=best[3], leaves=leaves)
            selections.append(dict(split=split, model=name, strength=best[3], leaves=leaves,
                calibration_objective=best[0], eligible_fit_keys=eligible_keys,
                positive_events=int(len(set(events[eligible][y[eligible]])))))
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
        print(f'{split}: static and soft worst-positive-event fits complete', flush=True)
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
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('event_robust.py')]},
        selections=selections, positive_event_robust_risk=True, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Positive-event soft worst-risk boosting', '',
        'Observed-positive event losses are averaged (strength 0) or soft-max aggregated (strength 1/5). '
        'The U loss remains uniform over contaminated unlabelled runs, not verified clean runs. '
        'Event identifiers only group fit losses, never enter predictor columns. Same strongest causal '
        'inputs and fit-only label quarantine; leaves inherited from its calibration-selected reference. '
        'Strength selection uses calibration recall only. Exact diagonal objective curvature is used '
        'with a training-only 1e-8 floor. Scores are raw margins, not crash probabilities. '
        'All original held targets remain untouched; this is exploratory reuse of inspected years.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
              [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Event risk: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-event-robust/`. Production unchanged.', '']
    (out/'event-robust.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled), flush=True)


if __name__ == '__main__':
    main()
