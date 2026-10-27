"""Refit/reload robust observed-label risk and check matched controls."""
import json
import pickle
import numpy as np
from bounded_risk import fit_bounded
from objectives import calibration_partition
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget
from target import BUDGETS


def verify(root, folder, manifest, x, meta, scores, read):
    index = {r['key']: i for i, r in enumerate(meta)}
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    groups = np.array([r['group'] for r in meta])
    parent = root/'artifacts/incident-ml-reviewed-history'
    usage = json.loads((parent/'review-usage.json').read_text())
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    with np.load(root/'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
        assert pack['keys'].tolist() == list(index)
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
        (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    maximum, changed, models, reference_count = 0., 0, 0, 0
    tuning = read(folder/'tuning.csv')
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        eligible_keys = next(r['eligible_fit_keys'] for r in usage
                            if r['split'] == split and r['model'] == 'quarantine-only')
        eligible = np.array([index[k] for k in eligible_keys])
        assert set(eligible) <= set(fit) and not set(groups[fit]) & set(groups[np.r_[cal, test]])
        names = sorted({conditions[i] for i in fit})
        with (parent/f'{split}.pickle').open('rb') as handle:
            reference = pickle.load(handle)
        with (folder/f'{split}.pickle').open('rb') as handle:
            saved = pickle.load(handle)
        assert names == saved['weather_names'] == reference['weather_names']
        with np.load(root/f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
            assert pack['keys'].tolist() == list(index)
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        original = reference['models']['quarantine-only']['model'].predict(inference, num_threads=2)
        for role, indices in [('cal', cal), ('test', test)]:
            np.testing.assert_array_equal(original[indices], [scores[split, 'quarantine-reference', role][i] for i in indices])
            reference_count += len(indices)
        for choice in [r for r in manifest['selections'] if r['split'] == split]:
            name = choice['model']
            assert choice['eligible_fit_keys'] == eligible_keys
            assert choice['symmetric'] == (name == 'symmetric-bounded')
            assert choice['leaves'] == reference['models']['quarantine-only']['leaves']
            trials = [r for r in tuning if r['split'] == split and r['model'] == name]
            assert [float(r['strength']) for r in trials] == ([0.] if name == 'matched-logistic' else [.3, .7])
            best = max(trials, key=lambda r: float(r['calibration_recall']))
            assert float(best['strength']) == choice['strength']
            assert float(best['calibration_recall']) == choice['calibration_objective']
            entry = saved['models'][name]
            for key in ['strength', 'symmetric', 'leaves']:
                assert entry[key] == choice[key]
            model = fit_bounded(fitting[eligible], y[eligible], choice['leaves'], choice['strength'], choice['symmetric'])
            values = model.predict(inference, raw_score=True, num_threads=2)
            np.testing.assert_array_equal(values, entry['model'].predict(inference, raw_score=True, num_threads=2))
            for role, indices in [('cal', cal), ('test', test)]:
                archived = np.array([scores[split, name, role][i] for i in indices])
                maximum = max(maximum, float(np.max(np.abs(values[indices]-archived))))
                for budget in BUDGETS:
                    threshold = threshold_for_budget(values[cal], budget)
                    changed += int(np.sum((values[indices] > threshold) != (archived > threshold)))
            objective = np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340))
            assert objective == choice['calibration_objective']
            models += 1
        print(f'{split}: bounded-risk refits verified', flush=True)
    assert maximum <= 1e-12 and changed == 0 and models == 24
    return dict(bounded_models_retrained=models, maximum_bounded_retrain_difference=maximum,
        changed_bounded_decisions=changed, bounded_reference_scores_verified=reference_count)
