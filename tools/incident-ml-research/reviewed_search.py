"""Fold-restricted human supervision; retain the original 341-positive benchmark."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from lightgbm import LGBMClassifier
from data import load_dataset, sha
from review_supervision import match_reviews, training_labels
from hybrid_search import training_weights
from objectives import calibration_partition, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def fit_reviewed(features, target, negative, fit, leaves, multiplier):
    model = LGBMClassifier(n_estimators=300, num_leaves=leaves, learning_rate=.025,
        min_child_samples=35, reg_lambda=5., n_jobs=2, deterministic=True,
        force_col_wise=True, verbosity=-1, random_state=SEED)
    weights = training_weights(target[fit], negative[fit], multiplier)
    model.fit(features[fit], target[fit].astype(int), sample_weight=weights)
    return model


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--reviews', type=Path, default=Path('artifacts/incident-human-review/annotations.json'))
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-reviewed'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    annotation_hash = sha(args.reviews)
    annotation = json.loads(args.reviews.read_text(encoding='utf-8'))
    reviews, audit = match_reviews(meta, annotation['rows'])
    write_csv(out / 'review-audit.csv', audit)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    reference_folder = root / 'artifacts/incident-ml-field-objectives-enhanced'
    assert hashes == json.loads((reference_folder / 'manifest.json').read_text())['input_hashes']
    index = {r['key']: i for i, r in enumerate(meta)}
    reference = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        for r in csv.DictReader((reference_folder / filename).open(encoding='utf-8')):
            if r['model'] == 'paired-weather-pointwise':
                reference.setdefault(r['split'], np.full(len(meta), np.nan))[index[r['key']]] = float(r['score'])
    rows, tuning, predictions, calibration_scores, subgroups, selections, usage = [], [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        quarantined = [int(i) for i in fit if reviews.get(int(i), {}).get('conflict', False)]
        eligible = np.array([i for i in fit if int(i) not in quarantined], int)
        names = sorted({conditions[i] for i in eligible})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        features = np.hstack([x, paired, weather])
        values_by_name = {'reference': reference[split]}
        models = {}
        for mode in ['quarantine-only', 'negative-only', 'reviewed-labels']:
            if mode == 'quarantine-only':
                target, negative = y.copy(), np.zeros(len(y), bool)
                multipliers = [1.]
            else:
                target, negative = training_labels(y, eligible, reviews, promote=mode == 'reviewed-labels')
                multipliers = [10., 50.]
            assert not np.any(target[np.concatenate([cal, test])] != y[np.concatenate([cal, test])])
            assert not negative[np.concatenate([cal, test])].any()
            assert all(i in set(eligible) for i in np.flatnonzero(target != y))
            best = None
            for leaves in [7, 15]:
                for multiplier in multipliers:
                    model = fit_reviewed(features, target, negative, eligible, leaves, multiplier)
                    values = score(model, features)
                    threshold = threshold_for_budget(values[cal], 700 / 20340)
                    objective = float(np.mean(values[cal][y[cal]] > threshold))
                    tuning.append(dict(split=split, model=mode, leaves=leaves, multiplier=multiplier,
                        calibration_recall=objective, fitting_rows=len(eligible),
                        positive_additions=int(np.sum(target[eligible] & ~y[eligible])),
                        reviewed_negatives=int(negative[eligible].sum())))
                    if best is None or objective > best[0]:
                        best = objective, model, values, leaves, multiplier
            values_by_name[mode] = best[2]
            models[mode] = dict(model=best[1], leaves=best[3], multiplier=best[4])
            selections.append(dict(split=split, model=mode, leaves=best[3], multiplier=best[4],
                calibration_objective=best[0]))
            usage.append(dict(split=split, model=mode,
                eligible_fit_keys=[meta[i]['key'] for i in eligible],
                quarantined_fit_keys=[meta[i]['key'] for i in quarantined],
                positive_added_keys=[meta[i]['key'] for i in np.flatnonzero(target & ~y)],
                negative_weighted_keys=[meta[i]['key'] for i in np.flatnonzero(negative)],
                calibration_targets_unchanged=True, test_targets_unchanged=True))
            print(f'{split}: {mode} evaluated', flush=True)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, values in values_by_name.items():
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
    pooled = []
    for name in values_by_name:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    (out / 'review-usage.json').write_text(json.dumps(usage, indent=2), encoding='utf-8')
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert sha(args.reviews) == annotation_hash
    manifest = dict(input_hashes=hashes, annotation_hash=annotation_hash,
        workbook_sha256=annotation['workbook_sha256'], source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), root / 'tools/incident-ml-research/review_supervision.py']}, selections=selections,
        original_benchmark_positives=int(y.sum()), protected_sources_unchanged=True,
        elapsed_seconds=time.perf_counter()-started, goal_passed=any(r['goal_passed'] for r in pooled),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Human-reviewed auxiliary supervision', '',
        'The user completed 60 purposefully sampled blind cases: 46 yes, 14 no, 39 sector annotations. '
        'All displayed measurements match the original CSV, and the XLSX source is read-only. Six '
        'human negatives conflict with the frozen positive reference; they are quarantined from '
        'training only, never removed or relabelled in calibration/test metrics. Outcome-only '
        'DNF/winner explanations are not treated as independent evidence for new strict labels. '
        'The original 20,340-run / 341-positive benchmark remains unchanged.', '',
        'Strict annotations supply 21 additional positive reviews and 8 negative reviews. They '
        'are human-adjudicated, not independently video-verified by this program. Unknown controls '
        'remain unverified, and no overall precision/FPR can be estimated from this selected sample. '
        'Notes, identities, status, review membership and sector labels are not prediction features.', '',
        'A quarantine-only control isolates removal of disputed fitting labels. Negative-only '
        'upweights strict reviewed negatives by 10/50 while retaining balanced class mass. '
        'Reviewed-labels also promotes new positive annotations within fit rows only. Leaves 7/15 '
        'and multipliers use calibration selection. Every annotated test/calibration run is excluded '
        'from supervision for its fold; exact used/quarantined keys are retained. Input features '
        'are unchanged enhanced paired/weather context. Same eight season/forward checks and five '
        'budgets, no test quota. Sector annotations are preserved for subsequent localization work, '
        'not silently assigned to every sector of a positive run.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Reviewed supervision: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Source hashes, label conflicts, exact fold "
              'usage and scores stay in `artifacts/incident-ml-reviewed/`. Production remains unchanged.', '']
    (out / 'reviewed.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible_rows = [r for r in pooled if r['alarms'] <= 700 and r['model'] != 'reference']
    print(json.dumps(dict(best_reviewed=max(eligible_rows, key=lambda r: r['hits']) if eligible_rows else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
