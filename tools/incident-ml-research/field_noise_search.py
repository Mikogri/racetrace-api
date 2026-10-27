"""Heteroscedastic-field ablation of the archived exact-path sequence model."""
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
from field_noise_sequence import scale_observations
from objectives import calibration_partition
from experiment import evaluation_splits, metrics, write_csv, cluster_interval
from local_density import available_threshold
from target import BUDGETS
from conditional import goal_passed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-field-noise'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    _, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    records, anchors = records_from_frozen(root, meta, sectors), anchor_mask(meta)
    within = scale_observations(observations(records, meta), sectors)
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    history_folder = root / 'artifacts/incident-ml-rolling-history'
    components, reference, index = {}, {}, {r['key']: i for i, r in enumerate(meta)}
    for folder, original, renamed in [(history_folder, 'history-rolling', 'time-reference'),
            (root / 'artifacts/incident-ml-sector-sequence', 'sequence-history', 'sequence-reference')]:
        assert json.loads((folder / 'manifest.json').read_text())['input_hashes'] == hashes
        for filename in ['predictions.csv', 'calibration-scores.csv']:
            source = folder / filename
            components[str(source.relative_to(root))] = sha(source)
            with source.open(encoding='utf-8') as handle:
                for row in csv.DictReader(handle):
                    if row['model'] == original:
                        values = reference.setdefault((row['split'], renamed), np.full(len(meta), np.nan))
                        # Missing sequence scores are NaN by design; the source file has unique keys.
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
            raw_fit, raw_eval = observations(records, meta, pack['frozen']), observations(records, meta, pack['rolling'])
            fitting, inference = scale_observations(raw_fit, sectors), scale_observations(raw_eval, sectors)
            joint_fit = scale_observations(raw_fit, sectors, pack['frozen'])
            joint_eval = scale_observations(raw_eval, sectors, pack['rolling'])
        selected = {name: (0., reference[split, name]) for name in ['time-reference', 'sequence-reference']}
        models = {}
        for name, fit_data, query_data in [('field-history', fitting, inference),
                ('field-within', within, within), ('field-and-history-noise', joint_fit, joint_eval)]:
            best = None
            for balanced in [False, True]:
                start = time.perf_counter()
                model = fit_sequence(*(a[fit] for a in fit_data), y[fit], balanced=balanced, anchors=anchors[fit])
                values = sequence_scores(model, *query_data)
                objective = float(np.mean(values[cal][y[cal]] > available_threshold(values[cal], 700/20340)))
                tuning.append(dict(split=split, model=name, balanced=balanced, calibration_recall=objective,
                    elapsed_seconds=time.perf_counter()-start,
                    optimizer_failures=sum(not r['optimizer_success'] for r in model['trace'])))
                if best is None or objective > best[0]:
                    best = objective, values, model
            selected[name], models[name] = best[:2], best[2]
            selections.append(dict(split=split, model=name, balanced=best[2]['balanced'],
                calibration_objective=best[0], fit_anchor_keys=[meta[i]['key'] for i in fit if anchors[i].any()]))
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(models, handle)
        for name, (objective, values) in selected.items():
            control = name.endswith('-reference')
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
        print(f'{split}: field-dispersion sequence ablations complete', flush=True)
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
        [Path(__file__), Path(__file__).with_name('field_noise_sequence.py'), Path(__file__).with_name('sector_sequence.py')]},
        component_source_hashes=components, selections=selections, field_noise_sequence=True,
        protected_sources_unchanged=True, causal_unlabelled_history_inputs=True,
        exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Heteroscedastic field-dispersion sequence ablation', '',
        'This tests whether the earlier sequence model absorbed moderate losses into an overly broad '
        'common normal distribution. Each sector scales its residual by s=field log-MAD/.04 and '
        'its reference clock by s inversely to the residual: r_new=r/s, E_new=E*s. Thus r_new*E_new '
        'preserves diagnostic seconds and the reused exponential-delay solver retains seconds units. '
        'The Gaussian SD now varies with current measured field dispersion. Field MAD is floored at .005; '
        'counts below 8 or absent/nonpositive dispersion retain neutral scale 1. These assumptions were '
        'fixed before the sweep, not fitted on test outcomes.', '',
        'The joint-noise ablation adds squared supported prior-history MAD before the square root; '
        'the within-run ablation omits the prior centre. All models reuse the exact archived 32-path solver, '
        '20 EM steps, original fit-only sector anchors, and calibration choice of natural or balanced bag '
        'weights. Cached homoscedastic sequence and strongest causal tree scores are controls. '
        'Missing measurements remain unknown; no final rank/status/identity is a predictor. '
        'These are model likelihood scores, not identified crash probabilities.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Field-noise: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['U remains contaminated; source reporting bias and previously inspected years remain. '
              'No precision/FPR claim. Artifacts: `artifacts/incident-ml-field-noise/`. Production unchanged.', '']
    (out / 'field-noise.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
