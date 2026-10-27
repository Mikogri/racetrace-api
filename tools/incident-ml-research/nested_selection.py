"""Choose recipes inside fit rounds; reserve outer calibration only for thresholds."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from sklearn.model_selection import GroupKFold
from data import load_dataset, sha
from objectives import calibration_partition, score
from field_objectives import fit_candidate, query_keys
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS

SELECTION_BUDGETS = [.025, .03, 700 / 20340]


def inner_splits(fit, meta):
    groups = np.array([meta[i]['group'] for i in fit])
    for training, held in GroupKFold(n_splits=3).split(fit, groups=groups):
        yield fit[training], fit[held]


def selection_objective(folds):
    positives = sum(p for _, p in folds)
    if positives <= 0:
        raise ValueError('No positive observations in inner validation')
    return float(sum(sum(hits) for hits, _ in folds) / (len(folds[0][0]) * positives))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-nested-selection'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    queries = query_keys(meta, 'round-category')
    def features_for(indices):
        names = sorted({conditions[i] for i in indices})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        return np.hstack([x, paired, weather]), names
    reference_folder = root / 'artifacts/incident-ml-field-objectives-enhanced'
    assert hashes == json.loads((reference_folder / 'manifest.json').read_text())['input_hashes']
    index = {r['key']: i for i, r in enumerate(meta)}
    reference_names = {'paired-weather-pointwise': 'reference-pointwise',
                       'paired-weather-round-category-rank': 'reference-rank'}
    reference, reference_keys = {}, {}
    for file in ['predictions.csv', 'calibration-scores.csv']:
        for r in csv.DictReader((reference_folder / file).open(encoding='utf-8')):
            if r['model'] not in reference_names:
                continue
            key = r['split'], reference_names[r['model']]
            reference.setdefault(key, np.full(len(meta), np.nan))[index[r['key']]] = float(r['score'])
            reference_keys.setdefault((file, *key), set()).add(r['key'])
    configs = {'pointwise': [dict(kind='pointwise', leaves=leaves) for leaves in [7, 15]],
               'rank': [dict(kind='rank', leaves=leaves, objective='lambdarank', truncation=truncation)
                        for leaves in [7, 15] for truncation in [5, 20]] +
                       [dict(kind='rank', leaves=leaves, objective='rank_xendcg', truncation=10)
                        for leaves in [7, 15]]}
    rows, tuning, predictions, calibration_scores, subgroups, selections, partitions = [], [], [], [], [], [], []
    partition_arrays = {}
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        counts = {(kind, trial): [] for kind in configs for trial in range(len(configs[kind]))}
        coverage = np.zeros(len(meta), int)
        for fold, (inner, held) in enumerate(inner_splits(fit, meta)):
            assert not set(inner) & set(held)
            assert not {meta[i]['group'] for i in inner} & {meta[i]['group'] for i in held}
            assert not set(np.concatenate([inner, held])) & set(np.concatenate([cal, test]))
            coverage[held] += 1
            partition_arrays[f'{split}-{fold}-fit'] = inner
            partition_arrays[f'{split}-{fold}-held'] = held
            partitions.append(dict(split=split, fold=fold, fit_rows=len(inner), validation_rows=len(held),
                positives=int(y[held].sum()), fitting_groups=json.dumps(sorted({meta[i]['group'] for i in inner})),
                validation_groups=json.dumps(sorted({meta[i]['group'] for i in held}))))
            features, names = features_for(inner)
            for kind, recipes in configs.items():
                for trial, recipe in enumerate(recipes):
                    model = fit_candidate(features[inner], y[inner], queries[inner], **recipe)
                    values = score(model, features[held])
                    hits = [int(np.sum(y[held] & (values > threshold_for_budget(values, b)))) for b in SELECTION_BUDGETS]
                    counts[(kind, trial)].append((hits, int(y[held].sum())))
                    tuning.append(dict(split=split, fold=fold, family=kind, trial=trial,
                        parameters=json.dumps(recipe, sort_keys=True), positives=int(y[held].sum()),
                        hits=json.dumps(hits), encoding_names=json.dumps(names)))
            print(f'{split}: recipe-selection fold {fold+1}/3 complete', flush=True)
        assert np.all(coverage[fit] == 1) and coverage[cal].sum() == 0 and coverage[test].sum() == 0
        features, names = features_for(fit)
        scores, models = {}, {}
        for kind, recipes in configs.items():
            objectives = [selection_objective(counts[(kind, i)]) for i in range(len(recipes))]
            chosen = max(range(len(recipes)), key=lambda i: objectives[i])
            model = fit_candidate(features[fit], y[fit], queries[fit], **recipes[chosen])
            name = f'nested-{kind}'
            scores[name] = score(model, features)
            models[name] = dict(model=model, recipe=recipes[chosen])
            selections.append(dict(split=split, model=name, trial=chosen, parameters=recipes[chosen],
                inner_objective=objectives[chosen], all_inner_objectives=objectives,
                fit_rows=len(fit), calibration_rows=len(cal)))
        for name in reference_names.values():
            assert reference_keys[('predictions.csv', split, name)] == {meta[i]['key'] for i in test}
            assert reference_keys[('calibration-scores.csv', split, name)] == {meta[i]['key'] for i in cal}
            scores[name] = reference[(split, name)]
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, values in scores.items():
            assert np.isfinite(values[np.concatenate([cal, test])]).all()
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        write_csv(out / 'selection-folds.csv', partitions)
        np.savez_compressed(out / 'selection-indices.npz', **partition_arrays)
    pooled = []
    for name in scores:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hash=sha(Path(__file__)), selections=selections,
        outer_calibration_excluded_from_recipe_selection=True, protected_sources_unchanged=True,
        elapsed_seconds=time.perf_counter()-started, goal_passed=any(r['goal_passed'] for r in pooled),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Nested round-group recipe selection', '',
        'This tests whether a small outer calibration set selects unstable recipes. Three GroupKFold '
        'partitions inside outer-fit rounds select pointwise leaves 7/15 or the same six round/category '
        'LambdaRank/XE-NDCG recipes already tested. The objective averages known-positive recall at '
        '2.5%, 3%, 700/20,340 budgets, weighted by inner-validation positive counts. Each fit row is '
        'validated once; all inner indices and groups are saved. Inner weather encoding fits inner '
        'training only. Neither outer calibration nor test labels select the nested recipe.', '',
        'After selection, the model refits all outer-fit rows. Outer calibration is used only for '
        'final threshold ranks, not recipe choice. Fit/cal/test population and five alarm budgets '
        'remain unchanged. Cached outer-calibration-selected pointwise and rank references use '
        'identical frozen inputs and exact matching calibration/test identities. This isolates '
        'selection protocol rather than adding features or test quotas.', '',
        'Features are the same enhanced earlier-session paired/weather context, without identity, '
        'stage or official final-result columns. Unlabelled controls remain contaminated and '
        'unverified. Previously inspected years remain exploratory; a gain does not identify '
        'crash probabilities or establish precision.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Nested selection: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Choices, inner partitions, scores and thresholds "
              'are retained in `artifacts/incident-ml-nested-selection/`. Production remains unchanged.', '']
    (out / 'nested-selection.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700 and r['model'].startswith('nested-')]
    print(json.dumps(dict(best_nested=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
