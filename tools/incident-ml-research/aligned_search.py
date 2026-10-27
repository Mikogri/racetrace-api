"""Run-level alignment/representation ablation and small weighted neural models."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from aligned_runs import sector_matrix, fit_neural, neural_scores
from data import load_dataset, sha
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from objectives import train, score, calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-aligned'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    original, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    ordered = np.array([sector_matrix(f).ravel() for f in sectors])
    sorted_losses = np.array([sector_matrix(f, True).ravel() for f in sectors])
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, predictions, calibration_scores, subgroups, selections = [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([original, paired, weather])
        views = {'aggregate': context, 'ordered': np.hstack([context, ordered]),
                 'loss-sorted': np.hstack([context, sorted_losses])}
        selected = {}
        for view, features in views.items():
            assert np.isfinite(features).all()
            for kind, parameters in [('trees', [7, 15]),
                                     ('neural', [(16, 'tanh', 10.), (32, 'relu', 10.)])]:
                best = None
                for parameter in parameters:
                    fitted_at = time.perf_counter()
                    if kind == 'neural':
                        model, diagnostics = fit_neural(features[fit], y[fit], parameter)
                        values = neural_scores(model, features)
                    else:
                        model = train('all-unlabelled', parameter, features[fit], y[fit], years[fit])
                        values = score(model, features)
                        diagnostics = {}
                    threshold = threshold_for_budget(values[cal], 700 / 20340)
                    objective = float(np.mean(values[cal][y[cal]] > threshold))
                    name = f'{view}-{kind}'
                    tuning.append(dict(split=split, model=name, parameter=json.dumps(parameter),
                        calibration_recall=objective, features=features.shape[1],
                        fit_predict_seconds=time.perf_counter() - fitted_at, diagnostics=json.dumps(diagnostics)))
                    if best is None or objective > best[0]:
                        best = objective, values, dict(model=model, kind=kind, view=view,
                                                      parameter=parameter, diagnostics=diagnostics)
                    if diagnostics.get('warnings'):
                        print(f'{split}: {name} {parameter}: {diagnostics["warnings"]}', flush=True)
                selected[name] = best
                print(f'{split}: {name} evaluated', flush=True)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models={name: result[2] for name, result in selected.items()}, weather_names=names), handle)
        for name, (objective, values, pack) in selected.items():
            selections.append(dict(split=split, model=name, calibration_objective=objective,
                parameter=pack['parameter'], diagnostics=pack['diagnostics'],
                fit_rows=len(fit), calibration_rows=len(cal)))
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session', 'paired_reference']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits / int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores),
                          ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), root / 'tools/incident-ml-research/aligned_runs.py']}, selections=selections,
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter() - started,
        goal_passed=any(r['goal_passed'] for r in pooled), exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Full run-level sector alignment and small neural models', '',
        'Three run-level representations share the same original aggregate, enhanced earlier-session '
        'paired and fit-only round-condition features. Ordered adds all five aligned 17-feature sector '
        'rows; loss-sorted adds those complete rows sorted by log excess (missing rows last, deterministic '
        'lexicographic ties). Time, rank and recovery remain attached to the same sector. Original sector '
        'fraction remains a physical-location feature. Missing slots have explicit missing flags; no '
        'timing or label is invented. Unlike the earlier top-two feature view or latent-sector MIL, '
        'this preserves all sector combinations for a run-level supervised learner.', '',
        'Trees select leaves 7/15 using the existing recipe. Small scikit-learn MLPs select a single '
        '16-unit tanh or 32-unit ReLU layer, alpha=10, Adam batch=512, at most 150 epochs, and seed '
        '20261026. Fit-only signed-log compression, robust scaling and clipping at +/-10 precede the '
        'MLP. Weighted observed-positive/unlabelled classes have equal total mass; this is not an '
        'identified clean/crash likelihood. Early stopping uses training-loss stability only: no '
        'random validation rows can leak an outer calibration event. Warnings and actual iterations '
        'are retained, and a max-iteration warning is not described as convergence. CPU BLAS is '
        'limited to two threads. No dependencies were added.', '',
        'Same eight held-year/chronological tests, grouped-round calibration and five fixed budgets. '
        'Configuration/threshold selection uses calibration only. No stage, identity or final-result '
        'column is a predictor. Reporting/availability bias and reuse of inspected seasons remain; '
        'scores cannot establish crash probabilities or verified precision.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Aligned-run study: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Scores, warnings, actual training iterations "
              'and model choices stay in `artifacts/incident-ml-aligned/`. Production remains unchanged.', '']
    (out / 'aligned.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
