"""Within-field observed-label ranking and event-balanced PU controls; offline."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from lightgbm import LGBMClassifier, LGBMRanker
from data import load_dataset, sha
from objectives import calibration_partition, balanced_observation_weights, grouped_rows, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def query_keys(meta, scope):
    if scope == 'session':
        return np.array([r['key'].split('/')[0] for r in meta])
    if scope == 'round-category':
        return np.array([f"{r['group']}/{r['category']}" for r in meta])
    raise ValueError(f'Unsupported query scope: {scope}')


def event_weights(y, queries):
    """Equal observed-label mass, then equal event mass within each observed class."""
    weights = np.zeros(len(y))
    for target in [True, False]:
        take = y == target
        if not take.any():
            raise ValueError('Both observed-positive and unlabelled rows are required')
        _, inverse, counts = np.unique(queries[take], return_inverse=True, return_counts=True)
        weights[take] = len(y) / (2. * len(counts) * counts[inverse])
    return weights


def percentile_scores(calibration, values):
    ordered = np.sort(calibration)
    return (np.searchsorted(ordered, values, side='left') +
            np.searchsorted(ordered, values, side='right')) / (2. * len(ordered))


def fit_candidate(features, y, queries, kind, leaves, objective='lambdarank', truncation=10):
    parameters = dict(n_estimators=300, num_leaves=leaves, learning_rate=.025,
        min_child_samples=35, reg_lambda=5., n_jobs=2, deterministic=True,
        force_col_wise=True, verbosity=-1, random_state=SEED)
    if kind in ['pointwise', 'event-balanced']:
        model = LGBMClassifier(**parameters)
        weights = (event_weights(y, queries) if kind == 'event-balanced'
                   else balanced_observation_weights(y))
        model.fit(features, y.astype(int), sample_weight=weights)
    else:
        order, sizes = grouped_rows(queries)
        model = LGBMRanker(objective=objective, label_gain=[0, 1],
                          lambdarank_truncation_level=truncation, **parameters)
        # All fit rows remain present. Uniform labels offer no observed-label
        # contrast; XE-NDCG can still produce gradients through tie randomization.
        model.fit(features[order], y[order].astype(int), group=sizes)
    return model


def calibration_objective(values, positive):
    threshold = threshold_for_budget(values, 700 / 20340)
    return float(np.mean(values[positive] > threshold))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-field-objectives'))
    parser.add_argument('--paired-enhanced', action='store_true')
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    original, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, availability = prepare_pairs(root, meta, enhanced=args.paired_enhanced)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    queries = {scope: query_keys(meta, scope) for scope in ['session', 'round-category']}
    rows, tuning, predictions, calibration_scores, subgroups, selections, contrasts = [], [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        views = {'timing': original, 'paired-weather': np.hstack([original, paired, weather])}
        for scope, ids in queries.items():
            unique = np.unique(ids[fit])
            no_contrast = [key for key in unique if len(np.unique(y[fit][ids[fit] == key])) < 2]
            contrasts.append(dict(split=split, scope=scope, queries=len(unique),
                uniform_label_queries=len(no_contrast),
                uniform_label_rows=int(np.isin(ids[fit], no_contrast).sum())))
        selected = {}
        for view, features in views.items():
            for kind in ['pointwise', 'event-balanced']:
                best = None
                for leaves in [7, 15]:
                    model = fit_candidate(features[fit], y[fit], queries['round-category'][fit], kind, leaves)
                    values = score(model, features)
                    objective = calibration_objective(values[cal], y[cal])
                    parameters = dict(kind=kind, leaves=leaves)
                    name = f'{view}-{kind}'
                    tuning.append(dict(split=split, model=name, parameters=json.dumps(parameters), objective=objective))
                    if best is None or objective > best[0]:
                        best = objective, values, dict(model=model, view=view, **parameters)
                selected[name] = best
            point = selected[f'{view}-pointwise'][1]
            point_percentile = percentile_scores(point[cal], point)
            for scope in queries:
                best = None
                configurations = [(leaves, 'lambdarank', truncation) for leaves in [7, 15] for truncation in [5, 20]]
                configurations += [(leaves, 'rank_xendcg', 10) for leaves in [7, 15]]
                for leaves, objective_name, truncation in configurations:
                    model = fit_candidate(features[fit], y[fit], queries[scope][fit],
                                          'rank', leaves, objective_name, truncation)
                    values = score(model, features)
                    objective = calibration_objective(values[cal], y[cal])
                    parameters = dict(kind='rank', leaves=leaves, objective=objective_name,
                                      truncation=truncation, scope=scope)
                    name = f'{view}-{scope}-rank'
                    tuning.append(dict(split=split, model=name, parameters=json.dumps(parameters), objective=objective))
                    if best is None or objective > best[0]:
                        best = objective, values, dict(model=model, view=view, **parameters)
                selected[name] = best
                rank_percentile = percentile_scores(best[1][cal], best[1])
                blended = None
                for weight in [.25, .5, .75]:
                    values = weight * rank_percentile + (1. - weight) * point_percentile
                    objective = calibration_objective(values[cal], y[cal])
                    blend_name = f'{view}-{scope}-blend'
                    tuning.append(dict(split=split, model=blend_name,
                        parameters=json.dumps(dict(rank_weight=weight)), objective=objective))
                    pack = dict(view=view, rank_name=name, point_name=f'{view}-pointwise', rank_weight=weight,
                                rank_calibration=best[1][cal], point_calibration=point[cal])
                    if blended is None or objective > blended[0]:
                        blended = objective, values, pack
                selected[blend_name] = blended
            print(f'{split}: {view} pointwise, event-balanced and within-field objectives evaluated', flush=True)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models={name: result[2] for name, result in selected.items()}, weather_names=names), handle)
        for name, (objective, values, pack) in selected.items():
            selections.append(dict(split=split, model=name, calibration_objective=objective,
                parameters={k: v for k, v in pack.items() if k not in ['model', 'rank_calibration', 'point_calibration']},
                fit_rows=len(fit), calibration_rows=len(cal)))
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session', 'paired_reference']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits / int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores),
                          ('subgroups', subgroups), ('query-contrasts', contrasts)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hash=sha(Path(__file__)), selections=selections,
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter() - started,
        goal_passed=any(r['goal_passed'] for r in pooled), paired_enhanced=args.paired_enhanced,
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Within-field ranking and event-balanced observed-label learning', '',
        'LambdaRank (leaves 7/15, truncation 5/20) and XE-NDCG (leaves 7/15) compare observed positives '
        'against contaminated unlabelled controls within the same session or round/category. Query IDs '
        'only group fit comparisons and never enter predictors. Categories are never mixed within a '
        'query. Uniform-label queries offer no observed-label contrast; their counts are exported. '
        'LambdaRank has no unequal-label pair in such a query, but an explicit native-library '
        'characterization confirms that XE-NDCG still fits nonzero scores through tie randomization. '
        'Neither is evidence of a clean query. This can amplify reporting-selection bias and does not identify clean '
        'qualifying runs or physical-crash probabilities.', '',
        'Separate event-balanced classification equalizes round/category mass within each observed '
        'class. A pointwise reference retains equal total positive/unlabelled mass. Calibration-only '
        'midrank CDFs blend rank and pointwise scores with rank weights .25/.5/.75; ties remain ties. '
        'The blend retains a pointwise anchor for fields without annotated positives. It does not '
        'turn unlabelled controls into known negatives. Both timing-only and original+paired+weather '
        'views are evaluated. Earlier references require strictly earlier dates; weather encoding '
        'is fit-only. No explicit stage, rider identity, event ID or final result is a predictor.', '',
        'Same eight held-year/chronological tests, grouped-round calibration and five fixed budgets. '
        'Model/weight/threshold selection never uses test labels or test-top-700 quotas. These '
        'previously inspected seasons are exploratory; no independent deployment validation is claimed.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    lines += [f"Enhanced paired change/rank features enabled: **{args.paired_enhanced}**.", '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Field-objective study: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. No production source changed; saved scores, models "
              'and query-contrast diagnostics are under `artifacts/incident-ml-field-objectives/`.', '']
    (out / 'field-objectives.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
