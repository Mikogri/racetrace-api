"""Causal unlabelled test-prefix updates, without learning from held labels."""
import argparse
import json
import pickle
from pathlib import Path
import numpy as np
from rolling_history import records_from_frozen, scoped_features, placebo_features
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
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-rolling-history'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    records = records_from_frozen(root, meta, sectors)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder = root / 'artifacts/incident-ml-field-factor'
    assert json.loads((factor_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    factor_hash = sha(factor_folder / 'field-factors.npz')
    with np.load(factor_folder / 'field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups, scopes = ([] for _ in range(8))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        frozen, rolling = scoped_features(records, meta, fit, cal, test)
        wrong_frozen, _ = placebo_features(frozen, records, meta)
        wrong_rolling, changed = placebo_features(rolling, records, meta)
        np.testing.assert_array_equal(frozen[fit], rolling[fit])
        np.testing.assert_array_equal(wrong_frozen[fit], wrong_rolling[fit])
        np.savez_compressed(out / f'{split}-history.npz', keys=np.array([r['key'] for r in meta]),
            fit=fit, cal=cal, test=test, frozen=frozen, rolling=rolling,
            placebo=wrong_rolling, placebo_changed=changed)
        scopes.append(dict(split=split, fit_keys=[meta[i]['key'] for i in fit],
            calibration_keys=[meta[i]['key'] for i in cal], test_keys=[meta[i]['key'] for i in test],
            fitting_source='fit only', calibration_source='fit plus earlier calibration measurements',
            test_source='fit plus earlier calibration/test measurements',
            training_history_equal=True, placebo_permutable_runs=int(changed.sum()),
            frozen_test_covered=int(np.sum(frozen[test, -1] > 0)),
            rolling_test_covered=int(np.sum(rolling[test, -1] > 0))))
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        frozen_view, rolling_view = np.hstack([context, frozen]), np.hstack([context, rolling])
        support = np.column_stack([rolling[:, -1], rolling[:, -1] > 0])
        selected, models = {}, {}

        def consider(name, model, values, leaves):
            scores = score(model, values)
            objective = float(np.mean(scores[cal][y[cal]] > threshold_for_budget(scores[cal], 700 / 20340)))
            tuning.append(dict(split=split, model=name, leaves=leaves, calibration_recall=objective))
            if name not in selected or objective > selected[name][0]:
                selected[name] = objective, scores, leaves
                models[name] = model

        for leaves in [7, 15]:
            shared_model = train('all-unlabelled', leaves, frozen_view[fit], y[fit], years[fit])
            consider('history-frozen', shared_model, frozen_view, leaves)
            consider('history-rolling', shared_model, rolling_view, leaves)
            for name, fitting_values, values in [
                ('history-placebo', np.hstack([context, wrong_frozen]), np.hstack([context, wrong_rolling])),
                ('history-only', frozen, rolling), ('support-only', support, support),
                ('context-reference', context, context)]:
                model = train('all-unlabelled', leaves, fitting_values[fit], y[fit], years[fit])
                consider(name, model, values, leaves)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, values, leaves) in selected.items():
            control = name in ['history-frozen', 'history-placebo', 'support-only', 'context-reference']
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
        print(f'{split}: frozen/rolling causal history complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    (out / 'history-scopes.json').write_text(json.dumps(scopes, indent=2), encoding='utf-8')
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert sha(factor_folder / 'field-factors.npz') == factor_hash
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('rolling_history.py'), Path(__file__).with_name('trajectory.py')]},
        component_source_hashes={str((factor_folder / 'field-factors.npz').relative_to(root)): factor_hash},
        selections=selections, causal_unlabelled_stream_history=True, protected_sources_unchanged=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Causal unlabelled history updates during a held season', '',
        'The earlier history helper used only fit-row measurements for every target. This experiment '
        'keeps that frozen control and the SAME historical formulas: per-run 35% log-relative pace, '
        'median/MAD over the last eight eligible prior runs, current-sector diagnostic losses floored '
        'by measured field best. Category, strictly earlier calendar day and different-round restrictions '
        'remain. Same-day order is not inferred. Checkpoint-quality-invalid timings are omitted.', '',
        'Fit features use fit measurements only. Calibration may add earlier calibration measurements, '
        'never any test row. Test inference may add earlier calibration/test measurements. Labels '
        'from those streams are never read by feature construction or used to refit the learner. '
        'This is an operational causal history state, not independently sampled test covariates: later '
        'test events can depend on observed earlier test-event inputs. Frozen/rolling learners share '
        'identical fitting matrices and fitted weights per candidate. Leaves 7/15 are selected using '
        'each mode’s calibration inputs/labels only. No test quota or future/same-round measurement.', '',
        'A different-rider placebo matches current session/category/history count and recomputes the '
        'target’s own losses from swapped historical center/dispersion. Support-only and unchanged '
        'factor/context are controls; history-only is an ablation. Identities are joins, not prediction '
        'columns. U is not verified clean. Exact source scopes, history arrays and swap coverage are '
        'saved for every split. The full 20,340/341 benchmark and eight season/forward checks remain.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Causal history: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['These previously inspected years remain exploratory. No crash probability or precision '
              'is identifiable. Artifacts stay in `artifacts/incident-ml-rolling-history/`; production is unchanged.', '']
    (out / 'rolling-history.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
