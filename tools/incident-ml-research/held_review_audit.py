"""Auxiliary held-review outcomes; never replace the frozen primary benchmark."""
import argparse
import csv
import json
import math
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from review_supervision import match_reviews
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, write_csv


def review_counts(meta, reviews, indices, flagged):
    if len(indices) != len(flagged) or len(set(indices)) != len(indices):
        raise ValueError('Require unique aligned held indices and decisions')
    result = dict(added_positive_reviews=0, added_positive_alerts=0, negative_reviews=0,
                  negative_alerts=0, disputed_original_positives=0, disputed_alerts=0)
    for i, alert in zip(indices, flagged):
        row = reviews.get(int(i))
        if not row:
            continue
        if row['conflict']:
            result['disputed_original_positives'] += 1
            result['disputed_alerts'] += int(alert)
        elif row['strict'] and not meta[i]['positive']:
            label = 'added_positive' if row['decision'] == 'yes' else 'negative'
            result[label+'_reviews'] += 1
            result[label+'_alerts'] += int(alert)
    return result


def read(path):
    with path.open(encoding='utf-8') as handle:
        return list(csv.DictReader(handle))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--study', type=Path, default=Path('artifacts/incident-ml-reviewed-history'))
    args = parser.parse_args()
    root, folder = Path(__file__).resolve().parents[2], args.study.resolve()
    manifest = json.loads((folder / 'manifest.json').read_text())
    _, meta, _, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    assert hashes == manifest['input_hashes']
    annotation_path = root / 'artifacts/incident-human-review/annotations.json'
    assert sha(annotation_path) == manifest['annotation_hash']
    annotations = json.loads(annotation_path.read_text(encoding='utf-8'))
    reviews, _ = match_reviews(meta, annotations['rows'])
    primary_hashes = {name: sha(folder / name) for name in
        ['manifest.json', 'metrics.csv', 'predictions.csv', 'calibration-scores.csv', 'review-usage.json']}
    index = {r['key']: i for i, r in enumerate(meta)}
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    scores = {}
    for filename, role in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
        for row in read(folder / filename):
            values = scores.setdefault((row['split'], row['model'], role), {})
            i = index[row['key']]
            assert i not in values
            values[i] = float(row['score'])
    partitions = {}
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        partitions[split] = cal, test
    usage = json.loads((folder / 'review-usage.json').read_text())
    result = []
    for row in read(folder / 'metrics.csv'):
        split, model, budget = row['split'], row['model'], float(row['budget'])
        cal, test = partitions[split]
        c, t = scores[split, model, 'cal'], scores[split, model, 'test']
        assert set(c) == set(cal) and set(t) == set(test)
        threshold = threshold_for_budget(np.array([c[i] for i in cal]), budget)
        assert math.isclose(threshold, float(row['threshold']), abs_tol=1e-12)
        use = next((u for u in usage if u['split'] == split and u['model'] == model), None)
        if use is not None:
            assert not set(use['eligible_fit_keys']) & {meta[i]['key'] for i in test}
            assert not set(use['positive_added_keys']+use['negative_weighted_keys']) & {meta[i]['key'] for i in test}
        result.append(dict(split=split, model=model, budget=budget,
            **review_counts(meta, reviews, test, np.array([t[i] for i in test]) > threshold)))
    fields = ['added_positive_reviews', 'added_positive_alerts', 'negative_reviews', 'negative_alerts',
              'disputed_original_positives', 'disputed_alerts']
    pooled = []
    for model in sorted({r['model'] for r in result}):
        for budget in sorted({r['budget'] for r in result}):
            take = [r for r in result if r['split'].startswith('leave-') and r['model'] == model and r['budget'] == budget]
            assert len(take) == 6
            pooled.append(dict(model=model, budget=budget, **{f: sum(r[f] for r in take) for f in fields}))
    write_csv(folder / 'held-review-outcomes.csv', result)
    audit = dict(input_hashes=hashes, annotation_hash=sha(annotation_path), primary_file_hashes=primary_hashes,
        source_hash=sha(Path(__file__)), original_primary_positive_count=int(y.sum()), pooled=pooled,
        evaluated_operating_points=len(result), held_reviews_not_used_for_selection=True,
        primary_benchmark_unchanged=True, purposeful_sample_not_population_precision=True)
    assert all(sha(folder / name) == digest for name, digest in primary_hashes.items())
    (folder / 'held-review-audit.json').write_text(json.dumps(audit, indent=2), encoding='utf-8')
    print(json.dumps([r for r in pooled if r['budget'] == .03]), flush=True)


if __name__ == '__main__':
    main()
