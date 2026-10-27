"""Exact 32-path weak-bag sequence experiment on frozen measured sectors."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from rolling_history import records_from_frozen
from sector_sequence import observations, fit_sequence, sequence_scores, anchor_mask
from objectives import calibration_partition
from experiment import evaluation_splits, metrics, write_csv, cluster_interval
from local_density import available_threshold
from target import BUDGETS
from conditional import goal_passed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-sector-sequence'))
    parser.add_argument('--pilot', action='store_true')
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    _, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    records = records_from_frozen(root, meta, sectors)
    within = observations(records, meta)
    anchors = anchor_mask(meta)
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    history_folder = root / 'artifacts/incident-ml-rolling-history'
    assert json.loads((history_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    components, reference, index = {}, {}, {r['key']: i for i, r in enumerate(meta)}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        source = history_folder / filename
        components[str(source.relative_to(root))] = sha(source)
        with source.open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'history-rolling':
                    values = reference.setdefault(row['split'], np.full(len(meta), np.nan))
                    assert np.isnan(values[index[row['key']]])
                    values[index[row['key']]] = float(row['score'])
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups = ([] for _ in range(7))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        source = history_folder / f'{split}-history.npz'
        components[str(source.relative_to(root))] = sha(source)
        with np.load(source) as pack:
            assert pack['keys'].tolist() == [r['key'] for r in meta]
            for label, indices in [('fit', fit), ('cal', cal), ('test', test)]:
                np.testing.assert_array_equal(pack[label], indices)
            fitting = observations(records, meta, pack['frozen'])
            inference = observations(records, meta, pack['rolling'])
        if args.pilot:
            # Resource pilot: fit-only sample, no test metrics or recipe selection.
            pilot = fit[:3000]
            start = time.perf_counter()
            model = fit_sequence(*(a[pilot] for a in fitting), y[pilot], anchors=anchors[pilot])
            summary = dict(split=split, fit_rows=len(pilot), positives=int(y[pilot].sum()),
                elapsed_seconds=time.perf_counter()-start, theta=model['theta'].tolist(),
                transitions=model['transitions'].tolist(), trace=model['trace'], input_hashes=hashes)
            (out / 'resource-pilot.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
            print(json.dumps(summary), flush=True)
            return
        selected, models = {'time-reference': (0., reference[split])}, {}
        for name, fit_data, query_data, independent in [
                ('sequence-history', fitting, inference, False),
                ('sequence-within', within, within, False),
                ('independent-history', fitting, inference, True)]:
            best = None
            for balanced in [False, True]:
                start = time.perf_counter()
                model = fit_sequence(*(a[fit] for a in fit_data), y[fit], balanced=balanced,
                                     independent=independent, anchors=anchors[fit])
                values = sequence_scores(model, *query_data)
                objective = float(np.mean(values[cal][y[cal]] > available_threshold(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, balanced=balanced, calibration_recall=objective,
                    elapsed_seconds=time.perf_counter()-start,
                    optimizer_failures=sum(not r['optimizer_success'] for r in model['trace'])))
                if best is None or objective > best[0]:
                    best = objective, values, model
            selected[name], models[name] = best[:2], best[2]
            selections.append(dict(split=split, model=name, balanced=best[2]['balanced'],
                calibration_objective=best[0], fit_anchor_keys=[meta[i]['key'] for i in fit if anchors[i].any()],
                omitted_all_missing_fit_keys=[meta[i]['key'] for i in fit if not fit_data[2][i].any()]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(models, handle)
        for name, (objective, values) in selected.items():
            control = name in ['time-reference', 'independent-history']
            for budget in BUDGETS:
                threshold = available_threshold(values[cal], budget)
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
        print(f'{split}: exact-path sequence models complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in components.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('sector_sequence.py'), Path(__file__).with_name('rolling_history.py')]},
        component_source_hashes=components, selections=selections, weak_bag_sequence=True,
        protected_sources_unchanged=True, causal_unlabelled_history_inputs=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Exact-path weak-bag positive-delay sector sequence', '',
        'Five ordered measured-sector residuals use within-run 35% log-relative tempo, or the same '
        'strictly earlier causal history centre when available. Expected clock=field median*exp(centre). '
        'The normal state is Gaussian on relative residuals; the delayed state is Gaussian plus '
        'a positive exponential delay whose learned mean is expressed in seconds. These are '
        'diagnostic reference clocks, not validated corrected race times. No official finish time/rank/status '
        'or identity is a predictor.', '',
        'A two-state Markov prior sums all 32 five-sector paths exactly. Positive fit bags require at least '
        'one delayed measured sector; explicit original fit sector labels anchor only those sectors. U bags '
        'allow every path: U is not forced clean. Missing emissions are marginalized; all-missing runs '
        'remain unknown and in the full benchmark denominator. EM fits emissions and transition rates on '
        'fit only for 20 iterations, with analytical gradients and Beta(2,2) transition MAP smoothing. '
        'Calibration chooses natural or equal-total-mass P/U bag weights. Bounds: relative mean +/-0.3, '
        'normal SD .002–.5, exponential mean .1–300 seconds. These are model assumptions, not incident '
        'decision thresholds. Independent-state history and the cached strongest tree are controls.', '',
        'Scores are sequence likelihood log-odds of any delayed observed state, NOT calibrated crash '
        'probabilities; reporting propensity is not identified. Same eight splits/five budgets, no test quota, '
        'no held labels used for fitting or history. Previously inspected years remain exploratory.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Sequence: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-sector-sequence/`. No production changes.', '']
    (out / 'sector-sequence.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
