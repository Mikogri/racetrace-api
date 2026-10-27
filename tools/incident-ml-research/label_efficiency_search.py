"""Fixed-learner diagnostic: observed-label quantity versus event diversity."""
import argparse
import csv
import json
import pickle
from pathlib import Path
import numpy as np
from label_budget import sample_fit_labels
from data import load_dataset, sha
from reviewed_search import fit_reviewed
from objectives import calibration_partition
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv
from conditional import goal_passed
from target import BUDGETS


def read(path):
    with path.open(encoding='utf-8') as handle:
        return list(csv.DictReader(handle))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-label-efficiency'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    base = root / 'artifacts/incident-ml-reviewed-history'
    parent = json.loads((base / 'manifest.json').read_text(encoding='utf-8'))
    assert parent['input_hashes'] == hashes
    for field in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root / path) == digest for path, digest in parent[field].items())
    proof = json.loads((base / 'verification.json').read_text(encoding='utf-8'))
    assert proof['changed_reviewed_history_decisions'] == 0 and proof['original_held_targets_unchanged']
    usage = json.loads((base / 'review-usage.json').read_text(encoding='utf-8'))
    components = {str((base / name).relative_to(root)): sha(base / name) for name in
                  ['manifest.json', 'verification.json', 'review-usage.json', 'predictions.csv', 'calibration-scores.csv']}
    source = root / 'artifacts/incident-ml-field-factor/field-factors.npz'
    components[str(source.relative_to(root))] = sha(source)
    with np.load(source) as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    index, reference = {r['key']: i for i, r in enumerate(meta)}, {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        for row in read(base / filename):
            if row['model'] == 'quarantine-only':
                values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                assert np.isnan(values[index[row['key']]])
                values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    groups, baseline = np.array([r['group'] for r in meta]), np.array([r['production'] for r in meta])
    rows, pooled, recipes, predictions, calibration_scores = ([] for _ in range(5))
    new_models = 0
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        use = next(row for row in usage if row['split'] == split and row['model'] == 'quarantine-only')
        eligible = np.array([index[key] for key in use['eligible_fit_keys']], int)
        assert set(eligible) <= set(fit) and not set(eligible) & set(np.r_[cal, test])
        source = root / f'artifacts/incident-ml-rolling-history/{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            frozen, rolling = pack['frozen'], pack['rolling']
        source = base / f'{split}.pickle'
        components[str(source.relative_to(root))] = sha(source)
        with source.open('rb') as handle:
            saved = pickle.load(handle)
        assert saved['weather_names'] == sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in saved['weather_names']] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        fitting, inference = np.hstack([context, frozen]), np.hstack([context, rolling])
        original = saved['models']['quarantine-only']
        assert original['multiplier'] == 1.
        chosen_leaves = original['leaves']
        native = original['model']
        values = native.predict(inference, num_threads=2)
        np.testing.assert_array_equal(values[np.r_[cal, test]], reference[split][np.r_[cal, test]])
        models = {'full-reference': dict(model=native, take=eligible, fraction=1., unit='full', seed=None)}
        for unit in ['run', 'event']:
            for seed in [SEED, SEED+1]:
                for fraction in [.25, .5, .75]:
                    take = sample_fit_labels(y, groups, eligible, fraction, seed, unit)
                    assert np.array_equal(take[~y[take]], eligible[~y[eligible]])
                    model = fit_reviewed(fitting, y, np.zeros(len(y), bool), take, chosen_leaves, 1.).booster_
                    name = f'{unit}-{fraction:g}-s{seed-SEED}'
                    models[name] = dict(model=model, take=take, fraction=fraction, unit=unit, seed=seed)
                    new_models += 1
        for name, entry in models.items():
            values = entry['model'].predict(inference, num_threads=2)
            take = entry['take']
            fit_scores = entry['model'].predict(fitting[take], num_threads=2)
            recipes.append(dict(split=split, model=name, fraction=entry['fraction'], unit=entry['unit'], seed=entry['seed'],
                leaves=chosen_leaves, original_fit_positives=int(y[eligible].sum()),
                kept_fit_positives=int(y[take].sum()), unknown_fit_rows=int((~y[take]).sum()),
                original_positive_events=len(np.unique(groups[eligible[y[eligible]]])),
                kept_positive_events=len(np.unique(groups[take[y[take]]]))))
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                rows.append(dict(split=split, model=name, control=True, budget=budget, threshold=threshold,
                    **metrics(y[test], values[test] > threshold, baseline[test]),
                    fit_positive_count=int(y[take].sum()), fit_positive_hits=int(np.sum((fit_scores > threshold) & y[take])),
                    fit_positive_recall=float(np.mean(fit_scores[y[take]] > threshold))))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=saved['weather_names'], eligible=eligible), handle)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'fit-label-recipes.csv', recipes)
        print(f'{split}: fixed-learner label-count/event-diversity curves complete', flush=True)
    for name in models:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=True, budget=budget, hits=hits, positives=int(y.sum()),
                alarms=alarms, recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / path) == digest for path, digest in protected.items())
    assert all(sha(root / path) == digest for path, digest in components.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(path.relative_to(root)): sha(path) for path in
        [Path(__file__), Path(__file__).with_name('label_budget.py'), Path(__file__).with_name('reviewed_search.py'),
         Path(__file__).with_name('hybrid_search.py')]}, component_source_hashes=components,
        label_efficiency_diagnostic=True, diagnostic_only=True, new_models_fitted=new_models,
        inherited_leaves_not_retuned=True, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True, detector_selected=False)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Fixed-learner label quantity versus positive-event diversity', '',
        'This diagnostic keeps all eligible U rows, full timing/context measurements, original held '
        '341-positive targets and model settings. It retains 25/50/75% of fit positive annotations '
        'by run or positive-bearing event, using two fixed nested random permutations. Hidden original '
        'P rows are EXCLUDED from supervised fitting, never relabelled U/clean. Their unlabelled '
        'sporting measurements still enter the same frozen history/field features. No fabricated '
        'measurements, new annotations or data downloads are used.', '',
        'The same native learner and leaves inherited from the full-label calibration-selected '
        'quarantine model are used; leaves are not retuned at each fraction. Calibration data and '
        'its threshold policy remain complete/fixed across arms. Thus fractions count FIT positive '
        'labels only, not total annotation cost. Dropping known P instead of contaminating U is an '
        'optimistic controlled ablation, not a realistic random missing-label process. All 341 '
        'original positives still contribute to held evaluation. No seed/fraction is selected as '
        'a new detector from these test outcomes; these curves are excluded from the detector scoreboard.', '',
        table(['Fit-label arm', 'Target', 'Held hits / 341', 'Alarms', 'Recall'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']))
             for r in pooled if r['budget'] == .03]), '',
        'Per-fold recipes retain exact label/event counts. Training observed-positive recall uses '
        'the SAME calibration threshold, not a favourable fit-only cutoff. Training rows overlap '
        'between leave-year folds and are not pooled as independent observations. Two seeds are '
        'perturbations, not confidence intervals or evidence for extrapolation to 307 hits.', '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Label-efficiency diagnostic: {split}', '',
            table(['Arm', 'Fit P', 'Fit hits', 'Held hits', 'Held P', 'Alarms'],
                [(r['model'], r['fit_positive_count'], r['fit_positive_hits'], r['hits'], r['positives'], r['alarms'])
                 for r in rows if r['split'] == split and r['budget'] == .03]), '']
    lines += ['Artifacts: `artifacts/incident-ml-label-efficiency/`. Unknown U remains contaminated; '
              'no precision/FPR claim, no new detector selected and no production changes.', '']
    (out / 'label-efficiency.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(dict(new_models_fitted=new_models, diagnostic_only=True,
                         full_reference=[r for r in pooled if r['model'] == 'full-reference' and r['budget'] == .03])), flush=True)


if __name__ == '__main__':
    main()
