"""Isolated conversion/parity experiment, never an API integration."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
import onnxruntime as ort
from onnxmltools import convert_lightgbm
from onnxmltools.convert.common.data_types import FloatTensorType
from data import load_dataset, FEATURE_NAMES
from experiment import predict, license_inventory


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml'))
    args = parser.parse_args()
    x, meta, _, _ = load_dataset(args.root.resolve(), args.documents,
        args.root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    x = x.astype(np.float32)
    with (args.output/'chronological-2026-lightgbm.pickle').open('rb') as handle:
        payload = pickle.load(handle)  # Trusted, locally generated artifact only.
    models = payload['models']
    expected = predict(models, x)
    actual = []
    for i, model in enumerate(models):
        converted = convert_lightgbm(model, initial_types=[('features', FloatTensorType([None,len(FEATURE_NAMES)]))],
                                     target_opset=15, zipmap=False)
        path = args.output/f'lightgbm-bag-{i}.onnx'
        path.write_bytes(converted.SerializeToString())
        settings = ort.SessionOptions()
        settings.intra_op_num_threads = 2
        session = ort.InferenceSession(str(path), sess_options=settings, providers=['CPUExecutionProvider'])
        actual.append(session.run(None, {'features':x})[1][:,1])
    actual = np.mean(actual,axis=0)
    difference = np.abs(actual-expected)
    # Inputs/results for a completely separate .NET console demonstration. No
    # identity or label is needed at the inference boundary.
    sample_ids = list(dict.fromkeys([int(np.argmax(difference))]+[i for i,m in enumerate(meta) if m['year']==2026]))[:1000]
    sample = x[sample_ids]
    (args.output/'onnx-inputs.json').write_text(json.dumps(sample.tolist()),encoding='utf-8')
    (args.output/'onnx-expected.json').write_text(json.dumps(predict(models,sample).tolist()),encoding='utf-8')
    (args.output/'onnx-runtime-expected.json').write_text(json.dumps(actual[sample_ids].tolist()),encoding='utf-8')
    tick = time.perf_counter()
    for _ in range(5):
        session.run(None, {'features':sample})
    result = dict(runs=len(x), max_absolute_score_difference=float(np.max(difference)),
                  mean_absolute_score_difference=float(np.mean(difference)), tolerance=2e-5,
                  feature_count=x.shape[1], ensemble_size=len(models), dtype='float32',
                  native_parity_passed=bool(np.max(difference) <= 2e-5),
                  changed_runs_over_tolerance=int(np.sum(difference > 2e-5)),
                  worst_run_key=meta[int(np.argmax(difference))]['key'],
                  last_bag_ms_per_1000=(time.perf_counter()-tick)*1000/5,
                  provider='CPUExecutionProvider', api_integration=False)
    (args.output/'onnx-parity.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    license_inventory(args.output)
    print(json.dumps(result,indent=2))
    if not result['native_parity_passed']:
        raise SystemExit('Native LightGBM/ONNX parity FAILED; do not deploy. Diagnostics were saved.')


if __name__ == '__main__':
    main()
