"""Replay retained partial tail models and prove the 307-hit target impossible."""
import argparse
import csv
import json
import math
import pickle
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from objectives import calibration_partition
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv
from tail_ranking import tail_scores


def read(path):
    with path.open(encoding='utf-8') as handle:
        return list(csv.DictReader(handle))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    folder = root / 'artifacts/incident-ml-tail-ranking-pruned'
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    assert int(y.sum()) == 341
    baseline, index = np.array([r['production'] for r in meta]), {r['key']: i for i, r in enumerate(meta)}
    rows, tuning = read(folder / 'metrics.csv'), read(folder / 'tuning.csv')
    splits = sorted({r['split'] for r in rows})
    assert all(name.startswith('leave-') for name in splits) and len(splits) < 6
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    source = root / 'artifacts/incident-ml-field-factor/field-factors.npz'
    components = {str(source.relative_to(root)): sha(source)}
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    reference = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = root / 'artifacts/incident-ml-rolling-history' / filename
        components[str(source.relative_to(root))] = sha(source)
        for row in read(source):
            if row['model'] == 'history-rolling':
                values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                values[index[row['key']]] = float(row['score'])
    exported, replayed, models = [], 0, 0
    for split, outer, test in evaluation_splits(years):
        if split not in splits:
            continue
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        source = root / f'artifacts/incident-ml-rolling-history/{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            rolling = pack['rolling']
        with (folder / f'{split}.pickle').open('rb') as handle:
            saved = pickle.load(handle)
        assert saved['weather_names'] == sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
        inference = np.hstack([x, paired, weather, local, rolling])
        for name in ['time-reference', 'upper-tail', 'full-pair-control']:
            values = reference[split] if name == 'time-reference' else tail_scores(saved['models'][name], inference)
            if name != 'time-reference':
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                best = max((r for r in tuning if r['split'] == split and r['model'] == name),
                           key=lambda r: float(r['calibration_recall']))
                assert math.isclose(objective, float(best['calibration_recall']), abs_tol=1e-12)
                models += 1
            for row in [r for r in rows if r['split'] == split and r['model'] == name]:
                threshold = threshold_for_budget(values[cal], float(row['budget']))
                assert math.isclose(threshold, float(row['threshold']), abs_tol=1e-12)
                observed = metrics(y[test], values[test] > threshold, baseline[test])
                for key, value in observed.items():
                    assert (not row[key]) if value is None else math.isclose(float(row[key]), value, abs_tol=1e-12)
                replayed += 1
            exported.extend(dict(split=split, model=name, role=role, key=meta[i]['key'], score=float(values[i]))
                            for role, take in [('calibration', cal), ('test', test)] for i in take)
    bounds = []
    for budget in sorted({float(r['budget']) for r in rows}):
        take = [r for r in rows if r['model'] == 'upper-tail' and float(r['budget']) == budget]
        assert len(take) == len(splits)
        hits, positives = sum(int(r['hits']) for r in take), sum(int(r['positives']) for r in take)
        bounds.append(dict(budget=budget, completed_hits=hits, completed_positives=positives,
            completed_alarms=sum(int(r['alarms']) for r in take), remaining_positives=341-positives,
            optimistic_final_hits=hits+341-positives))
    assert max(r['optimistic_final_hits'] for r in bounds) < 307
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / path) == digest for path, digest in protected.items())
    source_hashes = {str(path.relative_to(root)): sha(path) for path in
        [Path(__file__), Path(__file__).with_name('tail_ranking.py'), Path(__file__).with_name('tail_ranking_search.py')]}
    native_hashes = {path.name: sha(path) for path in folder.glob('*.pickle')}
    write_csv(folder / 'replayed-partial-scores.csv', exported)
    proof = dict(input_hashes=hashes, source_hashes=source_hashes, component_source_hashes=components,
        native_model_hashes=native_hashes, completed_splits=splits, replayed_operating_points=replayed,
        native_models_replayed=models, models_retrained=0, bounds=bounds, whole_study_finished=False,
        forward_tests_completed=False, goal_passed=False, protected_sources_unchanged=True,
        reason='Stopped after target impossibility; even perfect recall on remaining original positives cannot reach 307.',
        no_threshold_or_model_adjustment_from_stopping=True)
    (folder / 'partial-verification.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
    from report import table
    lines = ['## Pruned upper-tail ranking trial (incomplete study)', '',
        'Training was deliberately stopped after the retained leave-year tests made the target mathematically '
        'unattainable at EVERY predeclared operating point, even assuming perfect detection of all remaining '
        'original positives. This is a negative stopping proof, not a complete six-year/forward evaluation '
        'and not a claim that the partial method improves the incumbent. No threshold or model was adjusted '
        'from this stopping decision. The partial trial is excluded from pooled study rankings.', '',
        f'Replayed {replayed} operating points from {models} saved native models and frozen controls, '
        'including calibration-only thresholds and full original test labels for completed years. '
        'No models were retrained in this partial audit; chronological 2025/2026 tests were not run.', '',
        table(['Target fraction', 'Completed hits', 'Completed P', 'Remaining P', 'Optimistic final hits'],
            [(r['budget'], r['completed_hits'], r['completed_positives'], r['remaining_positives'],
              r['optimistic_final_hits']) for r in bounds]), '',
        'Artifacts and original outputs remain in `artifacts/incident-ml-tail-ranking-pruned/`. '
        'Source hashes, native hashes and replayed partial scores are retained. Production unchanged.', '']
    (folder / 'tail-ranking-pruned.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(dict(completed_splits=splits, replayed_operating_points=replayed,
        native_models_replayed=models, optimistic_best=max(r['optimistic_final_hits'] for r in bounds),
        goal_passed=False)), flush=True)


if __name__ == '__main__':
    main()
