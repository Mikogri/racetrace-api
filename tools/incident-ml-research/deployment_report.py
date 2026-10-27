"""Render the measured deployment-readiness comparison without claiming an API A/B."""
import csv
import json
from pathlib import Path
from data import sha
from report import table

root = Path(__file__).resolve().parents[2]
folder = root/'artifacts/incident-deployment'
benchmark = json.loads((folder/'benchmark.json').read_text())
native = json.loads((folder/'csharp-native-benchmark.json').read_text())
http = json.loads((folder/'production-http.json').read_text())
assert native['NativeScoresCompared'] == 61644 and native['MaximumScoreDifference'] <= 2e-12
assert native['ChangedLearnedDecisions'] == native['FrozenProductionFlagDifferences'] == 0
assert native['ProductionAlarms'] == 1877
rows = list(csv.DictReader((folder/'accuracy.csv').open(encoding='utf-8')))
pooled = list(csv.DictReader((folder/'pooled.csv').open(encoding='utf-8')))
learned = [r for r in rows if r['model'] == 'learned' and r['split'].startswith('leave-')]
added, lost = sum(int(r['additional_hits']) for r in learned), sum(int(r['lost_hits']) for r in learned)
protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
assert all(sha(root/p) == digest for p, digest in protected.items())
lines = ['## Best measured model: deployment-readiness and latency comparison', '',
    'Status: a working dependency-free C# native-tree evaluator now reproduces the BEST frozen '
    'models exactly. This bypasses the earlier ONNX conversion problem, but is an isolated '
    'executable, NOT an active API replacement. The production calculator is NOT switched. '
    'This distinction is required because run classification has not validated sector localization '
    'or counterfactual time correction, and its 343-feature constructor is not yet ported into '
    'the API. No commit, push, deployment, migration, cache purge or official result edit.', '',
    'The production baseline was built separately from committed `2796eae4a07fdc8635a745f9b66052954f31eda8`, '
    'also verified as GitHub main during this measurement. All 20,340 direct detector flags match '
    'the frozen production baseline (zero differences). No existing dirty calculator files were reverted.', '',
    table(['Method', 'Known hits / 341', 'Missed known', 'All alarms', 'Other/unconfirmed alarms'],
        [(r['model'], r['hits'], 341-int(r['hits']), r['alarms'], int(r['alarms'])-int(r['hits'])) for r in pooled]), '',
    f'The learned method adds {added} original positives but loses {lost} previously detected positives '
    f'(net +{added-lost}). Other alarms are NOT verified false positives: labels remain incomplete. '
    '235/653 pools SIX held-season models, once per run; it is not the in-sample result of a single '
    'all-year deployment model. The chronological-2026 model is used only for CPU workload timing.', '',
    table(['Forward test', 'Method', 'Hits', 'Known P', 'Alarms'],
        [(r['split'], r['model'], r['hits'], r['positives'], r['alarms']) for r in rows if r['split'].startswith('chronological-')]), '',
    '### Local CPU timing', '',
    f"Production detector only, 20,340 already-materialized inputs: median {native['ProductionDetectorMedianMs']:.2f} ms "
    '(11 repetitions). Compiled expression dispatch avoids per-call reflection. Input file reading, '
    'JSON parsing, API, database and network are excluded.', '',
    table(['Learned prepared workload', 'Runs', 'Median ms', 'P95 ms'],
        [(r['Subset'], r['Runs'], f"{r['MedianMs']:.3f}", f"{r['P95Ms']:.3f}") for r in native['FullSetPreparedScoring']]), '',
    f"Native C# vs Python: {native['NativeScoresCompared']} scores, maximum difference "
    f"{native['MaximumScoreDifference']:.17g}, {native['ChangedLearnedDecisions']} changed decisions. "
    'Warm scoring uses 15 repetitions; P95 interpolates sorted measured samples. The full-set '
    'context was reconstructed only from identical existing exported columns plus the exact '
    'chronological history scope, not fabricated or approximated inputs.', '',
    '### Actual research input-preparation costs', '',
    table(['Component on all 20,340 runs', 'Observed ms'],
        [(key, f'{value:.2f}') for key, value in benchmark['timings'].items() if isinstance(value, (int, float))]), '',
    'These are ONE measured reconstruction of the offline research implementation. Some phases '
    'reread six frozen JSON files; corpus loading also checks labels/baselines and is not a minimal '
    'production feature constructor. They MUST NOT be presented as measured API latency or simply '
    'added to current page latency. Nevertheless, using this unchanged Python reconstruction inside '
    'requests would introduce substantial work. Fast model evaluation alone does not establish '
    'fast end-to-end requests. A production port or correctly invalidated prepared-input cache '
    'requires separate parity and request-level measurements.', '',
    '### Existing production HTTP observations', '',
    table(['Route', 'Successful GETs', 'Min ms', 'Median ms', 'Max ms'],
        [(name, r['successful_samples'], f"{r['min_ms']:.2f}", f"{r['median_ms']:.2f}", f"{r['max_ms']:.2f}")
         for name, r in http['summary'].items()]), '',
    'Three ordinary GETs per route through https://racetracemtb.com, including TLS/network, '
    'Cloudflare, Railway and body transfer. Cache state is uncontrolled; no forced misses or '
    'restarts. These are NOT a browser render benchmark, cold-cache proof, database-time split '
    'or new-model A/B. No Server-Timing breakdown was exposed. The first observed requests were '
    'slower; the measurement alone does not identify which cache/network/server phase caused it.', '',
    '### Remaining integration requirements', '',
    '1. Port and verify the actual 343 input features, including previous-session cohort changes, '
    'same-field factors, weather vocabulary and strictly earlier history. Preserve full same-category '
    'fields under ranking search, rider details and filter changes; do not substitute missing '
    'historical context with arbitrary zeros.', '',
    '2. Define and validate sector localization and allowed time correction independently. A '
    'run-level score alone is not evidence that its highest residual is a safely removable loss.', '',
    '3. Select/version a deployable model and threshold without presenting training-year outputs '
    'as held validation. Then benchmark both complete API paths with identical inputs/cache '
    'conditions and inspect rating/Pace/Consistency changes before switching.', '',
    'Artifacts: `artifacts/incident-deployment/`. No production activation is claimed.', '']
(folder/'deployment.md').write_text('\n'.join(lines), encoding='utf-8')
proof = dict(native_score_parity=True, native_decision_parity=True, production_flags_match_committed_head=True,
    protected_sources_unchanged=True, api_activated=False, end_to_end_new_model_latency_measured=False,
    files={name: sha(folder/name) for name in ['benchmark.json', 'csharp-native-benchmark.json',
        'production-http.json', 'accuracy.csv', 'pooled.csv', 'native-packs.json', 'full-inputs.json']},
    source_hashes={str(p.relative_to(root)): sha(p) for p in [Path(__file__),
        Path(__file__).with_name('deployment_benchmark.py'), Path(__file__).with_name('complete_benchmark_inputs.py'),
        Path(__file__).with_name('production_http_benchmark.py'), Path(__file__).parent/'native-parity/Program.cs']})
(folder/'deployment-verification.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
print(folder/'deployment.md')
