"""Sector-localized human supervision versus run-only labels, on frozen inputs."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from reviewed_sector import prepare_fit, train_reviewed
from review_supervision import match_reviews
from multiple_instance import instance_features, instance_scores, bag_scores
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from objectives import calibration_partition
from field_objectives import percentile_scores
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def write_tuning(path, rows):
    empty = dict.fromkeys((key for row in rows for key in row), '')
    write_csv(path, [empty | row for row in rows])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--reviews', type=Path, default=Path('artifacts/incident-human-review/annotations.json'))
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-reviewed-sectors'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sector_data, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    annotation_hash = sha(args.reviews)
    annotation = json.loads(args.reviews.read_text(encoding='utf-8'))
    reviews, audit = match_reviews(meta, annotation['rows'])
    write_csv(out / 'review-audit.csv', audit)
    local, valid = instance_features(x, sector_data, meta)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    known = [r['known_sectors'] for r in meta]
    reference_folder = root / 'artifacts/incident-ml-field-objectives-enhanced'
    assert hashes == json.loads((reference_folder / 'manifest.json').read_text())['input_hashes']
    index = {r['key']: i for i, r in enumerate(meta)}
    reference = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        for r in csv.DictReader((reference_folder / filename).open(encoding='utf-8')):
            if r['model'] == 'paired-weather-round-category-rank':
                reference.setdefault(r['split'], np.full(len(y), np.nan))[index[r['key']]] = float(r['score'])
    rows, tuning, predictions, calibration_scores, selections, usage, localization, subgroups = ([] for _ in range(8))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        eligible, _, _, _ = prepare_fit(y, known, fit, reviews, False)
        names = sorted({conditions[i] for i in eligible})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather])
        features = np.concatenate([local, np.repeat(context[:, None, :], 5, axis=1)], axis=2)
        values_by_name, sector_scores, saved_models = {'reference-rank': reference[split]}, {}, {}
        for mode in ['original-mil', 'reviewed-runs', 'reviewed-sectors']:
            if mode == 'original-mil':
                fitting, target, negative = fit, y.copy(), np.zeros(len(y), bool)
                anchors = [list(known[i]) if i in set(fit) else [] for i in range(len(y))]
            else:
                fitting, target, negative, anchors = prepare_fit(y, known, fit, reviews, mode == 'reviewed-sectors')
            outside = np.setdiff1d(np.arange(len(y)), fitting)
            assert not negative[outside].any() and not np.any(target[outside] != y[outside])
            assert all(not anchors[i] for i in outside)
            usage.append(dict(split=split, model=mode, fit_keys=[meta[i]['key'] for i in fitting],
                positive_added_keys=[meta[i]['key'] for i in np.flatnonzero(target & ~y)],
                negative_weighted_keys=[meta[i]['key'] for i in np.flatnonzero(negative)],
                anchors={meta[i]['key']: anchors[i] for i in fitting if anchors[i]}))
            best = None
            for leaves in [7, 15]:
                model = train_reviewed(features[fitting], valid[fitting], target[fitting],
                    [anchors[i] for i in fitting], negative[fitting], leaves, 10.)
                sectors = instance_scores(model, features)
                values = bag_scores(sectors, valid, 'max')
                threshold = threshold_for_budget(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=mode, leaves=leaves,
                    calibration_recall=objective, fitting_rows=len(fitting),
                    anchored_bags=sum(bool(anchors[i]) for i in fitting if target[i]),
                    positive_additions=int(np.sum(target[fitting] & ~y[fitting])),
                    reviewed_negatives=int(negative[fitting].sum())))
                if best is None or objective > best[0]:
                    best = objective, model, values, sectors, leaves
            _, model, values, sectors, leaves = best
            values_by_name[mode], sector_scores[mode] = values, sectors
            saved_models[mode] = dict(model=model, leaves=leaves)
            selections.append(dict(split=split, model=mode, leaves=leaves, calibration_objective=best[0]))
            print(f'{split}: {mode} complete', flush=True)
        reference_percentile = percentile_scores(reference[split][cal], reference[split])
        blend = None
        for mode in ['reviewed-runs', 'reviewed-sectors']:
            component = percentile_scores(values_by_name[mode][cal], values_by_name[mode])
            for weight in [.25, .5, .75]:
                values = weight * component + (1. - weight) * reference_percentile
                threshold = threshold_for_budget(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model='reviewed-blend', component=mode,
                                   weight=weight, calibration_recall=objective))
                if blend is None or objective > blend[0]:
                    blend = objective, values, mode, weight
        values_by_name['reviewed-blend'] = blend[1]
        selections.append(dict(split=split, model='reviewed-blend', component=blend[2],
                               weight=blend[3], calibration_objective=blend[0]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=saved_models, weather_names=names, blend_component=blend[2],
                             blend_weight=blend[3]), handle)
        np.savez_compressed(out / f'{split}-sectors.npz', keys=np.array([r['key'] for r in meta]), **sector_scores)
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
            if name in sector_scores:
                for label_source in ['original', 'human']:
                    labelled = [i for i in test if valid[i].any() and
                        (bool(known[i]) and y[i] if label_source == 'original' else
                         i in reviews and reviews[i]['strict'] and reviews[i]['decision'] == 'yes' and
                         bool(reviews[i]['sectors']))]
                    exact = sum(int(np.argmax(np.where(valid[i], sector_scores[name][i], -np.inf))) + 1 in
                        (known[i] if label_source == 'original' else reviews[i]['sectors']) for i in labelled)
                    localization.append(dict(split=split, model=name, label_source=label_source,
                                             labelled_runs=len(labelled), exact=int(exact)))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_tuning(out / 'tuning.csv', tuning)
    pooled = []
    for name in values_by_name:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores),
                          ('localization', localization), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    (out / 'review-usage.json').write_text(json.dumps(usage, indent=2), encoding='utf-8')
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert sha(args.reviews) == annotation_hash
    manifest = dict(input_hashes=hashes, annotation_hash=annotation_hash,
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('reviewed_sector.py'),
            Path(__file__).with_name('review_supervision.py'), Path(__file__).with_name('multiple_instance.py')]},
        workbook_sha256=annotation['workbook_sha256'], selections=selections,
        original_benchmark_positives=int(y.sum()), protected_sources_unchanged=True,
        elapsed_seconds=time.perf_counter()-started, goal_passed=any(r['goal_passed'] for r in pooled),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Human sector anchors and reviewed negative controls', '',
        'The frozen 20,340-run / 341-positive benchmark is unchanged. Original-MIL uses original '
        'fitting labels; reviewed-runs promotes strict reviewed positives and quarantines six conflicting '
        'fit labels, without new sector anchors. Reviewed-sectors differs only by unioning explicit '
        'human sector annotations into positive fit bags. Both reviewed models include all valid sectors '
        'of strict reviewed negative fit runs with 10x weight, normalized within the U control mass. '
        'Other sectors of positive bags are never labelled negative. Outcome-only review rationales '
        'cannot supply new strict supervision. Identities/notes/status/annotations are not predictors.', '',
        'All three models use the same 33 local sector features plus repeated aggregate, earlier-session '
        'paired and fit-only weather context. Three latent-assignment steps, 150 trees, leaves 7/15. '
        'Calibration alone selects leaves. A separate blend selects reviewed-runs/sectors and '
        '25/50/75% weight against the existing round/category ranker using calibration CDFs, not test '
        'ranks or quotas. Exact fitting keys and sector anchors are saved for auditing. Original and '
        'selected human sector localization are reported separately, regardless of the detection threshold.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Reviewed sector model: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Review sampling is purposeful and neither precision nor FPR is identifiable from it. '
              'Previously inspected seasons make this exploratory, not a fresh locked-model evaluation. '
              'Production and source workbook remain unchanged.', '']
    (out / 'reviewed-sectors.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible_rows = [r for r in pooled if r['alarms'] <= 700 and r['model'] != 'reference-rank']
    print(json.dumps(dict(best_reviewed=max(eligible_rows, key=lambda r: r['hits']) if eligible_rows else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
