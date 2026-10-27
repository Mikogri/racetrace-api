"""Frozen label-subset ensembles as variance-reduction candidates."""
import argparse
import json
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv
from objectives import calibration_partition
from conditional import goal_passed
from target import BUDGETS
from subbagging import average_scores


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-subbagging'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    _, meta, _, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    source = root/'artifacts/incident-ml-label-efficiency'
    from verify_saved_study import read
    parent = json.loads((source/'manifest.json').read_text())
    assert parent['input_hashes'] == hashes
    proof = json.loads((source/'verification.json').read_text())
    assert proof['production_sources_unchanged'] and proof['research_source_hashes_match']
    components = {str((source/name).relative_to(root)): sha(source/name) for name in
                  ['manifest.json', 'verification.json', 'predictions.csv', 'calibration-scores.csv', 'fit-label-recipes.csv']}
    index = {r['key']: i for i, r in enumerate(meta)}
    raw = {}
    for file in ['predictions.csv', 'calibration-scores.csv']:
        for row in read(source/file):
            values = raw.setdefault((row['split'], row['model']), np.full(len(meta), np.nan))
            assert np.isnan(values[index[row['key']]])
            values[index[row['key']]] = float(row['score'])
    years, y = np.array([r['year'] for r in meta]), np.array([r['positive'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, predictions, calibration, recipes = ([] for _ in range(4))
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        indices = np.r_[cal, test]
        run = sorted(name for s, name in raw if s == split and name.startswith('run-'))
        event = sorted(name for s, name in raw if s == split and name.startswith('event-'))
        assert len(run) == len(event) == 6
        selected = {'full-reference': raw[split, 'full-reference'][indices]}
        for name, names, normalized in [('run-subbagging', run, True), ('event-subbagging', event, True),
                ('all-subbagging', run+event, True), ('all-raw-subbagging', run+event, False)]:
            selected[name] = average_scores([raw[split, key][indices] for key in names], np.arange(len(cal)), normalized)
            recipes.append(dict(split=split, model=name, components=names, calibration_midrank=normalized))
        for name, values in selected.items():
            c, t = values[:len(cal)], values[len(cal):]
            for budget in BUDGETS:
                threshold = threshold_for_budget(c, budget)
                rows.append(dict(split=split, model=name, control=name == 'full-reference', budget=budget,
                    threshold=threshold, **metrics(y[test], t > threshold, baseline[test])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(test, t))
            calibration.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(value)) for i, value in zip(cal, c))
    pooled = []
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits, positives=int(y.sum()),
                alarms=alarms, recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('metrics', rows), ('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, component_source_hashes=components,
        source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__), Path(__file__).with_name('subbagging.py')]},
        fixed_label_subbagging=True, recipes=recipes, new_models_fitted=0,
        protected_sources_unchanged=True, exploratory_reuse_of_previously_inspected_years=True,
        goal_passed=any(r['goal_passed'] for r in pooled if not r['control']))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Frozen positive-label subbagging ensembles', '',
        'Fixed ensembles reuse every earlier run- or event-subsampled component: 25/50/75% fit P '
        'and both predeclared seeds, with fixed eligible U. Hidden known P was excluded, not relabelled '
        'clean; all timing context remains. No component, seed or fraction is selected from test '
        'outcomes. Equal mean of six run components, six event components or all twelve is tested; '
        'the all-twelve raw-score mean is a normalization ablation. Other means use component '
        'midrank CDF fitted on calibration only. No new model fitting or new data. '
        'Component leaves were inherited from the full-label calibration-selected reference. '
        'Previous label-efficiency outcomes have been inspected, so this remains exploratory, '
        'not an independent confirmation. Unknown runs are not verified clean.', '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Subbagging: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['Artifacts: `artifacts/incident-ml-subbagging/`. Production unchanged.', '']
    (out/'subbagging.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps([r for r in pooled if r['budget'] == .03]), flush=True)


if __name__ == '__main__':
    main()
