"""Fit-positive subtype experts with calibration-only maximum-rank pooling."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from positive_specialists import profiles, partition_positives
from data import load_dataset, sha
from review_supervision import match_reviews
from objectives import calibration_partition, train
from field_objectives import percentile_scores
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-specialists'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, availability, measured = profiles(sectors)
    annotation_path = root / 'artifacts/incident-human-review/annotations.json'
    annotation_hash = sha(annotation_path)
    annotations = json.loads(annotation_path.read_text(encoding='utf-8'))
    reviews, _ = match_reviews(meta, annotations['rows'])
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder = root / 'artifacts/incident-ml-field-factor'
    history_folder = root / 'artifacts/incident-ml-rolling-history'
    reference_folder = root / 'artifacts/incident-ml-reviewed-history'
    for folder in [factor_folder, history_folder, reference_folder]:
        assert json.loads((folder / 'manifest.json').read_text(encoding='utf-8'))['input_hashes'] == hashes
    source = factor_folder / 'field-factors.npz'
    components = {str(source.relative_to(root)): sha(source)}
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index, reference = {r['key']: i for i, r in enumerate(meta)}, {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = reference_folder / filename
        components[str(source.relative_to(root))] = sha(source)
        with source.open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'quarantine-only':
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
        unknown = eligible[~y[eligible]]
        source = history_folder / f'{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        selected, saved = {'quarantine-reference': (0., reference[split])}, {}
        for name, grouping_view in [('shape-specialists', shape), ('availability-specialists', availability)]:
            partition = partition_positives(grouping_view, measured, y, eligible)
            groups = [np.sort(np.r_[unknown, group]) for group in partition['groups']]
            assert all(set(group) <= set(eligible) for group in groups)
            assert set(np.concatenate(partition['groups'])) == set(eligible[y[eligible]])
            best = None
            for leaves in [7, 15]:
                models = [train('all-unlabelled', leaves, fitting[group], y[group], years[group]).booster_
                          for group in groups]
                raw = [model.predict(inference, num_threads=2) for model in models]
                calibrations = [values[cal].copy() for values in raw]
                values = np.max(np.vstack([percentile_scores(c, v) for c, v in zip(calibrations, raw)]), axis=0)
                objective = float(np.mean(values[cal][y[cal]] > threshold_for_budget(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, leaves=leaves, calibration_recall=objective,
                    experts=len(models), structured_positives=len(partition['structured_indices']),
                    shared_positives=len(partition['shared_indices'])))
                if best is None or objective > best[0]:
                    best = objective, values, models, calibrations, leaves
            selected[name] = best[:2]
            saved[name] = dict(models=best[2], calibrations=best[3], partition=partition)
            selections.append(dict(split=split, model=name, leaves=best[4], calibration_objective=best[0]))
            usage.append(dict(split=split, model=name, eligible_fit_keys=[meta[i]['key'] for i in eligible],
                quarantined_fit_keys=[meta[i]['key'] for i in quarantine],
                positive_group_keys=[[meta[i]['key'] for i in group] for group in partition['groups']],
                shared_positive_keys=[meta[i]['key'] for i in partition['shared_indices']],
                unknown_fit_keys=[meta[i]['key'] for i in unknown]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=saved, weather_names=names), handle)
        for name, (objective, values) in selected.items():
            control = name != 'shape-specialists'
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
        print(f'{split}: profile and availability specialist ensembles complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    (out / 'specialist-partitions.json').write_text(json.dumps(usage, indent=2), encoding='utf-8')
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in components.items())
    assert sha(annotation_path) == annotation_hash
    manifest = dict(input_hashes=hashes, annotation_hash=annotation_hash,
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__),
            Path(__file__).with_name('positive_specialists.py'), Path(__file__).with_name('objectives.py'),
            Path(__file__).with_name('field_objectives.py')]}, component_source_hashes=components,
        selections=selections, positive_subtype_specialists=True, protected_sources_unchanged=True,
        causal_unlabelled_history_inputs=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Fit-positive profile specialist ensembles', '',
        'This tests whether one global discriminator suppresses rarer observed-positive profiles. '
        'Fit positives with at least two quality-valid measurements are clustered using their five '
        'ordered within-run-centred log-relative sector times plus five missing masks. RobustScaler '
        'fits these positives only; transformed grouping vectors are clipped +/-4. Up to three '
        'KMeans clusters (10 initializations, fixed seed) are used, reduced only when fewer unique '
        'profiles exist. Clusters have no asserted causal incident-type names. A mask-only grouping '
        'control diagnoses availability/reporting-pattern effects.', '',
        'Each expert learns its positive cluster plus ALL eligible U. Other known fit positives are '
        'excluded, never turned into negative controls. Positives with fewer than two valid sectors '
        'are shared among experts rather than dropped or assigned an invented shape. Original '
        'positive/human-No conflicts are quarantined from fit labels as in the frozen reference. '
        'No held labels determine centres, memberships or fitting rows. Zero profile placeholders '
        'are masked latent values, not fabricated sector measurements. U is not verified clean.', '',
        'All experts use the SAME strongest causal input view and 300-tree/.025/min-leaf35/L2=5 '
        'observed-label learner. Leaves 7/15 are selected jointly for an ensemble on calibration. '
        'Each component score is mapped through its CALIBRATION-only empirical midrank CDF, then '
        'the fixed maximum pools experts. No test CDF, test quota or held-stage/identity input. '
        'These are observed-pattern ranking scores, not physical crash probabilities. The 235/653 '
        'label-quarantine pipeline is a cached reference. All years remain exploratory.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Specialists: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-specialists/`. Production unchanged.', '']
    (out / 'specialists.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
