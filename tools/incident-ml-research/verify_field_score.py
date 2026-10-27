"""Independent leave-one-out score transform and calibration choice replay."""
import json
import numpy as np
from experiment import evaluation_splits, threshold_for_budget
from objectives import calibration_partition


def verify(root, folder, manifest, meta, scores, read, sha):
    index = {r['key']: i for i, r in enumerate(meta)}
    parent = root/'artifacts/incident-ml-reviewed-history'
    source = json.loads((parent/'manifest.json').read_text())
    for name in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in source[name].items())
    raw = {}
    for filename in ['predictions.csv', 'calibration-scores.csv']:
        for row in read(parent/filename):
            if row['model'] == 'quarantine-only':
                raw.setdefault(row['split'], {})[index[row['key']]] = float(row['score'])
    years, y = np.array([r['year'] for r in meta]), np.array([r['positive'] for r in meta])
    groups = np.array([r['key'].split('/')[0]+'/'+r['category'] for r in meta])
    maximum, comparisons, reference = 0., 0, 0
    trials = read(folder/'tuning.csv')
    for split, outer, test in evaluation_splits(years):
        _, cal = calibration_partition(outer, years, meta, y, 'grouped')
        indices = np.r_[cal, test]
        assert set(raw[split]) == set(indices) and not set(groups[cal]) & set(groups[test])
        p = np.array([raw[split][i] for i in indices])
        p = np.maximum(1e-12, np.minimum(1-1e-12, p))
        margin = np.log(p/(1-p))
        centre = np.median(margin[:len(cal)])
        scale = max(.1, 1.4826*np.median(np.abs(margin[:len(cal)]-centre)))
        for role, ii in [('cal', cal), ('test', test)]:
            assert scores[split, 'quarantine-reference', role] == {int(i): raw[split][i] for i in ii}
            reference += len(ii)
        for choice in [r for r in manifest['selections'] if r['split'] == split]:
            name = choice['model']
            assert choice['studentize'] == (name == 'field-studentized')
            candidates = [r for r in trials if r['split'] == split and r['model'] == name]
            assert [float(r['strength']) for r in candidates] == [.5, 1.]
            best = max(candidates, key=lambda r: float(r['calibration_recall']))
            assert float(best['strength']) == choice['strength']
            assert float(best['calibration_recall']) == choice['calibration_objective']
            expected = margin.copy()
            for j, i in enumerate(indices):
                peers = np.flatnonzero((groups[indices] == groups[i]) & (indices != i))
                if len(peers) < 7:
                    continue
                median = np.median(margin[peers])
                residual = margin[j]-median
                if choice['studentize']:
                    local = max(.1, 1.4826*np.median(np.abs(margin[peers]-median)))
                    residual *= min(2., max(.5, scale/local))
                expected[j] += choice['strength']*len(peers)/(len(peers)+20)*(centre+residual-margin[j])
            stored = np.array([scores[split, name, 'cal' if j < len(cal) else 'test'][i] for j, i in enumerate(indices)])
            maximum = max(maximum, float(np.max(np.abs(expected-stored))))
            objective = np.mean(expected[:len(cal)][y[cal]] > threshold_for_budget(expected[:len(cal)], 700/20340))
            assert objective == choice['calibration_objective']
            comparisons += len(indices)
    assert maximum <= 1e-12
    return dict(field_score_choices_verified=len(manifest['selections']), field_score_values_verified=comparisons,
        maximum_field_score_difference=maximum, field_score_reference_values_verified=reference)
