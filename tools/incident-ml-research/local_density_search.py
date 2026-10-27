"""Same-session density as retrospective context; supervised labels stay held out."""
import argparse
import json
import pickle
import time
import warnings
from collections import Counter
from pathlib import Path
import numpy as np
from local_density import field_density, available_threshold
from unsupervised_runs import positive_profile
from data import load_dataset, FEATURE_NAMES, sha
from focus import relative_columns
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-local-density'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    groups = np.array([r['key'].split('/')[0] for r in meta])
    measured = np.array([np.any(f[:, 12] == 0) for f in sectors])
    views = {'relative': x[:, relative_columns(FEATURE_NAMES)],
             'one-sided': np.array([positive_profile(f)[:30] for f in sectors])}
    densities, warning_rows = {}, []
    for view, features in views.items():
        for fraction in [.1, .25]:
            with warnings.catch_warnings(record=True) as caught:
                warnings.simplefilter('always')
                densities[(view, fraction)] = field_density(features, groups, measured, fraction)
            messages = Counter(f'{type(w.message).__name__}: {w.message}' for w in caught)
            warning_rows.extend(dict(view=view, fraction=fraction, warning=message, count=count)
                                for message, count in sorted(messages.items()))
            print(f'Current-session context computed: {view}, neighbor fraction={fraction}', flush=True)
    density = np.column_stack(list(densities.values()))
    available = np.isfinite(density)
    local = np.hstack([np.where(available, np.log(np.maximum(density, 1e-12)), 0.), (~available).astype(float)])
    np.savez_compressed(out / 'field-scores.npz', keys=np.array([r['key'] for r in meta]), scores=density)
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
        context = np.hstack([x, paired, weather])
        selected = {}
        for view in views:
            best = None
            for fraction in [.1, .25]:
                values = densities[(view, fraction)]
                threshold = available_threshold(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=f'direct-{view}', parameter=fraction, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, dict(kind='direct', view=view, fraction=fraction)
            selected[f'direct-{view}'] = best
        learning_views = {'density-only': local, 'context-reference': context,
                          'density-augmented': np.hstack([context, local]),
                          'availability-only': (~available).astype(float)}
        for name, features in learning_views.items():
            best = None
            for leaves in [7, 15]:
                model = train('all-unlabelled', leaves, features[fit], y[fit], years[fit])
                values = score(model, features)
                threshold = threshold_for_budget(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, parameter=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, dict(kind='learned', view=name, parameter=leaves, model=model)
            selected[name] = best
            print(f'{split}: {name} evaluated', flush=True)
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models={name: result[2] for name, result in selected.items()}, weather_names=names), handle)
        for name, (objective, values, recipe) in selected.items():
            control = name == 'availability-only'
            selections.append(dict(split=split, model=name, calibration_objective=objective,
                control=control, parameters={k: v for k, v in recipe.items() if k != 'model'}))
            for budget in BUDGETS:
                threshold = (available_threshold(values[cal], budget) if recipe['kind'] == 'direct'
                             else threshold_for_budget(values[cal], budget))
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    available_runs=int(np.isfinite(values[test]).sum()),
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            for target, indices in [(predictions, test), (calibration_scores, cal)]:
                target.extend(dict(split=split, model=name, key=meta[i]['key'], available=bool(np.isfinite(values[i])),
                    score=float(values[i]) if np.isfinite(values[i]) else None) for i in indices)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=name == 'availability-only', budget=budget,
                hits=hits, positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores),
                          ('subgroups', subgroups), ('field-warnings', warning_rows)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), root / 'tools/incident-ml-research/local_density.py']}, selections=selections,
        current_session_transductive_context=True, available_density_runs=int(available[:, 0].sum()),
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter()-started,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Same-session density context and learned augmentation', '',
        'This is explicitly retrospective/transductive current-field context, not an inductive '
        'novelty estimator trained exclusively on prior seasons. Each session is normalized and '
        'compared independently using only its measured sporting profiles, without incident labels '
        'or other sessions. Public fit-time LOF factors exclude each point from its own neighbor '
        'set. Neighbor counts are ceil(10%/25% of the field), bounded by available peers and a '
        'two-neighbor preference when possible. Current-field scaling includes the current field '
        'as field medians already do. It is not an online/pre-start forecast.', '',
        'No measured profile or no field peer leaves the density score unknown (blank/available=false), '
        'not zero time or a verified clean run. Direct thresholds use only available calibration '
        'scores but retain the whole calibration population in the alarm-budget denominator. All '
        '20,340 runs / 341 positives still count in evaluation, including unknowns as missed positives.', '',
        'Two pure density views compare relative timing and one-sided positive loss/rank/recovery. '
        'Learned variants compare density-only, unchanged enhanced paired/weather reference and '
        'density-augmented reference; explicit missing flags accompany encoded log-density inputs. '
        'Availability-only is a bias control. Learned estimators and weather encoding fit only '
        'outer-fit rounds. Calibration selects parameters/thresholds; test labels never do. Same '
        'eight held-year/forward checks and five budgets. Reporting/availability bias and previously '
        'inspected-year limitations remain; an outlier is not an identified physical crash.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Local-density study: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Candidate goal met: **{manifest['goal_passed']}**. Full scores/availability/warnings "
              'stay in `artifacts/incident-ml-local-density/`. Production remains unchanged.', '']
    (out / 'local-density.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700 and not r['control']]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
