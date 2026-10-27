"""Reassemble exact exported context and one causal history scope for fair CPU timing."""
import json
import pickle
from pathlib import Path
import numpy as np
from data import sha

root = Path(__file__).resolve().parents[2]
folder = root/'artifacts/incident-deployment'
packs = json.loads((folder/'native-packs.json').read_text())
with np.load(root/'artifacts/incident-ml-rolling-history/chronological-2026-history.npz') as history:
    keys, rolling = history['keys'].tolist(), history['rolling']
index = {key: i for i, key in enumerate(keys)}
columns = packs[-1]['columns']
base_columns = columns-rolling.shape[1]
features = np.zeros((len(keys), columns))
seen, comparisons = np.zeros(len(keys), bool), 0
with (root/'artifacts/incident-ml-reviewed-history/chronological-2026.pickle').open('rb') as handle:
    reference = pickle.load(handle)
for pack in packs:
    with (root/f"artifacts/incident-ml-reviewed-history/{pack['split']}.pickle").open('rb') as handle:
        assert pickle.load(handle)['weather_names'] == reference['weather_names']
    values = np.fromfile(folder/pack['features'], dtype='<f8').reshape(pack['rows'], columns)
    for key, row in zip(pack['keys'], values):
        i = index[key]
        if seen[i]:
            np.testing.assert_array_equal(features[i, :base_columns], row[:base_columns])
            comparisons += 1
        else:
            features[i, :base_columns], seen[i] = row[:base_columns], True
assert seen.all()
features[:, base_columns:] = rolling
last = next(p for p in packs if p['split'] == 'chronological-2026')
rows2026 = [index[key] for key in last['keys'][last['calibration_rows']:]]
values = reference['models']['quarantine-only']['model'].predict(features, num_threads=2)
np.testing.assert_array_equal(values[[index[k] for k in last['keys']]], last['expected'])
path = folder/'chronological-2026.all-features.bin'
path.write_bytes(np.asarray(features, dtype='<f8').tobytes())
metadata = dict(file=path.name, sha256=sha(path), rows=len(keys), columns=columns,
    verified_repeated_context_rows=comparisons, model=last['model'],
    subsets=dict(all=list(range(len(keys))), season2026=rows2026,
        rider2026=[i for i in rows2026 if keys[i].endswith('/1')]))
(folder/'full-inputs.json').write_text(json.dumps(metadata), encoding='utf-8')
print(json.dumps({k: v for k, v in metadata.items() if k != 'subsets'}))
