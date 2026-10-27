"""Independent cached-score arithmetic and fixed-component provenance audit."""
import json
import numpy as np


def verify(root, folder, manifest, meta, scores, read, sha):
    parent = root/'artifacts/incident-ml-label-efficiency'
    source = json.loads((parent/'manifest.json').read_text())
    for name in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in source[name].items())
    proof = json.loads((parent/'verification.json').read_text())
    assert proof['production_sources_unchanged'] and proof['research_source_hashes_match']
    index = {r['key']: i for i, r in enumerate(meta)}
    raw = {}
    for filename, role in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
        for row in read(parent/filename):
            raw.setdefault((row['split'], row['model'], role), {})[index[row['key']]] = float(row['score'])
    maximum, comparisons, recipes = 0., 0, manifest['recipes']
    assert len(recipes) == 32
    for recipe in recipes:
        split, name = recipe['split'], recipe['model']
        members = recipe['components']
        run = sorted(key for s, key, role in raw if s == split and role == 'cal' and key.startswith('run-'))
        event = sorted(key for s, key, role in raw if s == split and role == 'cal' and key.startswith('event-'))
        expected = run if name == 'run-subbagging' else event if name == 'event-subbagging' else run+event
        assert members == expected and len(run) == len(event) == 6
        assert recipe['calibration_midrank'] == (name != 'all-raw-subbagging')
        for role in ['cal', 'test']:
            indices = list(scores[split, name, role])
            vectors = []
            for member in members:
                assert set(raw[split, member, role]) == set(indices)
                values = np.array([raw[split, member, role][i] for i in indices])
                if recipe['calibration_midrank']:
                    calibration = np.sort(list(raw[split, member, 'cal'].values()))
                    values = (np.searchsorted(calibration, values, 'left')+
                              np.searchsorted(calibration, values, 'right'))/(2*len(calibration))
                vectors.append(values)
            # Independent aggregation without the study's averaging helper.
            expected = np.sum(np.array(vectors), axis=0)/len(members)
            stored = np.array([scores[split, name, role][i] for i in indices])
            maximum = max(maximum, float(np.max(np.abs(expected-stored))))
            comparisons += len(indices)
    reference_count = 0
    for (split, name, role), values in scores.items():
        if name == 'full-reference':
            assert values == raw[split, 'full-reference', role]
            reference_count += len(values)
    assert maximum <= 1e-12
    return dict(subbagging_recipes_verified=len(recipes), subbagging_scores_verified=comparisons,
        maximum_subbagging_difference=maximum, subbagging_reference_scores_verified=reference_count,
        parent_refit_proof_hash=sha(parent/'verification.json'), new_model_fits=0)
