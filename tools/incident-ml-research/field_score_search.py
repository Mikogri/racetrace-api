"""Frozen strongest model plus label-free within-field score normalization."""
import argparse
import json
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv
from objectives import calibration_partition
from target import BUDGETS
from conditional import goal_passed
from field_score_context import adjust_field_scores


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-field-score'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    _, meta, _, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    parent = root/'artifacts/incident-ml-reviewed-history'
    source = json.loads((parent/'manifest.json').read_text())
    assert source['input_hashes'] == hashes
    for name in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in source[name].items())
    components = {str((parent/f).relative_to(root)): sha(parent/f) for f in
                  ['manifest.json', 'verification.json', 'predictions.csv', 'calibration-scores.csv']}
    from verify_saved_study import read
    index = {r['key']: i for i, r in enumerate(meta)}
    raw = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        for row in read(parent/filename):
            if row['model'] == 'quarantine-only':
                values = raw.setdefault(row['split'], np.full(len(meta), np.nan))
                assert np.isnan(values[index[row['key']]])
                values[index[row['key']]] = float(row['score'])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    groups = np.array([r['key'].split('/')[0]+'/'+r['category'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, tuning, selections, predictions, calibration = ([] for _ in range(5))
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not set(groups[cal]) & set(groups[test])
        indices = np.r_[cal, test]
        p, g, c = raw[split][indices], groups[indices], np.arange(len(cal))
        selected = {'quarantine-reference': p}
        for name, studentize in [('field-centering', False), ('field-studentized', True)]:
            best = None
            for strength in [.5, 1.]:
                values = adjust_field_scores(p, g, c, strength, studentize)
                objective = float(np.mean(values[c][y[cal]] > threshold_for_budget(values[c], 700/20340)))
                tuning.append(dict(split=split, model=name, strength=strength, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, values, strength
            selected[name] = best[1]
            selections.append(dict(split=split, model=name, strength=best[2], studentize=studentize,
                                   calibration_objective=best[0]))
        for name, values in selected.items():
            cv, tv = values[:len(cal)], values[len(cal):]
            for budget in BUDGETS:
                threshold = threshold_for_budget(cv, budget)
                rows.append(dict(split=split, model=name, control=name == 'quarantine-reference', budget=budget,
                    threshold=threshold, **metrics(y[test], tv > threshold, baseline[test])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(test, tv))
            calibration.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(cal, cv))
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits, positives=int(y.sum()),
                alarms=alarms, recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('metrics', rows), ('tuning', tuning), ('pooled', pooled),
                         ('predictions', predictions), ('calibration-scores', calibration)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, component_source_hashes=components,
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('field_score_context.py')]},
        field_score_context=True, selections=selections, protected_sources_unchanged=True,
        new_models_fitted=0, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Within-field frozen-score location/scale adjustment', '',
        'The strongest frozen predictor is unchanged. Its probabilities are clipped only for finite '
        'logit conversion, then each run uses the median score of OTHER runs in the same session/category '
        '(at least eight total scored runs). Shrinkage n/(n+20) tempers small fields. Calibration-wide '
        'median/MAD provide the global location/scale; studentization clips the scale ratio to 0.5–2, '
        'MAD floor 0.1. Strength 0.5/1 is calibration-selected; no held labels, identities, status or '
        'official finish data enter the transform. Test context is explicitly transductive within '
        'its own completed field. Neither a within-field percentile cutoff nor an alarm quota is '
        'imposed. No online claim. U remains contaminated, and margins are not crash probabilities.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
              [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Field-score context: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-field-score/`. Production unchanged.', '']
    (out/'field-score.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled), flush=True)


if __name__ == '__main__':
    main()
