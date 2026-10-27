"""Apply the frozen best models and measure their real offline input/scoring costs."""
import argparse
import csv
import json
import pickle
import time
from pathlib import Path
import numpy as np
from threadpoolctl import threadpool_limits
from data import load_dataset, sha
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from field_factor import field_delays
from rolling_history import records_from_frozen, scoped_features
from objectives import calibration_partition
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-deployment'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    timing = {}

    def measure(name, callback):
        start = time.perf_counter()
        result = callback()
        timing[name] = (time.perf_counter()-start)*1000
        print(f'{name}: {timing[name]:.2f} ms', flush=True)
        return result

    x, meta, sectors, hashes = measure('frozen_corpus_load_and_base_features_ms', lambda: load_dataset(root,
        args.documents, root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv'))
    parent = root/'artifacts/incident-ml-reviewed-history'
    manifest = json.loads((parent/'manifest.json').read_text())
    assert manifest['input_hashes'] == hashes
    for field in ['source_hashes', 'component_source_hashes']:
        assert all(sha(root/p) == digest for p, digest in manifest[field].items())
    paired, _ = measure('earlier_session_features_with_frozen_file_reads_ms', lambda: prepare_pairs(root, meta, enhanced=True))
    _, conditions = measure('environment_with_frozen_file_reads_ms', lambda: prepare_environment(root, meta))
    times = np.full((len(meta), 5), np.nan)
    for i, row in enumerate(meta):
        for j, value in enumerate(row['times']):
            if value and sectors[i][j, 12] == 0 and sectors[i][j, 13] == 0:
                times[i, j] = value
    groups = np.array([r['key'].split('/')[0]+'/'+r['category'] for r in meta])
    configurations = [(rank, penalty) for rank in [1, 2] for penalty in [1.5, 3.]]
    with threadpool_limits(limits=2):
        factors = measure('field_factors_recomputed_ms', lambda:
            [field_delays(times, groups, rank, penalty) for rank, penalty in configurations])
    with np.load(root/'artifacts/incident-ml-field-factor/field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        for j, values in enumerate(factors):
            np.testing.assert_allclose(values, pack[f'configuration-{j}'], atol=1e-12, rtol=0, equal_nan=True)
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
        (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    records = measure('history_records_with_frozen_file_reads_ms', lambda: records_from_frozen(root, meta, sectors))
    index = {r['key']: i for i, r in enumerate(meta)}
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    production = np.array([r['production'] for r in meta])
    cached = {}
    for filename, role in [('predictions.csv', 'test'), ('calibration-scores.csv', 'cal')]:
        with (parent/filename).open(encoding='utf-8') as handle:
            for row in csv.DictReader(handle):
                if row['model'] == 'quarantine-only':
                    cached.setdefault((row['split'], role), {})[index[row['key']]] = float(row['score'])
    rows, packs = [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        if split == 'chronological-2026':
            frozen, rolling = measure('causal_history_recomputed_ms', lambda: scoped_features(records, meta, fit, cal, test))
        else:
            with np.load(root/f'artifacts/incident-ml-rolling-history/{split}-history.npz') as pack:
                frozen, rolling = pack['frozen'], pack['rolling']
        with (parent/f'{split}.pickle').open('rb') as handle:
            saved = pickle.load(handle)
        names = saved['weather_names']
        assert names == sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        features = np.hstack([x, paired, weather, local, rolling])
        model = saved['models']['quarantine-only']['model']
        values = model.predict(features, num_threads=2)
        for role, take in [('cal', cal), ('test', test)]:
            np.testing.assert_allclose(values[take], [cached[split, role][i] for i in take], atol=1e-12, rtol=0)
        threshold = threshold_for_budget(values[cal], .03)
        for name, flags in [('frozen-production', production[test]), ('learned', values[test] > threshold)]:
            rows.append(dict(split=split, model=name, **metrics(y[test], flags, production[test])))
        take = np.r_[cal, test]
        file = f'{split}.features.bin'
        (out/file).write_bytes(np.asarray(features[take], dtype='<f8').tobytes())
        (out/f'{split}.model.json').write_text(json.dumps(model.dump_model()), encoding='utf-8')
        pack = dict(split=split, features=file, model=f'{split}.model.json', rows=len(take), columns=features.shape[1],
            calibration_rows=len(cal), threshold=threshold, expected=values[take].tolist(),
            keys=[meta[i]['key'] for i in take], positive=y[take].tolist(), production=production[take].tolist())
        packs.append(pack)
        if split == 'chronological-2026':
            measures = {}
            for name, subset in [('all_runs', np.arange(len(meta))), ('2026', test),
                                 ('rider_2026', np.array([i for i in test if meta[i]['key'].split('/')[1] == '1']))]:
                if not len(subset):
                    continue
                prepared = np.ascontiguousarray(features[subset])
                model.predict(prepared, num_threads=2)
                elapsed = []
                for _ in range(15):
                    tick = time.perf_counter()
                    model.predict(prepared, num_threads=2)
                    elapsed.append((time.perf_counter()-tick)*1000)
                measures[name] = dict(runs=len(subset), median_ms=float(np.median(elapsed)),
                    p95_ms=float(np.quantile(elapsed, .95)), samples=elapsed)
            timing['prepared_native_scoring'] = measures
    pooled = []
    for name in ['frozen-production', 'learned']:
        take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name]
        pooled.append(dict(model=name, hits=sum(r['hits'] for r in take), alarms=sum(r['alarms'] for r in take),
            positives=sum(r['positives'] for r in take), runs=sum(r['runs'] for r in take)))
    write_csv(out/'accuracy.csv', rows)
    write_csv(out/'pooled.csv', pooled)
    (out/'native-packs.json').write_text(json.dumps(packs), encoding='utf-8')
    report = dict(input_hashes=hashes, source_hash=sha(Path(__file__)), timings=timing, pooled=pooled,
        meaning='Offline corpus feature preparation and native scoring. NOT API/page latency.',
        database_requests=0, api_requests=0, api_integration=False, automatic_time_correction_validated=False)
    (out/'benchmark.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(dict(pooled=pooled, timings=timing)), flush=True)


if __name__ == '__main__':
    main()
