"""Calibration-only combinations of frozen factor and within-field rank scores."""
import argparse
import csv
import json
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from field_objectives import percentile_scores
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def combine(calibration, values, kind, weight=.5):
    calibration, values = np.asarray(calibration, float), np.asarray(values, float)
    if calibration.ndim != 2 or calibration.shape[1] != 2 or not len(calibration):
        raise ValueError('Expected nonempty two-component calibration scores')
    if values.ndim != 2 or values.shape[1] != 2 or not np.isfinite(values).all() or not np.isfinite(calibration).all():
        raise ValueError('Expected finite two-component scores')
    if not np.isfinite(weight) or not 0 <= weight <= 1:
        raise ValueError('Expected a finite weight between zero and one')
    cdf = np.column_stack([percentile_scores(calibration[:, j], values[:, j]) for j in range(2)])
    if kind == 'mean':
        return weight*cdf[:, 0] + (1.-weight)*cdf[:, 1]
    if kind == 'min':
        return cdf.min(axis=1)
    if kind == 'max':
        return cdf.max(axis=1)
    raise ValueError('Unknown combination rule')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-factor-blend'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    _, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    index = {r['key']: i for i, r in enumerate(meta)}
    components, component_hashes = [], {}
    for folder, model in [('incident-ml-field-factor', 'factor-context'),
                          ('incident-ml-field-objectives-enhanced', 'paired-weather-round-category-rank')]:
        source = root / 'artifacts' / folder
        assert json.loads((source / 'manifest.json').read_text())['input_hashes'] == hashes
        component_hashes[str((source / 'manifest.json').relative_to(root))] = sha(source / 'manifest.json')
        component = {}
        for filename, part in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
            component_hashes[str((source / filename).relative_to(root))] = sha(source / filename)
            with (source / filename).open(encoding='utf-8') as handle:
                for row in csv.DictReader(handle):
                    if row['model'] == model:
                        values = component.setdefault((row['split'], part), {})
                        i = index[row['key']]
                        assert i not in values
                        values[i] = float(row['score'])
        components.append(component)
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    recipes = [('mean', .25), ('mean', .5), ('mean', .75), ('min', .5), ('max', .5)]
    rows, pooled, tuning, selections, predictions, calibration_scores, subgroups, disagreements = ([] for _ in range(8))
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        for component in components:
            assert set(component[split, 'cal']) == set(cal) and set(component[split, 'test']) == set(test)
        c = np.column_stack([[component[split, 'cal'][i] for i in cal] for component in components])
        t = np.column_stack([[component[split, 'test'][i] for i in test] for component in components])
        best = None
        for kind, weight in recipes:
            values = combine(c, c, kind, weight)
            threshold = threshold_for_budget(values, 700 / 20340)
            objective = float(np.mean(values[y[cal]] > threshold))
            tuning.append(dict(split=split, model='selected-combination', kind=kind,
                               weight=weight, calibration_recall=objective))
            if best is None or objective > best[0]:
                best = objective, kind, weight, values
        selections.append(dict(split=split, model='selected-combination', kind=best[1],
                               weight=best[2], calibration_objective=best[0]))
        variants = {'factor-reference': (c[:, 0], t[:, 0]), 'rank-reference': (c[:, 1], t[:, 1]),
                    'selected-combination': (best[3], combine(c, t, best[1], best[2]))}
        for budget in BUDGETS:
            a, b = (t[:, j] > threshold_for_budget(c[:, j], budget) for j in range(2))
            disagreements.append(dict(split=split, budget=budget, both=int(np.sum(a & b)),
                factor_only=int(np.sum(a & ~b)), rank_only=int(np.sum(b & ~a)),
                known_both=int(np.sum(y[test] & a & b)), known_factor_only=int(np.sum(y[test] & a & ~b)),
                known_rank_only=int(np.sum(y[test] & b & ~a)), known_neither=int(np.sum(y[test] & ~a & ~b))))
        for name, (cal_values, test_values) in variants.items():
            control = name != 'selected-combination'
            for budget in BUDGETS:
                threshold = threshold_for_budget(cal_values, budget)
                flagged = test_values > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'session', 'status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(v)) for i, v in zip(test, test_values))
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(v)) for i, v in zip(cal, cal_values))
        print(f'{split}: calibration-only combination complete', flush=True)
    for name in variants:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('metrics', rows), ('pooled', pooled), ('tuning', tuning), ('predictions', predictions),
                          ('calibration-scores', calibration_scores), ('subgroups', subgroups),
                          ('component-disagreement', disagreements)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert all(sha(root / p) == digest for p, digest in component_hashes.items())
    manifest = dict(input_hashes=hashes, source_hashes={str(Path(__file__).relative_to(root)): sha(Path(__file__))},
        component_source_hashes=component_hashes, selections=selections, no_model_refit=True,
        protected_sources_unchanged=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Factor/ranking score complementarity', '',
        'Frozen factor-context and enhanced within-round/category rank scores use identical fit/calibration/test '
        'keys and unchanged input hashes. Calibration-only midrank CDFs remove score-unit differences. '
        'Three means (25/50/75% factor weight), minimum consensus and maximum union compete on '
        'known-positive calibration recall at the existing budget. The chosen rule is then evaluated '
        'on all five fixed budgets. No test percentile, test quota or test label selects the rule. '
        'Base scores are controls, not newly trained models. U remains unverified; disagreement '
        'counts do not establish false positives. Previously inspected years remain exploratory.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Score combination: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Component hashes, selections, scores and subgroup/disagreement counts stay in '
              '`artifacts/incident-ml-factor-blend/`. Production remains unchanged.', '']
    (out / 'factor-blend.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_combination=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
