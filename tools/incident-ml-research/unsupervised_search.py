"""Isolation and density models fitted to the contaminated fit mixture without labels."""
import argparse
import json
import pickle
import time
import warnings
from pathlib import Path
import numpy as np
from unsupervised_runs import positive_profile, fit_anomaly, anomaly_scores
from data import load_dataset, FEATURE_NAMES, sha
from focus import relative_columns
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-unsupervised'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    quality_names = ['measured_fraction', 'checkpoint_max', 'sample_ratio_min']
    views = {'relative': x[:, relative_columns(FEATURE_NAMES)],
             'one-sided': np.array([positive_profile(f) for f in sectors]),
             'quality-only': x[:, [FEATURE_NAMES.index(name) for name in quality_names]]}
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, predictions, calibration_scores, subgroups, selections = [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        target = np.concatenate([cal, test])
        assert not set(fit) & set(target)
        selected = {}
        for view, features in views.items():
            assert np.isfinite(features).all()
            for kind, parameters in [('isolation', [256, 1024]), ('density', [20, 50])]:
                name = f'{view}-{kind}'
                best = None
                for parameter in parameters:
                    fitted_at = time.perf_counter()
                    with warnings.catch_warnings(record=True) as caught:
                        warnings.simplefilter('always')
                        # No positive mask, names, status, stage or final result
                        # is supplied to the estimator or preprocessing fit.
                        model = fit_anomaly(features[fit], kind, parameter)
                        values = anomaly_scores(model, features[target])
                    messages = [f'{type(w.message).__name__}: {w.message}' for w in caught]
                    assert np.isfinite(values).all()
                    threshold = threshold_for_budget(values[:len(cal)], 700 / 20340)
                    objective = float(np.mean(values[:len(cal)][y[cal]] > threshold))
                    tuning.append(dict(split=split, model=name, parameter=parameter,
                        calibration_recall=objective, fit_predict_seconds=time.perf_counter()-fitted_at,
                        warnings=json.dumps(messages)))
                    if best is None or objective > best[0]:
                        best = objective, values, dict(model=model, kind=kind, view=view,
                                                      parameter=parameter, warnings=messages)
                    if messages:
                        print(f'{split}: {name} {parameter}: {messages}', flush=True)
                selected[name] = best
                print(f'{split}: {name} evaluated', flush=True)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump({name: result[2] for name, result in selected.items()}, handle)
        for name, (objective, values, pack) in selected.items():
            control = name.startswith('quality-only-')
            selections.append(dict(split=split, model=name, calibration_objective=objective,
                parameter=pack['parameter'], control=control, warnings=pack['warnings'],
                fit_rows=len(fit), calibration_rows=len(cal)))
            calibration, tested = values[:len(cal)], values[len(cal):]
            for budget in BUDGETS:
                threshold = threshold_for_budget(calibration, budget)
                flagged = tested > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(test, tested))
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(cal, calibration))
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=name.startswith('quality-only-'), budget=budget,
                hits=hits, positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), root / 'tools/incident-ml-research/unsupervised_runs.py']}, selections=selections,
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter()-started,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Unsupervised-mixture isolation and local-density novelty', '',
        'Isolation Forest (300 trees, max samples 256/1024) and novelty LOF (20/50 neighbors) fit '
        'the entire fit mixture without incident labels. Known positives are not removed or asserted '
        'clean. Relative timing removes absolute field-size/duration/ability columns; one-sided '
        'profiles retain paired positive time/rank/recovery channels, explicit measurement fraction '
        'and timing-quality context. Faster-sector channels are not flipped into positive losses. '
        'Quality-only models are bias controls, not proposed detectors.', '',
        'Signed-log compression and fit-only robust scaling precede both estimators. More positive '
        'score means more unusual; native contamination thresholds are never used. LOF is scored '
        'only on disjoint calibration/test rows, never its own fit rows. BLAS/OpenMP is limited to '
        'two threads and pairwise working memory to 64 MiB. Warnings, including duplicate-profile '
        'density warnings, are retained rather than interpreted as evidence of an incident.', '',
        'Configuration selection and external thresholds still use observed-positive calibration '
        'labels: fitting is unsupervised, the complete selection protocol is not label-free. Same '
        'eight held-year/chronological tests, grouped-round calibration and five fixed budgets. No '
        'identity, stage, official final result or status predictor; no test quota. Outliers need '
        'not be incidents. Positive-only reporting and reused-season limitations still apply.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Novelty study: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Candidate goal met: **{manifest['goal_passed']}**. Full scores/warnings stay in "
              '`artifacts/incident-ml-unsupervised/`. No production source changed.', '']
    (out / 'unsupervised.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700 and not r['control']]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
