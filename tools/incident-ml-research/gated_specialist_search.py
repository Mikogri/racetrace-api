"""Matched-component gate versus max: no new expert fitting or leaf selection."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from gated_specialists import gated_scores
from positive_specialists import profiles
from data import load_dataset, sha
from objectives import calibration_partition
from field_objectives import percentile_scores
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-gated-specialists'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, availability, measured = profiles(sectors)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    base = root / 'artifacts/incident-ml-specialists'
    original = json.loads((base / 'manifest.json').read_text(encoding='utf-8'))
    assert original['input_hashes'] == hashes
    for field in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root / path) == digest for path, digest in original[field].items())
    inherited = json.loads((base / 'verification.json').read_text(encoding='utf-8'))
    assert inherited['specialist_ensembles_rebuilt'] == 16 and inherited['changed_specialist_decisions'] == 0
    assert inherited['maximum_specialist_component_difference'] <= 1e-10
    components = {str((base / name).relative_to(root)): sha(base / name)
                  for name in ['manifest.json', 'verification.json', 'predictions.csv', 'calibration-scores.csv']}
    source = root / 'artifacts/incident-ml-field-factor/field-factors.npz'
    components[str(source.relative_to(root))] = sha(source)
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index, reference = {r['key']: i for i, r in enumerate(meta)}, {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        with (base / filename).open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                values = reference.setdefault((row['split'], row['model']), np.full(len(meta), np.nan))
                assert np.isnan(values[index[row['key']]])
                values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, predictions, calibration_scores, subgroups, diagnostics = ([] for _ in range(6))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        source = root / f'artifacts/incident-ml-rolling-history/{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            rolling = pack['rolling']
        source = base / f'{split}.pickle'
        components[str(source.relative_to(root))] = sha(source)
        with source.open('rb') as handle:
            saved = pickle.load(handle)
        assert saved['weather_names'] == sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
        inference = np.hstack([x, paired, weather, local, rolling])
        selected = {'quarantine-reference': reference[split, 'quarantine-reference']}
        for original_name, prefix, query in [('shape-specialists', 'shape', shape),
                                             ('availability-specialists', 'availability', availability)]:
            state = saved['models'][original_name]
            raw = [model.predict(inference, num_threads=2) for model in state['models']]
            for calibration, values in zip(state['calibrations'], raw):
                np.testing.assert_array_equal(calibration, values[cal])
            ranks = np.vstack([percentile_scores(c, v) for c, v in zip(state['calibrations'], raw)])
            maximum = np.max(ranks, axis=0)
            inherited_values = reference[split, original_name]
            np.testing.assert_array_equal(maximum[np.r_[cal, test]], inherited_values[np.r_[cal, test]])
            gated = gated_scores(state['partition'], query, measured, ranks)
            assert np.all(gated <= maximum+1e-12)
            selected[prefix+'-max-control'], selected[prefix+'-gated'] = maximum, gated
            diagnostics.append(dict(split=split, view=prefix, experts=len(raw),
                test_low_support_fallbacks=int(np.sum(measured[test] < 2)),
                test_scores_changed_by_routing=int(np.sum(gated[test] != maximum[test]))))
        for name, values in selected.items():
            control = name != 'shape-gated'
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
        print(f'{split}: frozen specialist routing complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    write_csv(out / 'routing-diagnostics.csv', diagnostics)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / path) == digest for path, digest in protected.items())
    assert all(sha(root / path) == digest for path, digest in components.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(path.relative_to(root)): sha(path) for path in
        [Path(__file__), Path(__file__).with_name('gated_specialists.py'), Path(__file__).with_name('positive_specialists.py'),
         Path(__file__).with_name('field_objectives.py')]}, component_source_hashes=components,
        frozen_specialist_routing=True, new_models_fitted=0, inherited_component_count=inherited['specialist_components_retrained'],
        inherited_leaf_selection='calibration-selected for original max pooling; not retuned for gate',
        protected_sources_unchanged=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Frozen-component specialist routing', '',
        'This matched-component ablation changes only pooling. A measured query profile with at least '
        'two valid sectors is routed to the nearest fit-positive KMeans centre using its frozen '
        'RobustScaler and +/-4 clipping. The selected expert supplies its calibration-CDF rank. '
        'Low-support rows retain the old maximum pooling rule; a single cluster needs no routing. '
        'No query labels, refitting, test CDF, test quota or new model/leaf selection is used. '
        'Leaves were selected earlier for max pooling and are deliberately NOT retuned for routing.', '',
        'The exact same saved components and calibration arrays serve max controls and gates. '
        'The mask-only gate is an availability control. Component fit-only provenance and repeat '
        'training were established in the base study; this experiment fits zero new experts. '
        'A change cannot be attributed to a different classifier or newly selected fit data. '
        'Scores remain observed-pattern ranks, not physical crash probabilities; all years are exploratory.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Frozen routing: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-gated-specialists/`. Production unchanged.', '']
    (out / 'gated-specialists.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
