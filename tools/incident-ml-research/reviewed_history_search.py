"""Fit-only human annotations on the strongest causal-history representation."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from review_supervision import match_reviews, training_labels
from reviewed_search import fit_reviewed
from objectives import calibration_partition, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-reviewed-history'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    annotation_path = root / 'artifacts/incident-human-review/annotations.json'
    annotation_hash = sha(annotation_path)
    annotation = json.loads(annotation_path.read_text(encoding='utf-8'))
    reviews, audit = match_reviews(meta, annotation['rows'])
    write_csv(out / 'review-audit.csv', audit)
    workbook = Path('C:/Users/Mikolaj/Downloads/outputs/019fca2d-7605-7a00-b4fe-127b03d2637a/RaceTrace_Incident_Review.xlsx')
    assert sha(workbook) == annotation['workbook_sha256']
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder, history_folder = root / 'artifacts/incident-ml-field-factor', root / 'artifacts/incident-ml-rolling-history'
    for folder in [factor_folder, history_folder]:
        assert json.loads((folder / 'manifest.json').read_text())['input_hashes'] == hashes
    source = factor_folder / 'field-factors.npz'
    components = {str(source.relative_to(root)): sha(source)}
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index, reference = {r['key']: i for i, r in enumerate(meta)}, {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = history_folder / filename
        components[str(source.relative_to(root))] = sha(source)
        with source.open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'history-rolling':
                    values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                    assert np.isnan(values[index[row['key']]])
                    values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups, usage = ([] for _ in range(8))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        quarantine = [int(i) for i in fit if reviews.get(int(i), {}).get('conflict', False)]
        eligible = np.array([i for i in fit if int(i) not in quarantine], int)
        source = history_folder / f'{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        # The unsupervised sporting-history measurements and weather vocabulary
        # remain identical to the reference, including quarantined-row measurements.
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        selected, models = {'time-reference': (0., reference[split])}, {}
        for name in ['quarantine-only', 'negative-only', 'reviewed-labels']:
            if name == 'quarantine-only':
                target, negative, multipliers = y.copy(), np.zeros(len(y), bool), [1.]
            else:
                target, negative = training_labels(y, eligible, reviews, promote=name == 'reviewed-labels')
                multipliers = [10., 50.]
            held = np.concatenate([cal, test])
            assert np.array_equal(target[held], y[held]) and not negative[held].any()
            assert set(np.flatnonzero(target != y)) <= set(eligible)
            best = None
            for leaves in [7, 15]:
                for multiplier in multipliers:
                    model = fit_reviewed(fitting, target, negative, eligible, leaves, multiplier)
                    values = score(model, inference)
                    objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                    tuning.append(dict(split=split, model=name, leaves=leaves, multiplier=multiplier,
                        calibration_recall=objective, fitting_rows=len(eligible),
                        positive_additions=int(np.sum(target[eligible] & ~y[eligible])),
                        reviewed_negatives=int(negative[eligible].sum())))
                    if best is None or objective > best[0]:
                        best = objective, values, model, leaves, multiplier
            selected[name] = best[:2]
            models[name] = dict(model=best[2].booster_, leaves=best[3], multiplier=best[4])
            selections.append(dict(split=split, model=name, leaves=best[3], multiplier=best[4],
                                   calibration_objective=best[0]))
            usage.append(dict(split=split, model=name, eligible_fit_keys=[meta[i]['key'] for i in eligible],
                quarantined_fit_keys=[meta[i]['key'] for i in quarantine],
                positive_added_keys=[meta[i]['key'] for i in np.flatnonzero(target & ~y)],
                negative_weighted_keys=[meta[i]['key'] for i in np.flatnonzero(negative)],
                calibration_targets_unchanged=True, test_targets_unchanged=True))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values) in selected.items():
            control = name in ['time-reference', 'quarantine-only']
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'session', 'status']:
                    for value in sorted({str(meta[i][field]) for i in test}):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        print(f'{split}: reviewed supervision on causal context complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    (out / 'review-usage.json').write_text(json.dumps(usage, indent=2), encoding='utf-8')
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in components.items())
    assert sha(annotation_path) == annotation_hash and sha(workbook) == annotation['workbook_sha256']
    manifest = dict(input_hashes=hashes, annotation_hash=annotation_hash, workbook_sha256=annotation['workbook_sha256'],
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__),
            Path(__file__).with_name('review_supervision.py'), Path(__file__).with_name('reviewed_search.py'),
            Path(__file__).with_name('hybrid_search.py')]}, component_source_hashes=components,
        selections=selections, reviewed_causal_context=True, protected_sources_unchanged=True,
        causal_unlabelled_history_inputs=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Reviewed labels with strongest causal context', '',
        'This applies the existing human-review supervision to the strongest causal-history/factor '
        'representation, not the earlier weaker paired/weather view. The SAME 60 selected reviews '
        'supply 21 strict additional positive and 8 strict negative annotations; six conflicts with '
        'original positives are quarantined from FIT only. No new review or independent video check '
        'is claimed. Outcome-only explanations remain ineligible. U is not verified clean.', '',
        'Reviews from calibration/test do not supervise their fold. Original 341-positive targets, '
        'split identities and thresholds stay unchanged. Quarantined measurements may still enter '
        'label-free sporting history/current-field summaries; the disputed LABELS never enter fitting. '
        'The fit weather vocabulary and all raw feature arrays are identical to the frozen reference. '
        'Review membership, decision, note, rider identity and status are not predictor columns.', '',
        'Quarantine-only isolates removed fitting labels. Negative-only upweights strict reviewed '
        'negatives 10/50 while preserving balanced class mass. Reviewed-labels also adds fit-only '
        'positive reviews. Existing 300-tree/.025/min-leaf35/L2=5 learner is reused; leaves 7/15 and '
        'weights are calibration-selected. The original strongest tree is retained as a control. '
        'All eight splits/five budgets, no held-test quota. Purposeful reviews do not identify '
        'population precision, FPR or physical crash probabilities.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Reviewed causal context: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-reviewed-history/`. Previously inspected years remain '
              'exploratory. Production and completed workbook unchanged.', '']
    (out / 'reviewed-history.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
