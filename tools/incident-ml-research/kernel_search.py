"""Independent CPU search: kernel boundaries and known-positive pattern distance."""
import argparse
import itertools
import json
import pickle
import time
from pathlib import Path
import numpy as np
from sklearn.svm import SVC
from sklearn.neighbors import NearestNeighbors
from sklearn.preprocessing import RobustScaler
from threadpoolctl import threadpool_limits
from data import load_dataset, FEATURE_NAMES, sha
from focus import relative_columns
from trajectory import prepare, historical_features
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def distance_contrast(positive_distance, unknown_distance):
    return np.log(np.maximum(unknown_distance, 1e-8))-np.log(np.maximum(positive_distance, 1e-8))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-kernels'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, records, _ = prepare(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, predictions, subgroups, selections = [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        latest = max(years[outer])
        fit, cal = outer[years[outer] != latest], outer[years[outer] == latest]
        history = historical_features(records, meta, fit)
        views = {'relative': x[:, relative_columns(FEATURE_NAMES)],
                 'shape-history': np.hstack([shape, history])}
        p, u = fit[y[fit]], fit[~y[fit]]
        rng = np.random.default_rng(SEED)
        controls = rng.choice(u, min(len(u), 1500), replace=False)
        selected = np.concatenate([p, controls])
        scores, best_global = {}, None
        for view, features in views.items():
            scaler = RobustScaler(quantile_range=(10., 90.)).fit(features[fit])
            transformed = np.clip(scaler.transform(features), -10., 10.)
            best = None
            for c, gamma in itertools.product([.1, 1., 10.], ['scale', .01, .1]):
                model = SVC(C=c, gamma=gamma, class_weight='balanced', cache_size=256, random_state=SEED)
                model.fit(transformed[selected], y[selected])
                values = model.decision_function(transformed[cal])
                threshold = threshold_for_budget(values, 700/20340)
                objective = float(np.mean(values[y[cal]] > threshold))
                tuning.append(dict(split=split, family=f'svm-{view}', parameter=f'C={c},gamma={gamma}',
                                   calibration_recall=objective, calibration_year=int(latest)))
                if best is None or objective > best[0]:
                    best = objective, model, c, gamma
            name = f'svm-{view}'
            scores[name] = best[1].decision_function(transformed)
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(model=best[1], scaler=scaler, c=best[2], gamma=best[3]), handle)
            if best_global is None or best[0] > best_global[0]:
                best_global = best[0], name
            # All unlabelled fit observations remain eligible controls. The
            # cap is a deterministic uniform computational sample, not a clean set.
            reference = rng.choice(u, min(len(u), 3000), replace=False)
            maximum = min(30, len(p), len(reference))
            pos_nn = NearestNeighbors(n_neighbors=maximum, algorithm='brute', n_jobs=2).fit(transformed[p])
            unknown_nn = NearestNeighbors(n_neighbors=maximum, algorithm='brute', n_jobs=2).fit(transformed[reference])
            dp = pos_nn.kneighbors(transformed, return_distance=True)[0]
            du = unknown_nn.kneighbors(transformed, return_distance=True)[0]
            for kind in ['positive-distance', 'distance-contrast']:
                candidate = None
                for k in [1, 3, 7, 15, 30]:
                    if k > maximum:
                        continue
                    positive_distance = dp[:, :k].mean(axis=1)
                    values = (-positive_distance if kind == 'positive-distance'
                              else distance_contrast(positive_distance, du[:, :k].mean(axis=1)))
                    threshold = threshold_for_budget(values[cal], 700/20340)
                    objective = float(np.mean(values[cal][y[cal]] > threshold))
                    name = f'{kind}-{view}'
                    tuning.append(dict(split=split, family=name, parameter=f'k={k}',
                                       calibration_recall=objective, calibration_year=int(latest)))
                    if candidate is None or objective > candidate[0]:
                        candidate = objective, values, k
                scores[name] = candidate[1]
                if candidate[0] > best_global[0]:
                    best_global = candidate[0], name
                with (out/f'{split}-{name}.pickle').open('wb') as handle:
                    pickle.dump(dict(positive_reference=pos_nn, unknown_reference=unknown_nn,
                                     scaler=scaler, neighbours=candidate[2], kind=kind), handle)
            print(f'{split}: kernel and neighbour candidates completed for {view}', flush=True)
        scores['selected-kernel'] = scores[best_global[1]]
        selections.append(dict(split=split, selected=best_global[1], calibration_recall=best_global[0]))
        for name, values in scores.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field,
                            value=value, **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
        write_csv(out/'metrics.csv', rows)
        write_csv(out/'tuning.csv', tuning)
    pooled = []
    for name in scores:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                               recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for filename, values in [('pooled', pooled), ('predictions', predictions), ('subgroups', subgroups)]:
        write_csv(out/f'{filename}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(seed=SEED, input_hashes=hashes, selections=selections,
        elapsed_seconds=time.perf_counter()-started, protected_sources_unchanged=True,
        goal_passed=any(r['goal_passed'] for r in pooled), source_hash=sha(Path(__file__)),
        exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Kernel and positive-pattern neighbour search', '',
        'Two feature views: original relative-only features, and aligned run shape with strictly earlier '
        'fit-sourced same-category history. Robust scaling uses fit data only (10th/90th percentiles), '
        'with transformed values clipped to [-10,10]. RBF SVM searches C=0.1/1/10 and gamma=scale/0.01/0.1. '
        'Observed positives are compared with 1,500 uniformly sampled unlabelled fit controls, never verified '
        'clean negatives. Neighbour models use k=1/3/7/15/30: distance to observed-positive patterns, or '
        'log mean distance to unlabelled controls minus log distance to positives. Unlabelled reference '
        'is capped at 3,000 by deterministic uniform sampling. IDs do not enter distances.', '',
        'All 38 view/parameter configurations per outer partition and the cross-family selection use only '
        'calibration recall at 700/20,340. No test quota or test-top-700 threshold. Same five predeclared '
        'calibration targets and eight season tests. No precision, FPR or crash probability is identified. '
        'Prior inspection makes these exploratory, not fresh independent validation.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
            [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### Kernel search: {split}', '', table(['Model','Target','Hits','Positives','Alarms'],
            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. No production changes. Full choices, scores and subgroup "
              'counts are in `artifacts/incident-ml-kernels/`.', '']
    (out/'kernels.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled, indent=2), flush=True)


if __name__ == '__main__':
    with threadpool_limits(limits=2):
        main()
