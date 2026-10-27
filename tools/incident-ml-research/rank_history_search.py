"""Joint causal time/rank history with support and personal-alignment controls."""
import argparse
import json
import pickle
from pathlib import Path
import numpy as np
from rank_history import profiles_from_frozen, scoped_references, encode, swap_references
from rolling_history import records_from_frozen
from data import load_dataset, sha
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from target import BUDGETS
from conditional import goal_passed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-rank-history'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    records = records_from_frozen(root, meta, sectors)
    profiles = profiles_from_frozen(root, meta, sectors)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder = root / 'artifacts/incident-ml-field-factor'
    time_folder = root / 'artifacts/incident-ml-rolling-history'
    assert json.loads((factor_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    assert json.loads((time_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    components = {str((factor_folder / 'field-factors.npz').relative_to(root)): sha(factor_folder / 'field-factors.npz')}
    with np.load(factor_folder / 'field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups, diagnostics = ([] for _ in range(8))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        time_file = time_folder / f'{split}-history.npz'
        components[str(time_file.relative_to(root))] = sha(time_file)
        with np.load(time_file) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for name, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[name], indices)
            time_frozen, time_rolling = pack['frozen'], pack['rolling']
        frozen, rolling = scoped_references(profiles, records, meta, fit, cal, test)
        wrong_frozen, _ = swap_references(frozen, meta)
        wrong_rolling, changed = swap_references(rolling, meta)
        np.testing.assert_array_equal(frozen[fit], rolling[fit])
        np.testing.assert_array_equal(wrong_frozen[fit], wrong_rolling[fit])
        np.savez_compressed(out / f'{split}-rank.npz', keys=np.array([r['key'] for r in meta]),
            fit=fit, cal=cal, test=test, profiles=profiles, frozen=frozen, rolling=rolling,
            placebo=wrong_rolling, placebo_changed=changed)
        rf, rr = encode(profiles, frozen), encode(profiles, rolling)
        pf, pr = encode(profiles, wrong_frozen), encode(profiles, wrong_rolling)
        diagnostics.append(dict(split=split, frozen_test_covered=int(np.sum(frozen[test, 2] > 0)),
            rolling_test_covered=int(np.sum(rolling[test, 2] > 0)), placebo_permutable_runs=int(changed.sum()),
            rank_time_count_mismatches=int(np.sum(rolling[:, 2] != time_rolling[:, -1]))))
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        frozen_time, rolling_time = np.hstack([context, time_frozen]), np.hstack([context, time_rolling])
        views = {
            'time-reference': (frozen_time, rolling_time),
            'time-rank-joint': (np.hstack([frozen_time, rf]), np.hstack([rolling_time, rr])),
            'rank-context': (np.hstack([context, rf]), np.hstack([context, rr])),
            'rank-placebo': (np.hstack([frozen_time, pf]), np.hstack([rolling_time, pr])),
            'ability-only': (frozen, rolling),
            'support-only': (np.hstack([rf[:, 10:15], frozen[:, 2:3]]), np.hstack([rr[:, 10:15], rolling[:, 2:3]]))}
        selected, models = {}, {}
        for name, (fitting, values) in views.items():
            best = None
            for leaves in [7, 15]:
                model = train('all-unlabelled', leaves, fitting[fit], y[fit], years[fit])
                scores = score(model, values)
                objective = float(np.mean(scores[cal][y[cal]] > threshold_for_budget(scores[cal], 700 / 20340)))
                tuning.append(dict(split=split, model=name, leaves=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, scores, leaves
                    models[name] = model
            selected[name] = best
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values, leaves) in selected.items():
            control = name in ['time-reference', 'rank-placebo', 'ability-only', 'support-only']
            selections.append(dict(split=split, model=name, leaves=leaves, calibration_objective=objective))
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
        print(f'{split}: causal time/rank history complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores),
                          ('subgroups', subgroups), ('history-diagnostics', diagnostics)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in components.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('rank_history.py')]}, component_source_hashes=components,
        selections=selections, causal_unlabelled_rank_history=True, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Joint causal time and sector-percentile history', '',
        'Raw sector ranks/counts produce percentiles only with valid timing/checkpoint quality, a '
        'measured rank in [1,count], and count >=3. Unknown/invalid ranks remain unknown, not the '
        'generic midpoint used by some earlier feature encodings. No official finish rank/time or '
        'status is read. Historical ability is the 35% sector percentile of each prior run, then '
        'median/MAD over the last eight eligible earlier runs. Source scopes, same-category and '
        'different-round restrictions match the causal time-history experiment. Fit never consumes '
        'held rows; calibration never consumes test rows; inference may update from strictly earlier '
        'calendar-day unlabelled observations. Same-day order and test labels are never inferred.', '',
        'Current-sector rank deficits, standardized deficits (MAD floor .05), missingness and prior '
        'statistics supplement the unchanged time-history/context model. A rank/context ablation '
        'omits time history. Placebo swaps only rank references within current session/category and '
        'exact history count, keeping time history fixed. Ability-only (past center/MAD/count, no '
        'current loss) and support-only are reporting/availability-bias controls, not incident detectors. '
        'All models select leaves 7/15 on calibration only and use the same eight season/forward '
        'checks, five budgets and unchanged 20,340/341 benchmark.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Joint historical profiles: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Causal refers to history-input availability; leave-year-out can train on later seasons. '
              'Chronological checks train on earlier years. Finalized snapshots and previously inspected '
              'years keep this exploratory. U is not verified clean; no precision/crash probabilities '
              'are claimed. All artifacts stay in `artifacts/incident-ml-rank-history/`; production is unchanged.', '']
    (out / 'rank-history.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
