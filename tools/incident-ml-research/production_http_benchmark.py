"""Small read-only production HTTP sample; no cache clearing or API mutation."""
import gzip
import json
import statistics
import time
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parents[2]
folder = root/'artifacts/incident-deployment'
folder.mkdir(parents=True, exist_ok=True)
origin = 'https://racetracemtb.com'
common = 'categories=EM&pageSize=20&sortBy=rating&sortDirection=desc'
paths = {'ranking2026': '/api/v1/ranking?'+common+'&seasons=2026',
         'rankingAll': '/api/v1/ranking?'+common}
samples = []
rider = None
for repeat in range(3):
    for name, path in list(paths.items()):
        tick = time.perf_counter()
        request = urllib.request.Request(origin+path, headers={
            'Accept': 'application/json', 'Accept-Encoding': 'gzip',
            'User-Agent': 'RaceTrace-read-only-benchmark/1.0'})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                headers_ms = (time.perf_counter()-tick)*1000
                encoded = response.read()
                total_ms = (time.perf_counter()-tick)*1000
                decoded = gzip.decompress(encoded) if response.headers.get('Content-Encoding') == 'gzip' else encoded
                payload = json.loads(decoded)
                if name == 'ranking2026' and rider is None:
                    rider = next((r['riderId'] for r in payload['items'] if r.get('fullName', '').lower() == 'loic bruni'),
                                 payload['items'][0]['riderId'])
                    paths['rider2026'] = f'/api/v1/riders/{rider}/overview?categories=EM&seasons=2026'
                    paths['riderAll'] = f'/api/v1/riders/{rider}/overview?categories=EM'
                samples.append(dict(route=name, path=path, repeat=repeat, status=response.status,
                    headers_ms=headers_ms, total_ms=total_ms, encoded_bytes=len(encoded), decoded_bytes=len(decoded),
                    content_encoding=response.headers.get('Content-Encoding'), server_timing=response.headers.get('Server-Timing'),
                    cache=response.headers.get('CF-Cache-Status')))
                print(f'{name}/{repeat}: {total_ms:.1f} ms, {response.status}, {len(encoded)} encoded bytes', flush=True)
        except Exception as error:
            samples.append(dict(route=name, path=path, repeat=repeat, error=str(error),
                                elapsed_ms=(time.perf_counter()-tick)*1000))
            print(f'{name}/{repeat}: {type(error).__name__}', flush=True)
# The first pass discovers the rider; obtain its third sample without adding
# any extra ranking requests. Every route has exactly three normal GET samples.
for name in ['rider2026', 'riderAll']:
    if name not in paths:
        continue
    while len([r for r in samples if r['route'] == name]) < 3:
        path = paths[name]
        tick = time.perf_counter()
        try:
            with urllib.request.urlopen(urllib.request.Request(origin+path, headers={'Accept':'application/json',
                    'Accept-Encoding':'gzip', 'User-Agent':'RaceTrace-read-only-benchmark/1.0'}), timeout=45) as response:
                headers_ms = (time.perf_counter()-tick)*1000
                encoded = response.read()
                decoded = gzip.decompress(encoded) if response.headers.get('Content-Encoding') == 'gzip' else encoded
                json.loads(decoded)
                samples.append(dict(route=name, path=path, repeat=2, status=response.status, headers_ms=headers_ms,
                    total_ms=(time.perf_counter()-tick)*1000, encoded_bytes=len(encoded), decoded_bytes=len(decoded),
                    content_encoding=response.headers.get('Content-Encoding'), server_timing=response.headers.get('Server-Timing'),
                    cache=response.headers.get('CF-Cache-Status')))
        except Exception as error:
            samples.append(dict(route=name, path=path, repeat=2, error=str(error)))
summary = {}
for name in paths:
    times = [r['total_ms'] for r in samples if r['route'] == name and r.get('status') == 200]
    if times:
        summary[name] = dict(successful_samples=len(times), min_ms=min(times), median_ms=statistics.median(times), max_ms=max(times))
report = dict(origin=origin, rider_id=rider, samples=samples, summary=summary,
    scope='Live production HTTP via Cloudflare. Includes network/TLS/proxy/API/body; NOT browser rendering or a new-model A/B.',
    cache_state='Uncontrolled; no purge, admin request, forced miss or restart.',
    new_model_deployed=False, exact_database_time_available=False)
(folder/'production-http.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(summary), flush=True)
