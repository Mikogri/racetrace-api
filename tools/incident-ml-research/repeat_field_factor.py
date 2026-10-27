"""Repeat the selected factor/context learner on all eight unchanged splits."""
import argparse
import json
import pickle
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    out = root / 'artifacts/incident-ml-field-factor'
    manifest = json.loads((out / 'manifest.json').read_text())
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    assert hashes == manifest['input_hashes']
    with np.load(out / 'field-factors.npz') as pack:
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    maximum, changed, points, models = 0., 0, 0, 0
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        with (out / f'{split}.pickle').open('rb') as handle:
            saved = pickle.load(handle)
        names = saved['weather_names']
        assert names == sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        features = np.hstack([x, paired, weather, local])
        choice = next(r for r in manifest['selections'] if r['split'] == split and r['model'] == 'factor-context')
        fresh = train('all-unlabelled', choice['parameter'], features[fit], y[fit], years[fit])
        old_values, new_values = score(saved['models']['factor-context'], features), score(fresh, features)
        maximum = max(maximum, float(np.max(np.abs(old_values-new_values))))
        for budget in BUDGETS:
            old_flag = old_values[test] > threshold_for_budget(old_values[cal], budget)
            new_flag = new_values[test] > threshold_for_budget(new_values[cal], budget)
            changed += int(np.sum(old_flag != new_flag))
            points += 1
        models += 1
        print(f'{split}: selected model repeated', flush=True)
    assert maximum <= 1e-12 and changed == 0
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in manifest['source_hashes'].items())
    result = dict(model='factor-context', model_fits=models, operating_points=points,
        maximum_score_difference=maximum, changed_decisions=changed,
        input_hashes_match=True, production_sources_unchanged=True, saved_models_reload_verified=True)
    (out / 'selected-retraining-verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    main()
