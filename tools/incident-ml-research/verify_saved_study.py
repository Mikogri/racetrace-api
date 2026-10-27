"""Independently recount held-season predictions against the frozen benchmark."""
import argparse
import csv
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from experiment import evaluation_splits, metrics, threshold_for_budget
from objectives import calibration_partition


def read(path):
    with path.open(encoding='utf-8') as handle:
        return list(csv.DictReader(handle))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    parser.add_argument('--documents', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    folder = args.folder.resolve()
    manifest = json.loads((folder / 'manifest.json').read_text())
    base_features, meta, sector_data, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    assert hashes == manifest['input_hashes']
    y = np.array([r['positive'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    years = np.array([r['year'] for r in meta])
    index = {r['key']: i for i, r in enumerate(meta)}
    scores = {}
    for filename, kind in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
        for row in read(folder / filename):
            key = row['split'], row['model'], kind
            values = scores.setdefault(key, {})
            i = index[row['key']]
            assert i not in values, ('duplicate score', key, i)
            values[i] = float(row['score']) if row['score'] else np.nan
    partitions = {}
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        partitions[split] = cal, test
    checked = read(folder / 'metrics.csv')
    for row in checked:
        split, model = row['split'], row['model']
        cal, test = partitions[split]
        cal_values, test_values = (scores[split, model, part] for part in ['cal', 'test'])
        assert set(cal_values) == set(cal) and set(test_values) == set(test)
        c = np.array([cal_values[i] for i in cal])
        t = np.array([test_values[i] for i in test])
        if np.isfinite(c).all():
            threshold = threshold_for_budget(c, float(row['budget']))
        else:
            from local_density import available_threshold
            threshold = available_threshold(c, float(row['budget']))
        assert math.isclose(threshold, float(row['threshold']), abs_tol=1e-12)
        observed = metrics(y[test], t > threshold, baseline[test])
        for name, value in observed.items():
            if value is None:
                assert not row[name]
            else:
                assert math.isclose(float(row[name]), value, abs_tol=1e-12), (row, name, value)
    pooled = read(folder / 'pooled.csv')
    for row in pooled:
        selected = [r for r in checked if r['split'].startswith('leave-') and
                    r['model'] == row['model'] and float(r['budget']) == float(row['budget'])]
        assert len(selected) == 6
        for name in ['hits', 'alarms', 'positives']:
            assert sum(int(r[name]) for r in selected) == int(row[name])
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    source_hashes = manifest.get('source_hashes', {})
    assert all(sha(root / p) == digest for p, digest in source_hashes.items())
    assert all(sha(root / p) == digest for p, digest in manifest.get('component_source_hashes', {}).items())
    usage_checked = 0
    if (folder / 'review-usage.json').exists():
        from review_supervision import match_reviews, training_labels
        annotations_path = root / 'artifacts/incident-human-review/annotations.json'
        assert sha(annotations_path) == manifest['annotation_hash']
        annotations = json.loads(annotations_path.read_text(encoding='utf-8'))
        reviews, _ = match_reviews(meta, annotations['rows'])
        workbook = Path(annotations.get('workbook', annotations.get('workbook_path', '')))
        if workbook.is_file():
            assert sha(workbook) == annotations['workbook_sha256']
        usage = json.loads((folder / 'review-usage.json').read_text())
        outer_by_split = {name: outer for name, outer, _ in evaluation_splits(years)}
        for row in usage:
            usage_checked += 1
            fit, _ = calibration_partition(outer_by_split[row['split']], years, meta, y, 'grouped')
            if 'anchors' in row:
                if row['model'] == 'original-mil':
                    eligible, target, negative = fit, y, np.zeros(len(y), bool)
                    expected_anchors = {meta[i]['key']: meta[i]['known_sectors'] for i in fit
                                        if meta[i]['known_sectors']}
                else:
                    from reviewed_sector import prepare_fit
                    eligible, target, negative, anchors = prepare_fit(y,
                        [r['known_sectors'] for r in meta], fit, reviews, row['model'] == 'reviewed-sectors')
                    expected_anchors = {meta[i]['key']: anchors[i] for i in eligible if anchors[i]}
                assert set(row['fit_keys']) == {meta[i]['key'] for i in eligible}
                assert row['anchors'] == expected_anchors
                assert set(row['anchors']) <= set(row['fit_keys'])
                assert set(row['positive_added_keys']) == {meta[i]['key'] for i in np.flatnonzero(target & ~y)}
                assert set(row['negative_weighted_keys']) == {meta[i]['key'] for i in np.flatnonzero(negative)}
                continue
            quarantine = {int(i) for i in fit if reviews.get(int(i), {}).get('conflict', False)}
            eligible = np.array([i for i in fit if i not in quarantine])
            assert set(row['quarantined_fit_keys']) == {meta[i]['key'] for i in quarantine}
            assert set(row['eligible_fit_keys']) == {meta[i]['key'] for i in eligible}
            if row['model'] == 'quarantine-only':
                target, negative = y, np.zeros(len(y), bool)
            else:
                target, negative = training_labels(y, eligible, reviews, row['model'] == 'reviewed-labels')
            assert set(row['positive_added_keys']) == {meta[i]['key'] for i in np.flatnonzero(target & ~y)}
            assert set(row['negative_weighted_keys']) == {meta[i]['key'] for i in np.flatnonzero(negative)}
    result = dict(verified_operating_points=len(checked), verified_pooled_points=len(pooled),
        calibration_thresholds_recomputed=True, split_identities_recomputed=True,
        input_hashes_match=True, production_sources_unchanged=True,
        research_source_hashes_match=True, reviewed_fit_usages_verified=usage_checked)
    if manifest.get('positive_event_robust_risk'):
        from verify_event_robust import verify
        result.update(verify(root, folder, manifest, base_features, meta, scores, read))
    if manifest.get('fixed_label_subbagging'):
        from verify_subbagging import verify
        result.update(verify(root, folder, manifest, meta, scores, read, sha))
    if manifest.get('field_score_context'):
        from verify_field_score import verify
        result.update(verify(root, folder, manifest, meta, scores, read, sha))
    if manifest.get('continuous_additive_risk'):
        from verify_additive import verify
        result.update(verify(root, folder, manifest, base_features, meta, scores, read))
    if manifest.get('bounded_observed_U_risk'):
        from verify_bounded import verify
        result.update(verify(root, folder, manifest, base_features, meta, scores, read))
    if manifest.get('nonnegative_kernel_ratio'):
        import pickle
        from density_ratio import prepare_ratio, fit_prepared, ratio_scores
        from paired_sessions import prepare as prepare_pairs
        from environment import prepare as prepare_environment
        from data import FEATURE_NAMES
        from target import BUDGETS
        paired, _ = prepare_pairs(root, meta, enhanced=True)
        _, conditions = prepare_environment(root, meta)
        with np.load(root / 'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
            factors = [pack[f'configuration-{j}'] for j in range(4)]
        local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                      (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
        largest_difference, changed_decisions, retrained = 0., 0, 0
        tuning = read(folder / 'tuning.csv')
        assert all(float(r['kkt_residual']) <= 1e-7 for r in tuning)
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                frozen, rolling = pack['frozen'], pack['rolling']
                np.testing.assert_array_equal(pack['fit'], fit)
                np.testing.assert_array_equal(pack['cal'], cal)
                np.testing.assert_array_equal(pack['test'], test)
            with (folder / f'{split}.pickle').open('rb') as handle:
                saved = pickle.load(handle)
            assert saved['weather_names'] == sorted({conditions[i] for i in fit})
            weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
            context = np.hstack([base_features, paired, weather, local])
            fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
            support = base_features[:, [FEATURE_NAMES.index('measured_fraction'), FEATURE_NAMES.index('sector_count')]]
            views = {'ratio-pca12': (fitting, inference, 12), 'ratio-full': (fitting, inference, None),
                     'support-control': (np.hstack([support, frozen[:, -1:]]), np.hstack([support, rolling[:, -1:]]), None)}
            for name, (fit_values, query_values, dimensions) in views.items():
                choice = next(r for r in manifest['selections'] if r['split'] == split and r['model'] == name)
                best = max((r for r in tuning if r['split'] == split and r['model'] == name),
                           key=lambda r: float(r['calibration_recall']))
                assert float(best['multiplier']) == choice['multiplier'] and float(best['ridge']) == choice['ridge']
                old = saved['models'][name]
                prepared, transformed = prepare_ratio(fit_values[fit], y[fit], dimensions)
                assert [meta[fit[j]]['key'] for j in prepared['landmark_rows']] == choice['landmark_keys']
                assert y[fit][prepared['landmark_rows']].all()
                np.testing.assert_allclose(prepared['scaler'].center_, old['scaler'].center_, atol=1e-12)
                np.testing.assert_allclose(prepared['scaler'].scale_, old['scaler'].scale_, atol=1e-12)
                fresh = fit_prepared(prepared, transformed, y[fit], choice['multiplier'], choice['ridge'])
                old_values, new_values = ratio_scores(old, query_values), ratio_scores(fresh, query_values)
                largest_difference = max(largest_difference, float(np.max(np.abs(old_values-new_values))))
                for part, take in [('cal', cal), ('test', test)]:
                    original = np.array([scores[split, name, part][i] for i in take])
                    np.testing.assert_allclose(old_values[take], original, atol=1e-12, rtol=0.)
                for budget in BUDGETS:
                    a = old_values[test] > threshold_for_budget(old_values[cal], budget)
                    b = new_values[test] > threshold_for_budget(new_values[cal], budget)
                    changed_decisions += int(np.sum(a != b))
                retrained += 1
        assert largest_difference <= 1e-10 and changed_decisions == 0
        result.update(ratio_selected_fits_retrained=retrained, maximum_ratio_retrain_difference=largest_difference,
                      changed_ratio_decisions=changed_decisions, fit_positive_landmarks_verified=True,
                      fit_only_preprocessing_verified=True, all_trial_kkt_checks_verified=True)
    if (manifest.get('upper_tail_observed_ranking') or manifest.get('monotone_observed_trees')
            or manifest.get('reviewed_causal_context')):
        import pickle
        from tail_ranking import fit_tail, tail_scores, tail_objective
        from paired_sessions import prepare as prepare_pairs
        from environment import prepare as prepare_environment
        from target import BUDGETS
        from monotone_incidents import fit_monotone, constraints
        monotone = manifest.get('monotone_observed_trees', False)
        reviewed = manifest.get('reviewed_causal_context', False)
        if reviewed:
            from reviewed_search import fit_reviewed
            from hybrid_search import training_weights
            assert (folder / 'review-usage.json').is_file()
        property_checks = json.loads((folder / 'monotonicity-checks.json').read_text()) if monotone else []
        property_comparisons = 0
        paired, _ = prepare_pairs(root, meta, enhanced=True)
        _, conditions = prepare_environment(root, meta)
        with np.load(root / 'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            factors = [pack[f'configuration-{j}'] for j in range(4)]
        local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                      (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
        tuning = read(folder / 'tuning.csv')
        retrained, changed, largest = 0, 0, 0.
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(pack[name], indices)
                frozen, rolling = pack['frozen'], pack['rolling']
            with (folder / f'{split}.pickle').open('rb') as handle:
                saved = pickle.load(handle)
            assert saved['weather_names'] == sorted({conditions[i] for i in fit})
            weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
            context = np.hstack([base_features, paired, weather, local])
            fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
            recipes = ([('monotone-loss', False), ('monotone-loss-rank', True)] if monotone
                       else [('upper-tail', 700/20340), ('full-pair-control', 1.)])
            if reviewed:
                recipes = [(name, None) for name in ['quarantine-only', 'negative-only', 'reviewed-labels']]
            for name, recipe in recipes:
                choice = next(r for r in manifest['selections'] if r['split'] == split and r['model'] == name)
                best = max((r for r in tuning if r['split'] == split and r['model'] == name),
                           key=lambda r: float(r['calibration_recall']))
                assert int(best['leaves']) == choice['leaves']
                old = saved['models'][name]['model'] if reviewed else saved['models'][name]
                if reviewed:
                    eligible = np.array([i for i in fit if not reviews.get(int(i), {}).get('conflict', False)], int)
                    if name == 'quarantine-only':
                        target, negative = y.copy(), np.zeros(len(y), bool)
                    else:
                        target, negative = training_labels(y, eligible, reviews, name == 'reviewed-labels')
                    held = np.concatenate([cal, test])
                    np.testing.assert_array_equal(target[held], y[held])
                    assert not negative[held].any()
                    assert float(best['multiplier']) == choice['multiplier']
                    assert saved['models'][name]['leaves'] == choice['leaves']
                    assert saved['models'][name]['multiplier'] == choice['multiplier']
                    weights = training_weights(target[eligible], negative[eligible], choice['multiplier'])
                    assert abs(float(weights[target[eligible]].sum()-weights[~target[eligible]].sum())) < 1e-8
                    fresh = fit_reviewed(fitting, target, negative, eligible,
                                         choice['leaves'], choice['multiplier']).booster_
                    old_values, new_values = old.predict(inference, num_threads=2), fresh.predict(inference, num_threads=2)
                elif monotone:
                    assert choice['include_rank'] == recipe
                    expected_columns = [0, 4, 8, 12, 20] if recipe else [0, 4, 8, 12]
                    assert choice['constrained_columns'] == expected_columns
                    assert np.flatnonzero(constraints(fitting.shape[1], recipe)).tolist() == expected_columns
                    fresh = fit_monotone(fitting[fit], y[fit], choice['leaves'], recipe)
                    old_values = old.predict(inference, num_threads=2)
                    new_values = fresh.predict(inference, num_threads=2)
                    for column in expected_columns:
                        check = next(r for r in property_checks if r['split'] == split and r['model'] == name
                                     and r['column'] == column)
                        levels = np.quantile(fitting[fit, column], [0., .25, .5, .75, 1.])
                        np.testing.assert_array_equal(check['levels'], levels)
                        probes = np.repeat(inference[test[:32]], 5, axis=0)
                        probes[:, column] = np.tile(levels, min(32, len(test)))
                        differences = np.diff(old.predict(probes, num_threads=2).reshape(-1, 5), axis=1)
                        assert check['comparisons'] == differences.size and check['violations'] == 0
                        assert np.all(differences >= -1e-12)
                        property_comparisons += differences.size
                else:
                    assert choice['fraction'] == recipe
                    fresh = fit_tail(fitting[fit], y[fit], choice['leaves'], recipe)
                    old_values, new_values = tail_scores(old, inference), tail_scores(fresh, inference)
                assert np.isfinite(old_values).all() and np.isfinite(new_values).all()
                largest = max(largest, float(np.max(np.abs(old_values-new_values))))
                for part, take in [('cal', cal), ('test', test)]:
                    original = np.array([scores[split, name, part][i] for i in take])
                    np.testing.assert_allclose(old_values[take], original, atol=1e-12, rtol=0.)
                for budget in BUDGETS:
                    a = old_values[test] > threshold_for_budget(old_values[cal], budget)
                    b = new_values[test] > threshold_for_budget(new_values[cal], budget)
                    changed += int(np.sum(a != b))
                if not monotone and not reviewed:
                    loss, gradient, curvature = tail_objective(tail_scores(old, fitting[fit]), y[fit], recipe)
                    assert math.isfinite(loss) and np.isfinite(gradient).all() and np.isfinite(curvature).all()
                    assert np.all(gradient[y[fit]] <= 0) and np.all(gradient[~y[fit]] >= 0)
                    assert np.all(curvature >= 0) and abs(float(gradient.sum())) < 1e-8
                retrained += 1
        assert largest <= 1e-10 and changed == 0
        compared = 0
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-rolling-history' / filename):
                if row['model'] == 'history-rolling':
                    assert float(row['score']) == scores[row['split'], 'time-reference', part][index[row['key']]]
                    compared += 1
        if reviewed:
            result.update(reviewed_history_models_retrained=retrained, maximum_reviewed_history_retrain_difference=largest,
                          changed_reviewed_history_decisions=changed, reference_scores_compared=compared,
                          calibration_selection_verified=True, original_held_targets_unchanged=True)
        elif monotone:
            assert len(property_checks) == 72
            result.update(monotone_models_retrained=retrained, maximum_monotone_retrain_difference=largest,
                          changed_monotone_decisions=changed, reference_scores_compared=compared,
                          coordinate_comparisons_verified=int(property_comparisons), calibration_selection_verified=True)
        else:
            result.update(tail_models_retrained=retrained, maximum_tail_retrain_difference=largest,
                          changed_tail_decisions=changed, reference_scores_compared=compared,
                          fit_only_tail_objective_verified=True, calibration_selection_verified=True)
    if manifest.get('positive_subtype_specialists'):
        import pickle
        from positive_specialists import profiles, partition_positives
        from review_supervision import match_reviews
        from objectives import train
        from field_objectives import percentile_scores
        from paired_sessions import prepare as prepare_pairs
        from environment import prepare as prepare_environment
        from target import BUDGETS
        annotation_path = root / 'artifacts/incident-human-review/annotations.json'
        assert sha(annotation_path) == manifest['annotation_hash']
        annotations = json.loads(annotation_path.read_text(encoding='utf-8'))
        reviews, _ = match_reviews(meta, annotations['rows'])
        shape, availability, measured = profiles(sector_data)
        independent = np.zeros_like(shape)
        independent[:, 5:] = 1.
        for i, values in enumerate(sector_data):
            ratios = {j: math.log(meta[i]['times'][j]/float(value[11]))
                      for j, value in enumerate(values) if value[12] == 0 and value[13] == 0}
            center = statistics.median(ratios.values()) if ratios else 0.
            for j, value in ratios.items():
                independent[i, j], independent[i, 5+j] = value-center, 0.
        np.testing.assert_allclose(shape, independent, rtol=0., atol=1e-12)
        np.testing.assert_array_equal(availability, independent[:, 5:])
        np.testing.assert_array_equal(measured, np.sum(independent[:, 5:] == 0, axis=1))
        paired, _ = prepare_pairs(root, meta, enhanced=True)
        _, conditions = prepare_environment(root, meta)
        with np.load(root / 'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            factors = [pack[f'configuration-{j}'] for j in range(4)]
        local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                      (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
        usage = json.loads((folder / 'specialist-partitions.json').read_text(encoding='utf-8'))
        tuning = read(folder / 'tuning.csv')
        components, ensembles, changed, max_component, max_ensemble = 0, 0, 0, 0., 0.
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            quarantine = [int(i) for i in fit if reviews.get(int(i), {}).get('conflict', False)]
            eligible = np.array([i for i in fit if int(i) not in quarantine], int)
            unknown = eligible[~y[eligible]]
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(pack[name], indices)
                frozen, rolling = pack['frozen'], pack['rolling']
            with (folder / f'{split}.pickle').open('rb') as handle:
                saved = pickle.load(handle)
            assert saved['weather_names'] == sorted({conditions[i] for i in fit})
            weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
            context = np.hstack([base_features, paired, weather, local])
            fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
            for name, view in [('shape-specialists', shape), ('availability-specialists', availability)]:
                choice = next(r for r in manifest['selections'] if r['split'] == split and r['model'] == name)
                best = max((r for r in tuning if r['split'] == split and r['model'] == name),
                           key=lambda r: float(r['calibration_recall']))
                assert int(best['leaves']) == choice['leaves']
                original = saved['models'][name]
                partition = partition_positives(view, measured, y, eligible)
                for field in ['positive_indices', 'structured_indices', 'shared_indices', 'assignments']:
                    np.testing.assert_array_equal(original['partition'][field], partition[field])
                for a, b in zip(original['partition']['groups'], partition['groups']):
                    np.testing.assert_array_equal(a, b)
                assert len(original['partition']['groups']) == len(partition['groups'])
                if partition['scaler'] is not None:
                    np.testing.assert_allclose(original['partition']['scaler'].center_, partition['scaler'].center_, atol=1e-12, rtol=0.)
                    np.testing.assert_allclose(original['partition']['scaler'].scale_, partition['scaler'].scale_, atol=1e-12, rtol=0.)
                if partition['clusterer'] is not None:
                    np.testing.assert_allclose(original['partition']['clusterer'].cluster_centers_,
                                               partition['clusterer'].cluster_centers_, atol=1e-12, rtol=0.)
                use = next(r for r in usage if r['split'] == split and r['model'] == name)
                assert use['eligible_fit_keys'] == [meta[i]['key'] for i in eligible]
                assert use['quarantined_fit_keys'] == [meta[i]['key'] for i in quarantine]
                assert use['unknown_fit_keys'] == [meta[i]['key'] for i in unknown]
                assert use['shared_positive_keys'] == [meta[i]['key'] for i in partition['shared_indices']]
                assert use['positive_group_keys'] == [[meta[i]['key'] for i in group] for group in partition['groups']]
                assert set(np.concatenate(partition['groups'])) == set(eligible[y[eligible]])
                old_ranks, new_ranks = [], []
                for j, positives in enumerate(partition['groups']):
                    take = np.sort(np.r_[unknown, positives])
                    assert len(np.unique(take)) == len(take) and set(take) <= set(fit)
                    assert not set(take) & set(np.concatenate([cal, test]))
                    fresh = train('all-unlabelled', choice['leaves'], fitting[take], y[take], years[take]).booster_
                    old_values = original['models'][j].predict(inference, num_threads=2)
                    new_values = fresh.predict(inference, num_threads=2)
                    np.testing.assert_array_equal(original['calibrations'][j], old_values[cal])
                    max_component = max(max_component, float(np.max(np.abs(old_values-new_values))))
                    old_ranks.append(percentile_scores(original['calibrations'][j], old_values))
                    new_ranks.append(percentile_scores(new_values[cal], new_values))
                    components += 1
                a, b = np.max(np.vstack(old_ranks), axis=0), np.max(np.vstack(new_ranks), axis=0)
                max_ensemble = max(max_ensemble, float(np.max(np.abs(a-b))))
                for part, take in [('cal', cal), ('test', test)]:
                    np.testing.assert_allclose(a[take], [scores[split, name, part][i] for i in take], atol=1e-12, rtol=0.)
                for budget in BUDGETS:
                    changed += int(np.sum((a[test] > threshold_for_budget(a[cal], budget)) !=
                                          (b[test] > threshold_for_budget(b[cal], budget))))
                ensembles += 1
        assert ensembles == len(usage) == 16 and max_component <= 1e-10 and max_ensemble <= 1e-10 and changed == 0
        compared = 0
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-reviewed-history' / filename):
                if row['model'] == 'quarantine-only':
                    assert float(row['score']) == scores[row['split'], 'quarantine-reference', part][index[row['key']]]
                    compared += 1
        result.update(specialist_ensembles_rebuilt=ensembles, specialist_components_retrained=components,
                      maximum_specialist_component_difference=max_component, maximum_specialist_ensemble_difference=max_ensemble,
                      changed_specialist_decisions=changed, reference_scores_compared=compared,
                      independently_rebuilt_profiles=len(meta), fitting_partitions_verified=ensembles,
                      calibration_only_component_cdfs_verified=True)
    if manifest.get('frozen_specialist_routing'):
        import pickle
        from positive_specialists import profiles
        from gated_specialists import gated_scores
        from field_objectives import percentile_scores
        from paired_sessions import prepare as prepare_pairs
        from environment import prepare as prepare_environment
        base = root / 'artifacts/incident-ml-specialists'
        parent = json.loads((base / 'manifest.json').read_text(encoding='utf-8'))
        inherited = json.loads((base / 'verification.json').read_text(encoding='utf-8'))
        assert parent['input_hashes'] == hashes
        for field in ['source_hashes', 'component_source_hashes']:
            assert all(sha(root / path) == digest for path, digest in parent[field].items())
        assert inherited['changed_specialist_decisions'] == 0 and inherited['maximum_specialist_component_difference'] <= 1e-10
        shape, availability, measured = profiles(sector_data)
        paired, _ = prepare_pairs(root, meta, enhanced=True)
        _, conditions = prepare_environment(root, meta)
        with np.load(root / 'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            factors = [pack[f'configuration-{j}'] for j in range(4)]
        local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                      (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
        old_scores = {}
        for filename, role in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(base / filename):
                old_scores[row['split'], row['model'], role, index[row['key']]] = float(row['score'])
        diagnostics = read(folder / 'routing-diagnostics.csv')
        views, components, compared, independently_checked, near_ties = 0, 0, 0, 0, 0
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(pack[name], indices)
                rolling = pack['rolling']
            with (base / f'{split}.pickle').open('rb') as handle:
                saved = pickle.load(handle)
            assert saved['weather_names'] == sorted({conditions[i] for i in fit})
            weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
            inference = np.hstack([base_features, paired, weather, local, rolling])
            for base_name, prefix, profile in [('shape-specialists', 'shape', shape),
                                              ('availability-specialists', 'availability', availability)]:
                package = saved['models'][base_name]
                raw = [model.predict(inference, num_threads=2) for model in package['models']]
                for calibration, value in zip(package['calibrations'], raw):
                    np.testing.assert_array_equal(calibration, value[cal])
                ranks = np.vstack([percentile_scores(c, v) for c, v in zip(package['calibrations'], raw)])
                maximum = np.max(ranks, axis=0)
                gated = gated_scores(package['partition'], profile, measured, ranks)
                fallback = measured < 2
                np.testing.assert_array_equal(gated[fallback], maximum[fallback])
                state, take = package['partition'], np.flatnonzero(~fallback)
                if state['clusterer'] is None:
                    np.testing.assert_array_equal(gated, maximum)
                elif len(take):
                    # Reconstruct scaling/distances independently of sklearn's predict.
                    z = np.clip((profile[take]-state['scaler'].center_)/state['scaler'].scale_, -4., 4.)
                    distances = np.sum((z[:, None, :]-state['clusterer'].cluster_centers_[None, :, :])**2, axis=2)
                    nearest = np.argmin(distances, axis=1)
                    tolerance = 1e-12*(1+np.abs(distances.min(axis=1)))
                    allowed = distances <= distances.min(axis=1)[:, None]+tolerance[:, None]
                    unique = allowed.sum(axis=1) == 1
                    np.testing.assert_array_equal(gated[take[unique]], ranks[nearest[unique], take[unique]])
                    for position in np.flatnonzero(~unique):
                        assert gated[take[position]] in ranks[np.flatnonzero(allowed[position]), take[position]]
                    independently_checked += len(take)
                    near_ties += int(np.sum(~unique))
                for name, values in [(prefix+'-max-control', maximum), (prefix+'-gated', gated)]:
                    for role, indices in [('cal', cal), ('test', test)]:
                        np.testing.assert_allclose(values[indices], [scores[split, name, role][i] for i in indices],
                                                   atol=1e-12, rtol=0.)
                for role, indices in [('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(maximum[indices], [old_scores[split, base_name, role, i] for i in indices])
                    compared += len(indices)
                diagnostic = next(r for r in diagnostics if r['split'] == split and r['view'] == prefix)
                assert int(diagnostic['experts']) == len(raw)
                assert int(diagnostic['test_low_support_fallbacks']) == int(np.sum(fallback[test]))
                assert int(diagnostic['test_scores_changed_by_routing']) == int(np.sum(gated[test] != maximum[test]))
                components += len(raw)
                views += 1
            for role, indices in [('cal', cal), ('test', test)]:
                assert all(scores[split, 'quarantine-reference', role][i] ==
                           old_scores[split, 'quarantine-reference', role, i] for i in indices)
                compared += len(indices)
        assert views == 16 and components == manifest['inherited_component_count']
        result.update(routing_views_replayed=views, frozen_components_reused=components, new_models_fitted=0,
                      independent_nearest_profile_checks=independently_checked, numerical_near_tie_queries=near_ties,
                      reference_scores_compared=compared, inherited_fit_only_provenance_verified=True,
                      calibration_only_cdfs_replayed=True)
    if manifest.get('label_efficiency_diagnostic'):
        import pickle
        from label_budget import sample_fit_labels
        from reviewed_search import fit_reviewed
        from paired_sessions import prepare as prepare_pairs
        from environment import prepare as prepare_environment
        from target import BUDGETS
        assert manifest['diagnostic_only'] and manifest['detector_selected'] is False
        base = root / 'artifacts/incident-ml-reviewed-history'
        parent = json.loads((base / 'manifest.json').read_text(encoding='utf-8'))
        assert parent['input_hashes'] == hashes
        for field in ['source_hashes', 'component_source_hashes']:
            assert all(sha(root / path) == digest for path, digest in parent[field].items())
        uses = json.loads((base / 'review-usage.json').read_text(encoding='utf-8'))
        paired, _ = prepare_pairs(root, meta, enhanced=True)
        _, conditions = prepare_environment(root, meta)
        with np.load(root / 'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            factors = [pack[f'configuration-{j}'] for j in range(4)]
        local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                      (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
        groups = np.array([r['group'] for r in meta])
        recipes = read(folder / 'fit-label-recipes.csv')
        samples_checked, retrained, full_replayed, largest, changed = 0, 0, 0, 0., 0
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            use = next(r for r in uses if r['split'] == split and r['model'] == 'quarantine-only')
            eligible = np.array([index[key] for key in use['eligible_fit_keys']], int)
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(pack[name], indices)
                frozen, rolling = pack['frozen'], pack['rolling']
            with (folder / f'{split}.pickle').open('rb') as handle:
                saved = pickle.load(handle)
            with (base / f'{split}.pickle').open('rb') as handle:
                inherited = pickle.load(handle)
            np.testing.assert_array_equal(saved['eligible'], eligible)
            assert saved['weather_names'] == inherited['weather_names'] == sorted({conditions[i] for i in fit})
            weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
            context = np.hstack([base_features, paired, weather, local])
            fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
            leaves = inherited['models']['quarantine-only']['leaves']
            for name, entry in saved['models'].items():
                recipe = next(r for r in recipes if r['split'] == split and r['model'] == name)
                assert int(recipe['leaves']) == leaves and float(recipe['fraction']) == entry['fraction']
                if name == 'full-reference':
                    wanted = eligible
                    fresh = inherited['models']['quarantine-only']['model']
                    full_replayed += 1
                else:
                    # Independently derive the nested prefix, without the sampler helper.
                    p = eligible[y[eligible]]
                    rng = np.random.default_rng(entry['seed'])
                    if entry['unit'] == 'run':
                        order = rng.permutation(p)
                        selected = set(order[:max(1, math.ceil(entry['fraction']*len(order)))])
                    else:
                        events = rng.permutation(np.unique(groups[p]))
                        event_set = set(events[:max(1, math.ceil(entry['fraction']*len(events)))])
                        selected = {i for i in p if groups[i] in event_set}
                    wanted = np.array([i for i in eligible if not y[i] or i in selected], int)
                    np.testing.assert_array_equal(wanted, sample_fit_labels(y, groups, eligible,
                                                  entry['fraction'], entry['seed'], entry['unit']))
                    fresh = fit_reviewed(fitting, y, np.zeros(len(y), bool), wanted, leaves, 1.).booster_
                    retrained += 1
                np.testing.assert_array_equal(entry['take'], wanted)
                assert not set(wanted) & set(np.r_[cal, test])
                np.testing.assert_array_equal(wanted[~y[wanted]], eligible[~y[eligible]])
                assert int(recipe['kept_fit_positives']) == int(y[wanted].sum())
                assert int(recipe['unknown_fit_rows']) == int((~y[wanted]).sum())
                assert int(recipe['kept_positive_events']) == len(np.unique(groups[wanted[y[wanted]]]))
                a = entry['model'].predict(inference, num_threads=2)
                b = fresh.predict(inference, num_threads=2)
                largest = max(largest, float(np.max(np.abs(a-b))))
                for role, take in [('cal', cal), ('test', test)]:
                    np.testing.assert_allclose(a[take], [scores[split, name, role][i] for i in take], atol=1e-12, rtol=0.)
                fitted = entry['model'].predict(fitting[wanted], num_threads=2)
                for row in [r for r in checked if r['split'] == split and r['model'] == name]:
                    threshold = threshold_for_budget(a[cal], float(row['budget']))
                    hits = int(np.sum((fitted > threshold) & y[wanted]))
                    assert int(row['fit_positive_count']) == int(y[wanted].sum())
                    assert int(row['fit_positive_hits']) == hits
                    assert math.isclose(float(row['fit_positive_recall']), hits/int(y[wanted].sum()), abs_tol=1e-12)
                    changed += int(np.sum((a[test] > threshold) !=
                                          (b[test] > threshold_for_budget(b[cal], float(row['budget'])))))
                samples_checked += 1
        assert retrained == manifest['new_models_fitted'] == 96 and full_replayed == 8
        assert largest <= 1e-10 and changed == 0 and samples_checked == len(recipes) == 104
        result.update(label_samples_independently_rebuilt=samples_checked, learning_curve_models_retrained=retrained,
                      full_reference_models_replayed=full_replayed, maximum_learning_curve_retrain_difference=largest,
                      changed_learning_curve_decisions=changed, fixed_u_and_original_held_targets_verified=True,
                      inherited_leaves_not_retuned=True, fit_threshold_diagnostics_verified=True)
    if manifest.get('weak_bag_sequence') or manifest.get('field_noise_sequence'):
        import pickle
        from rolling_history import records_from_frozen
        from sector_sequence import observations, anchor_mask, fit_sequence, sequence_scores
        from local_density import available_threshold
        from target import BUDGETS
        records = records_from_frozen(root, meta, sector_data)
        anchors = anchor_mask(meta)
        within = observations(records, meta)
        retrained, changed, max_difference, observation_checks = 0, 0, 0., 0
        tuning = read(folder / 'tuning.csv')
        scaled_checks = 0

        def independent_observations(history=None):
            rr, clocks, masks = [], [], []
            for i, row in enumerate(meta):
                times, medians, *_ = records[row['key']]
                valid = [bool(math.isfinite(t) and t > 0 and math.isfinite(m) and m > 0)
                         for t, m in zip(times, medians)]
                ratios = sorted(math.log(times[j]/medians[j]) for j in range(5) if valid[j])
                if ratios:
                    position = .35*(len(ratios)-1)
                    low, high = math.floor(position), math.ceil(position)
                    center = ratios[low]+(ratios[high]-ratios[low])*(position-low)
                else:
                    center = 0.
                if history is not None and history[i, -1] > 0:
                    center = float(history[i, -3])
                expected = [medians[j]*math.exp(center) if valid[j] else 1. for j in range(5)]
                rr.append([times[j]/expected[j]-1 if valid[j] else 0. for j in range(5)])
                clocks.append(expected)
                masks.append(valid)
            return np.array(rr), np.array(clocks), np.array(masks)

        def audited_scale(view, history=None):
            from field_noise_sequence import scale_observations
            actual = scale_observations(view, sector_data, history)
            r, clock, valid = view
            wanted_r, wanted_clock = r.copy(), clock.copy()
            for i, values in enumerate(sector_data):
                spread = (float(history[i, -2]) if history is not None and history[i, -1] > 0
                          and math.isfinite(history[i, -2]) and history[i, -2] >= 0 else 0.)
                for j in range(5):
                    mad = .04
                    if j < len(values):
                        candidate, count = values[j, 9], values[j, 10]
                        if math.isfinite(candidate) and candidate > 0 and count >= 8:
                            mad = max(.005, candidate)
                    scale = math.sqrt(mad*mad+spread*spread)/.04
                    wanted_r[i, j], wanted_clock[i, j] = r[i, j]/scale, clock[i, j]*scale
            for a, b in zip(actual, (wanted_r, wanted_clock, valid)):
                np.testing.assert_allclose(a, b, rtol=1e-12, atol=1e-12)
            np.testing.assert_allclose(actual[0]*actual[1], r*clock, rtol=1e-12, atol=1e-12)
            return actual

        for a, b in zip(within, independent_observations()):
            np.testing.assert_allclose(a, b, rtol=1e-12, atol=1e-12)
        observation_checks += len(meta)
        if manifest.get('field_noise_sequence'):
            within = audited_scale(within)
            scaled_checks += len(meta)
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            with np.load(root / f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                    np.testing.assert_array_equal(pack[name], indices)
                fitting = observations(records, meta, pack['frozen'])
                inference = observations(records, meta, pack['rolling'])
                for label, observed in [('frozen', fitting), ('rolling', inference)]:
                    for a, b in zip(observed, independent_observations(pack[label])):
                        np.testing.assert_allclose(a, b, rtol=1e-12, atol=1e-12)
                    observation_checks += len(meta)
                if manifest.get('field_noise_sequence'):
                    joint_fit, joint_eval = audited_scale(fitting, pack['frozen']), audited_scale(inference, pack['rolling'])
                    fitting, inference = audited_scale(fitting), audited_scale(inference)
                    scaled_checks += 4*len(meta)
            with (folder / f'{split}.pickle').open('rb') as handle:
                models = pickle.load(handle)
            views = [
                    ('sequence-history', fitting, inference, False),
                    ('sequence-within', within, within, False),
                    ('independent-history', fitting, inference, True)]
            if manifest.get('field_noise_sequence'):
                views = [('field-history', fitting, inference, False),
                         ('field-within', within, within, False),
                         ('field-and-history-noise', joint_fit, joint_eval, False)]
            for name, fit_data, query_data, independent in views:
                choice = next(r for r in manifest['selections'] if r['split'] == split and r['model'] == name)
                best = max((r for r in tuning if r['split'] == split and r['model'] == name),
                           key=lambda r: float(r['calibration_recall']))
                assert (best['balanced'] == 'True') == choice['balanced']
                assert choice['fit_anchor_keys'] == [meta[i]['key'] for i in fit if anchors[i].any()]
                if 'omitted_all_missing_fit_keys' in choice:
                    assert choice['omitted_all_missing_fit_keys'] == [meta[i]['key'] for i in fit if not fit_data[2][i].any()]
                old = models[name]
                fresh = fit_sequence(*(a[fit] for a in fit_data), y[fit], balanced=choice['balanced'],
                                     independent=independent, anchors=anchors[fit])
                old_values, new_values = sequence_scores(old, *query_data), sequence_scores(fresh, *query_data)
                available = np.isfinite(old_values)
                np.testing.assert_array_equal(available, np.isfinite(new_values))
                assert np.array_equal(~available, ~query_data[2].any(axis=1))
                max_difference = max(max_difference, float(np.max(np.abs(old_values[available]-new_values[available]))))
                for part, take in [('cal', cal), ('test', test)]:
                    original = np.array([scores[split, name, part][i] for i in take])
                    np.testing.assert_allclose(old_values[take], original, atol=1e-12, rtol=0., equal_nan=True)
                for budget in BUDGETS:
                    a = old_values[test] > available_threshold(old_values[cal], budget)
                    b = new_values[test] > available_threshold(new_values[cal], budget)
                    changed += int(np.sum(a != b))
                retrained += 1
        assert max_difference <= 1e-10 and changed == 0
        result.update(sequence_selected_fits_retrained=retrained, maximum_sequence_retrain_difference=max_difference,
                      changed_sequence_decisions=changed, independently_rebuilt_observations=observation_checks,
                      fit_only_bag_anchors_verified=True, missing_observations_verified=True)
        if manifest.get('field_noise_sequence'):
            comparisons, largest = 0, 0.
            for original_folder, original_model, current_name in [
                    ('incident-ml-rolling-history', 'history-rolling', 'time-reference'),
                    ('incident-ml-sector-sequence', 'sequence-history', 'sequence-reference')]:
                for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
                    for row in read(root / 'artifacts' / original_folder / filename):
                        if row['model'] != original_model:
                            continue
                        a = float(row['score'])
                        b = scores[row['split'], current_name, part][index[row['key']]]
                        assert math.isfinite(a) == math.isfinite(b)
                        if math.isfinite(a):
                            largest = max(largest, abs(a-b))
                        comparisons += 1
            assert largest == 0.
            result.update(independently_rebuilt_field_scaled_observations=scaled_checks,
                          reference_scores_compared=comparisons, maximum_reference_difference=largest,
                          optimizer_failures=sum(int(r['optimizer_failures']) for r in tuning))
    if manifest.get('causal_unlabelled_rank_history'):
        from rank_history import profiles_from_frozen, scoped_references, swap_references
        from rolling_history import records_from_frozen
        records = records_from_frozen(root, meta, sector_data)
        profiles = profiles_from_frozen(root, meta, sector_data)
        audited_targets = 0
        for split, outer, test in evaluation_splits(years):
            fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
            frozen, rolling = scoped_references(profiles, records, meta, fit, cal, test)
            wrong, changed = swap_references(rolling, meta)
            np.testing.assert_array_equal(frozen[fit], rolling[fit])
            np.testing.assert_array_equal(rolling[:, 2], wrong[:, 2])
            with np.load(folder / f'{split}-rank.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, value in [('fit', fit), ('cal', cal), ('test', test), ('profiles', profiles),
                                    ('frozen', frozen), ('rolling', rolling), ('placebo', wrong), ('placebo_changed', changed)]:
                    np.testing.assert_allclose(pack[name], value, atol=1e-12, rtol=0., equal_nan=True)
            for targets, allowed in [(fit, fit), (cal, np.concatenate([fit, cal])),
                                     (test, np.concatenate([fit, cal, test]))]:
                histories = defaultdict(list)
                for i in allowed:
                    _, _, _, rider, date, round_id, category = records[meta[i]['key']]
                    values = profiles[i, np.isfinite(profiles[i])]
                    if len(values) >= 2 and date:
                        histories[rider, category].append((date, round_id, float(np.quantile(values, .35))))
                for i in targets:
                    _, _, _, rider, date, round_id, category = records[meta[i]['key']]
                    prior = sorted((r for r in histories[rider, category] if date and r[0] < date and r[1] != round_id),
                                   key=lambda r: r[0])[-8:]
                    values = [r[2] for r in prior]
                    center = statistics.median(values) if values else 0.
                    dispersion = 1.4826*statistics.median([abs(v-center) for v in values]) if values else 0.
                    np.testing.assert_allclose(rolling[i], [center, dispersion, len(values)], rtol=0., atol=1e-12)
                    audited_targets += 1
        tuning = read(folder / 'tuning.csv')
        for choice in manifest['selections']:
            best = max((r for r in tuning if r['split'] == choice['split'] and r['model'] == choice['model']),
                       key=lambda r: float(r['calibration_recall']))
            assert int(best['leaves']) == choice['leaves']
            cal, _ = partitions[choice['split']]
            values = np.array([scores[choice['split'], choice['model'], 'cal'][i] for i in cal])
            observed = float(np.mean(values[y[cal]] > threshold_for_budget(values, 700 / 20340)))
            assert math.isclose(observed, choice['calibration_objective'], abs_tol=1e-12)
        maximum, compared = 0., 0
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-rolling-history' / filename):
                if row['model'] == 'history-rolling':
                    maximum = max(maximum, abs(scores[row['split'], 'time-reference', part][index[row['key']]]-float(row['score'])))
                    compared += 1
        assert maximum <= 1e-12 and compared > 0
        old_manifest = json.loads((root / 'artifacts/incident-ml-rolling-history/manifest.json').read_text())
        assert all(sha(root / p) == digest for p, digest in old_manifest['source_hashes'].items())
        result.update(rank_history_features_recomputed=True, independent_rank_targets_audited=audited_targets,
                      calibration_selections_verified=len(manifest['selections']), shared_history_dependencies_unchanged=True,
                      reference_scores_compared=compared, maximum_reference_difference=maximum)
    if manifest.get('causal_unlabelled_stream_history'):
        from rolling_history import records_from_frozen, scoped_features, placebo_features
        records = records_from_frozen(root, meta, sector_data)
        scopes = json.loads((folder / 'history-scopes.json').read_text())
        outer_by_split = {name: outer for name, outer, _ in evaluation_splits(years)}
        audited_targets = 0
        for row in scopes:
            split = row['split']
            cal, test = partitions[split]
            fit, _ = calibration_partition(outer_by_split[split], years, meta, y, 'grouped')
            for name, indices in [('fit_keys', fit), ('calibration_keys', cal), ('test_keys', test)]:
                assert row[name] == [meta[i]['key'] for i in indices]
            frozen, rolling = scoped_features(records, meta, fit, cal, test)
            wrong, changed = placebo_features(rolling, records, meta)
            np.testing.assert_array_equal(frozen[fit], rolling[fit])
            np.testing.assert_array_equal(rolling[:, -1], wrong[:, -1])
            with np.load(folder / f'{split}-history.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for name, value in [('fit', fit), ('cal', cal), ('test', test), ('frozen', frozen),
                                    ('rolling', rolling), ('placebo', wrong), ('placebo_changed', changed)]:
                    np.testing.assert_allclose(pack[name], value, atol=1e-12, rtol=0.)
            assert int(changed.sum()) == row['placebo_permutable_runs']
            assert int(np.sum(frozen[test, -1] > 0)) == row['frozen_test_covered']
            assert int(np.sum(rolling[test, -1] > 0)) == row['rolling_test_covered']
            # Independent source-role/date audit, not just replay of the helper.
            for targets, allowed in [(fit, fit), (cal, np.concatenate([fit, cal])),
                                     (test, np.concatenate([fit, cal, test]))]:
                histories = defaultdict(list)
                for i in allowed:
                    t, m, _, rider, date, round_id, category = records[meta[i]['key']]
                    valid = np.isfinite(t) & (m > 0)
                    if valid.sum() >= 2 and date:
                        histories[rider, category].append((date, round_id, float(np.quantile(np.log(t[valid]/m[valid]), .35))))
                for i in targets:
                    _, _, _, rider, date, round_id, category = records[meta[i]['key']]
                    candidates = sorted((r for r in histories[rider, category]
                                         if date and r[0] < date and r[1] != round_id), key=lambda r: r[0])[-8:]
                    values = [r[2] for r in candidates]
                    center = statistics.median(values) if values else 0.
                    dispersion = 1.4826*statistics.median([abs(v-center) for v in values]) if values else 0.
                    np.testing.assert_allclose(rolling[i, -3:], [center, dispersion, len(values)], rtol=0., atol=1e-12)
                    audited_targets += 1
        tuning = read(folder / 'tuning.csv')
        for choice in manifest['selections']:
            best = max((r for r in tuning if r['split'] == choice['split'] and r['model'] == choice['model']),
                       key=lambda r: float(r['calibration_recall']))
            assert int(best['leaves']) == choice['leaves']
            cal, _ = partitions[choice['split']]
            values = np.array([scores[choice['split'], choice['model'], 'cal'][i] for i in cal])
            observed = float(np.mean(values[y[cal]] > threshold_for_budget(values, 700 / 20340)))
            assert math.isclose(observed, choice['calibration_objective'], abs_tol=1e-12)
        maximum, compared = 0., 0
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-field-factor' / filename):
                if row['model'] == 'factor-context':
                    maximum = max(maximum, abs(scores[row['split'], 'context-reference', part][index[row['key']]]-float(row['score'])))
                    compared += 1
        assert maximum <= 1e-12 and compared > 0
        result.update(history_scopes_verified=len(scopes), rolling_features_recomputed=True,
                      fitting_histories_unchanged=True, reference_scores_compared=compared,
                      maximum_reference_difference=maximum, calibration_selections_verified=len(manifest['selections']),
                      independent_role_date_targets_audited=audited_targets)
        archive = folder.with_name(folder.name + '-failed-forward-scope')
        repeated_arrays, repeated_models = 0, 0
        if archive.exists():
            import pickle
            for old_file in archive.glob('leave-*-out-history.npz'):
                with np.load(old_file) as old, np.load(folder / old_file.name) as new:
                    assert old.files == new.files
                    for name in old.files:
                        np.testing.assert_array_equal(old[name], new[name])
                    repeated_arrays += 1
                model_name = old_file.name.replace('-history.npz', '.pickle')
                with (archive / model_name).open('rb') as handle:
                    old_models = pickle.load(handle)['models']
                with (folder / model_name).open('rb') as handle:
                    new_models = pickle.load(handle)['models']
                assert set(old_models) == set(new_models)
                for name in old_models:
                    assert old_models[name].booster_.model_to_string() == new_models[name].booster_.model_to_string()
                    repeated_models += 1
        result.update(repeated_loyo_history_arrays=repeated_arrays,
                      identical_selected_model_representations=repeated_models)
    if (folder / 'checkpoint-features.npz').exists():
        from finish_change import movements
        times = np.full((len(meta), 5), np.nan)
        for i, row in enumerate(meta):
            for j, value in enumerate(row['times']):
                if value and sector_data[i][j, 12] == 0 and sector_data[i][j, 13] == 0:
                    times[i, j] = value
        groups = np.array([r['key'].split('/')[0] + '/' + r['category'] for r in meta])
        intermediate, full = np.zeros((len(meta), 5, 7)), np.zeros((len(meta), 5, 7))
        finish_indices = np.zeros(len(meta), int)
        for group in np.unique(groups):
            take = np.flatnonzero(groups == group)
            finish = max(len(sector_data[i]) for i in take)-1
            if finish >= 0:
                finish_indices[take] = finish
                intermediate[take] = movements(times[take], finish, False)
                full[take] = movements(times[take], finish, True)
        with np.load(folder / 'checkpoint-features.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            assert pack['groups'].tolist() == groups.tolist()
            for name, value in [('times', times), ('finish', finish_indices), ('intermediate', intermediate), ('full', full)]:
                np.testing.assert_allclose(pack[name], value, atol=1e-12, rtol=0., equal_nan=True)
        tuning = read(folder / 'tuning.csv')
        for choice in manifest['selections']:
            best = max((r for r in tuning if r['split'] == choice['split'] and r['model'] == choice['model']),
                       key=lambda r: float(r['calibration_recall']))
            assert int(best['leaves']) == choice['leaves']
            cal, _ = partitions[choice['split']]
            values = np.array([scores[choice['split'], choice['model'], 'cal'][i] for i in cal])
            observed = float(np.mean(values[y[cal]] > threshold_for_budget(values, 700 / 20340)))
            assert math.isclose(observed, choice['calibration_objective'], abs_tol=1e-12)
        maximum, compared = 0., 0
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-field-factor' / filename):
                if row['model'] == 'factor-context':
                    maximum = max(maximum, abs(scores[row['split'], 'context-reference', part][index[row['key']]]-float(row['score'])))
                    compared += 1
        assert maximum <= 1e-12 and compared > 0
        result.update(checkpoint_features_recomputed=True, calibration_selections_verified=len(manifest['selections']),
                      reference_scores_compared=compared, maximum_reference_difference=maximum)
    if (folder / 'sibling-features.npz').exists():
        from sibling_profiles import references, placebo, features as sibling_features
        lookup, layouts = {}, {}
        for year in range(2021, 2027):
            bundle = json.loads((root / f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
            for run in bundle['PaceSessions']:
                lookup[f"{run['SessionId']}/{run['Run']['RiderId']}"] = str(run['CourseKey'])
        for row, sectors in zip(meta, sector_data):
            sid = row['key'].split('/')[0]
            layouts[sid] = max(layouts.get(sid, 0), len(sectors))
        courses = [lookup[r['key']] + '/layout-' + str(layouts[r['key'].split('/')[0]]) for r in meta]
        profiles, durations = np.full((len(meta), 5), np.nan), np.zeros((len(meta), 5))
        for i, sectors in enumerate(sector_data):
            for j, sector in enumerate(sectors):
                if sector[12] == 0 and sector[13] == 0:
                    profiles[i, j], durations[i, j] = sector[4], sector[11]
        reference, counts = references(profiles, meta, courses)
        swapped, changed = placebo(reference, counts, meta, courses)
        with np.load(folder / 'sibling-features.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            assert pack['courses'].tolist() == courses
            for name, values in [('profiles', profiles), ('reference', reference), ('counts', counts),
                    ('placebo_reference', swapped), ('placebo_changed', changed),
                    ('aligned', sibling_features(profiles, reference, counts, durations)),
                    ('placebo', sibling_features(profiles, swapped, counts, durations)),
                    ('support', np.hstack([counts, np.isfinite(reference).astype(float)]))]:
                np.testing.assert_allclose(pack[name], values, atol=1e-12, rtol=0., equal_nan=True)
        assert np.array_equal(np.isfinite(reference), np.isfinite(swapped))
        assert int(changed.sum()) == manifest['placebo_permutable_runs']
        tuning = read(folder / 'tuning.csv')
        for choice in manifest['selections']:
            best = max((r for r in tuning if r['split'] == choice['split'] and r['model'] == choice['model']),
                       key=lambda r: float(r['calibration_recall']))
            assert int(best['leaves']) == choice['leaves']
            cal, _ = partitions[choice['split']]
            values = np.array([scores[choice['split'], choice['model'], 'cal'][i] for i in cal])
            recall = float(np.mean(values[y[cal]] > threshold_for_budget(values, 700 / 20340)))
            assert math.isclose(recall, choice['calibration_objective'], abs_tol=1e-12)
        compared, maximum = 0, 0.
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(root / 'artifacts/incident-ml-field-factor' / filename):
                if row['model'] == 'factor-context':
                    value = scores[row['split'], 'context-reference', part][index[row['key']]]
                    maximum = max(maximum, abs(value-float(row['score'])))
                    compared += 1
        assert maximum <= 1e-12 and compared > 0
        result.update(sibling_features_recomputed=True, reference_scores_compared=compared,
            maximum_reference_difference=maximum, support_matched_placebo_verified=True,
            usable_sibling_runs=int(np.any(np.isfinite(reference) & np.isfinite(profiles), axis=1).sum()))
    if manifest.get('no_model_refit') and manifest.get('component_source_hashes'):
        from factor_blend import combine
        source_rows = 0
        for source_folder, source_model, alias in [
                ('incident-ml-field-factor', 'factor-context', 'factor-reference'),
                ('incident-ml-field-objectives-enhanced', 'paired-weather-round-category-rank', 'rank-reference')]:
            for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
                for row in read(root / 'artifacts' / source_folder / filename):
                    if row['model'] == source_model:
                        assert math.isclose(scores[row['split'], alias, part][index[row['key']]], float(row['score']), abs_tol=1e-12)
                        source_rows += 1
        tuning = read(folder / 'tuning.csv')
        disagreements = read(folder / 'component-disagreement.csv')
        for choice in manifest['selections']:
            split = choice['split']
            cal, test = partitions[split]
            c = np.column_stack([[scores[split, name, 'cal'][i] for i in cal] for name in ['factor-reference', 'rank-reference']])
            t = np.column_stack([[scores[split, name, 'test'][i] for i in test] for name in ['factor-reference', 'rank-reference']])
            trials = [r for r in tuning if r['split'] == split]
            for row in trials:
                combined = combine(c, c, row['kind'], float(row['weight']))
                observed = float(np.mean(combined[y[cal]] > threshold_for_budget(combined, 700 / 20340)))
                assert math.isclose(observed, float(row['calibration_recall']), abs_tol=1e-12)
            winner = max(trials, key=lambda r: float(r['calibration_recall']))
            assert winner['kind'] == choice['kind'] and float(winner['weight']) == choice['weight']
            for part, take, values in [('cal', cal, c), ('test', test, t)]:
                expected = combine(c, values, choice['kind'], choice['weight'])
                saved = np.array([scores[split, 'selected-combination', part][i] for i in take])
                assert np.max(np.abs(expected-saved)) <= 1e-12
            for row in [r for r in disagreements if r['split'] == split]:
                budget = float(row['budget'])
                a, b = (t[:, j] > threshold_for_budget(c[:, j], budget) for j in range(2))
                regions = {'both': a & b, 'factor_only': a & ~b, 'rank_only': b & ~a}
                for name, mask in regions.items():
                    assert int(mask.sum()) == int(row[name])
                    assert int(np.sum(y[test] & mask)) == int(row['known_' + name])
                assert int(np.sum(y[test] & ~a & ~b)) == int(row['known_neither'])
        result.update(component_scores_verified=source_rows, combination_selections_verified=len(manifest['selections']),
                      combinations_recomputed=True, component_source_hashes_match=True,
                      component_disagreement_rows_verified=len(disagreements))
    if (folder / 'field-factors.npz').exists():
        from field_factor import field_delays
        times = np.full((len(meta), 5), np.nan)
        for i, item in enumerate(meta):
            for j, value in enumerate(item['times']):
                if value and sector_data[i][j, 12] == 0 and sector_data[i][j, 13] == 0:
                    times[i, j] = value
        groups = np.array([r['key'].split('/')[0] + '/' + r['category'] for r in meta])
        maximum = 0.
        with np.load(folder / 'field-factors.npz') as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for number, (rank, penalty) in enumerate(manifest['configurations']):
                recomputed = field_delays(times, groups, rank, penalty)
                saved = pack[f'configuration-{number}']
                assert np.array_equal(np.isnan(recomputed), np.isnan(saved))
                maximum = max(maximum, float(np.nanmax(np.abs(recomputed-saved))))
        assert maximum <= 1e-12
        reference = root / 'artifacts/incident-ml-field-objectives-enhanced'
        compared, reference_maximum = 0, 0.
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            for row in read(reference / filename):
                if row['model'] == 'paired-weather-pointwise':
                    value = scores[row['split'], 'context-reference', part][index[row['key']]]
                    reference_maximum = max(reference_maximum, abs(value-float(row['score'])))
                    compared += 1
        assert compared > 0 and reference_maximum <= 1e-12
        result.update(field_configurations_recomputed=len(manifest['configurations']),
            maximum_field_recompute_difference=maximum, reference_scores_compared=compared,
            maximum_reference_difference=reference_maximum)
    if (folder / 'leave-2021-out-sectors.npz').exists():
        valid = np.zeros((len(meta), 5), bool)
        for i, features in enumerate(sector_data):
            if len(features):
                valid[i, :len(features)] = (features[:, 12] == 0) & (features[:, 13] == 0)
        localized = read(folder / 'localization.csv')
        sector_points = 0
        for split, (_, test) in partitions.items():
            with np.load(folder / f'{split}-sectors.npz') as pack:
                assert pack['keys'].tolist() == [r['key'] for r in meta]
                for model in pack.files:
                    if model == 'keys':
                        continue
                    instances = pack[model]
                    values = np.where(valid, instances, 0.).max(axis=1)
                    for part in ['cal', 'test']:
                        for i, value in scores[split, model, part].items():
                            assert math.isclose(values[i], value, abs_tol=1e-12)
                            sector_points += 1
                    for row in [r for r in localized if r['split'] == split and r['model'] == model]:
                        labels = {i: (meta[i]['known_sectors'] if row['label_source'] == 'original'
                                      else reviews.get(int(i), {}).get('sectors', [])) for i in test}
                        eligible = [i for i in test if valid[i].any() and labels[i] and
                            (y[i] if row['label_source'] == 'original' else
                             reviews.get(int(i), {}).get('strict', False) and reviews[int(i)]['decision'] == 'yes')]
                        exact = sum(int(np.argmax(np.where(valid[i], instances[i], -np.inf))) + 1 in labels[i]
                                    for i in eligible)
                        assert len(eligible) == int(row['labelled_runs']) and exact == int(row['exact'])
        result.update(sector_bag_scores_verified=sector_points, localization_rows_verified=len(localized))
        tuning = read(folder / 'tuning.csv')
        for choice in manifest['selections']:
            trials = [r for r in tuning if r['split'] == choice['split'] and r['model'] == choice['model']]
            best = max(trials, key=lambda r: float(r['calibration_recall']))
            assert math.isclose(float(best['calibration_recall']), choice['calibration_objective'])
            if choice['model'] == 'reviewed-blend':
                assert best['component'] == choice['component'] and float(best['weight']) == choice['weight']
            else:
                assert int(best['leaves']) == choice['leaves']
        result['calibration_selections_verified'] = len(manifest['selections'])
        repeat = folder.with_name(folder.name + '-failed-csv') / 'leave-2021-out-sectors.npz'
        if repeat.exists():
            with np.load(repeat) as old, np.load(folder / repeat.name) as new:
                assert old['keys'].tolist() == new['keys'].tolist()
                maximum = max(float(np.max(np.abs(old[k] - new[k]))) for k in old.files if k != 'keys')
                assert maximum <= 1e-12
                result.update(first_fold_repeated_models=len(old.files)-1,
                              maximum_first_fold_repeat_difference=maximum)
    (folder / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
