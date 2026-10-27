"""Predeclared multi-family sector search, then calibration-selected ensembles."""
import argparse
import itertools
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from trajectory import prepare, historical_features
from multiple_instance import instance_features, train_hard, train_soft, instance_scores, bag_scores
from experiment import (evaluation_splits, threshold_for_budget, metrics, write_csv,
                        predict, cluster_interval)
from conditional import goal_passed
from target import BUDGETS


def calibrated_percentile(values, calibration):
    return np.searchsorted(np.sort(calibration), values, side='right')/len(calibration)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-multiple-instance'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sector_data, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, records, diagnostic = prepare(root, meta)
    local, valid = instance_features(x, sector_data, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    known = [r['known_sectors'] for r in meta]
    families = ['hard-sector', 'hard-context', 'hard-mining', 'soft-sector', 'soft-context']
    grid = [(name, leaves, step) for name in families for leaves in [7, 15]
            for step in ([3] if name == 'hard-mining' else [1, 3] if name.startswith('hard') else [0., 1.])]
    tuning, rows, predictions, groups, localizations, selections = [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        year = max(years[outer])
        fit, cal = outer[years[outer] != year], outer[years[outer] == year]
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        history = historical_features(records, meta, fit)
        sector_history = np.concatenate([history[:, :10].reshape(-1, 5, 2),
            np.repeat(history[:, None, 10:], 5, axis=1)], axis=2)
        cohort = shape[:, -10:].reshape(-1, 5, 2)
        small = np.concatenate([local, sector_history, cohort], axis=2)
        contextual = np.concatenate([small, np.repeat(x[:, None, :], 5, axis=1)], axis=2)
        views = {'sector': small, 'context': contextual}
        best = {}
        for number, (family, leaves, parameter) in enumerate(grid):
            features = views['sector' if family.endswith('sector') else 'context']
            if family.startswith('hard'):
                model = train_hard(features[fit], valid[fit], y[fit], [known[i] for i in fit],
                                   leaves, parameter, mine=family == 'hard-mining')
            else:
                model = train_soft(features[fit], valid[fit], y[fit], [known[i] for i in fit], leaves, parameter)
            probabilities = instance_scores(model, features[cal])
            calibration = bag_scores(probabilities, valid[cal], model['pooling'])
            threshold = threshold_for_budget(calibration, 700/20340)
            objective = float(np.mean(calibration[y[cal]] > threshold))
            tuning.append(dict(split=split, family=family, leaves=leaves, parameter=parameter,
                               calibration_recall=objective, feature_count=features.shape[-1],
                               calibration_year=int(year)))
            if family not in best or objective > best[family][0]:
                best[family] = objective, model, features, leaves, parameter
            print(f'{split}: trial {number+1}/{len(grid)} {family} calibration recall {objective:.3f}', flush=True)
        winning_family = max(best, key=lambda k: best[k][0])
        scores, sector_scores = {}, {}
        for family, (objective, model, features, leaves, parameter) in best.items():
            sectors = instance_scores(model, features)
            scores[family] = bag_scores(sectors, valid, model['pooling'])
            sector_scores[family] = sectors
            with (out/f'{split}-{family}.pickle').open('wb') as handle:
                pickle.dump(dict(model=model, leaves=leaves, parameter=parameter), handle)
        scores['selected-mil'] = scores[winning_family]
        sector_scores['selected-mil'] = sector_scores[winning_family]
        with (root/f'artifacts/incident-ml/{split}-lightgbm.pickle').open('rb') as handle:
            scores['original-frozen'] = predict(pickle.load(handle)['models'], x)
        with (root/f'artifacts/incident-ml-trajectory/{split}-shape-history.pickle').open('rb') as handle:
            model = pickle.load(handle)['model']
            scores['shape-history'] = model.booster_.predict(np.hstack([shape, history]))
        components = ['original-frozen', 'shape-history', 'selected-mil']
        percentiles = np.array([calibrated_percentile(scores[name], scores[name][cal]) for name in components])
        ensemble = None
        for weights in itertools.product(range(5), repeat=3):
            if sum(weights) != 4:
                continue
            values = np.array(weights) @ percentiles / 4.
            threshold = threshold_for_budget(values[cal], 700/20340)
            objective = float(np.mean(values[cal][y[cal]] > threshold))
            if ensemble is None or objective > ensemble[0]:
                ensemble = objective, values, weights
        scores['selected-ensemble'] = ensemble[1]
        selections.append(dict(split=split, mil_family=winning_family, calibration_recall=best[winning_family][0],
                               ensemble_weights=ensemble[2], ensemble_calibration_recall=ensemble[0]))
        for family, values in scores.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=family, budget=budget, threshold=threshold,
                                 **metrics(y[test], flagged, baseline[test]),
                                 recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        groups.append(dict(split=split, model=family, budget=budget, field=field,
                            value=value, **metrics(y[test][take], flagged[take], baseline[test][take])))
                if family in sector_scores:
                    eligible = [i for i in test if y[i] and known[i]]
                    flags = dict(zip(test, flagged))
                    exact = adjacent = detected = 0
                    for i in eligible:
                        if not flags[i] or not valid[i].any():
                            continue
                        detected += 1
                        candidate = int(np.argmax(np.where(valid[i], sector_scores[family][i], -np.inf)))+1
                        exact += candidate in known[i]
                        adjacent += any(abs(candidate-k) <= 1 for k in known[i])
                    localizations.append(dict(split=split, model=family, budget=budget,
                        labelled_runs=len(eligible), detected=detected, exact=int(exact), adjacent=int(adjacent)))
            predictions.extend(dict(split=split, model=family, key=meta[i]['key'], score=float(values[i])) for i in test)
        write_csv(out/'metrics.csv', rows)
        write_csv(out/'tuning.csv', tuning)
        print(f'{split}: outer test completed; next partition', flush=True)
    pooled = []
    for family in scores:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == family and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=family, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                               recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('pooled', pooled), ('predictions', predictions), ('subgroups', groups), ('localization', localizations)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(grid=grid, selections=selections, input_hashes=hashes, protected_sources_unchanged=True,
                    elapsed_seconds=time.perf_counter()-started, goal_passed=any(r['goal_passed'] for r in pooled),
                    exploratory_reuse_of_previously_inspected_years=True,
                    source_hashes={p:sha(Path(__file__).with_name(p)) for p in ['search.py','multiple_instance.py','trajectory.py']})
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Multiple-instance sector search and calibration-selected ensembles', '',
        f'Each outer partition searches {len(grid)} predeclared sector-model configurations, then 15 ensemble '
        'weight triples. Choice uses calibration labels only; no outer-test score/label selects a configuration '
        'or threshold. Years have been inspected in earlier research: this is still exploratory, not a fresh '
        'independent final validation.', '',
        '`hard-*` alternates between one latent candidate sector per run and a LightGBM classifier. Explicit '
        'sector labels anchor fit bags only; they are never supplied to test inference. Other sectors of a '
        'positive run are not labelled negative. `hard-mining` adds high-scoring unlabelled controls. '
        '`soft-*` optimizes a noisy-OR bag loss directly, with exact bag gradients and a positive Gauss-Newton '
        'diagonal; explicit-sector positive anchors have strength 0 or 1. The observed-label loss is not an '
        'identified physical-crash risk. Unlabelled controls remain contaminated, not verified clean.', '',
        'Sector features combine measured contrasts, pre/post shape, strictly fit-sourced prior-round history '
        'and same-cohort rank contrasts. Context variants add the original 71 run features. Missing and '
        'compensating-checkpoint sectors are excluded from candidate inference; an empty bag scores zero. '
        'Localizing a sector does not validate removable time loss. No correction enters production.', '',
        'Ensembles combine original, shape/history and calibration-selected MIL scores after calibration-only '
        'empirical-CDF mapping. Integer weights sum to four. There is no test-top-700 selection. '
        'All five calibration budgets were declared before fitting. Threshold transfer may change actual '
        'alarm rates. No precision/FPR or calibrated crash-probability claim is made.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
              [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### MIL search: {split}', '', table(['Model','Target','Hits','Positives','Alarms'],
            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Protected production source hashes are unchanged. "
              'Full calibration choices, per-run predictions, subgroup counts, explicit-sector localization '
              'and conditional event-bootstrap recall intervals are in `artifacts/incident-ml-multiple-instance/`.', '']
    (out/'multiple-instance.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled, indent=2), flush=True)


if __name__ == '__main__':
    main()
