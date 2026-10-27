"""Frozen sporting measurements only. No identity, status or final result in X."""
import csv
import hashlib
import json
import re
from pathlib import Path
import numpy as np

SECTOR_NAMES = ['log_excess', 'excess_seconds', 'relative_excess', 'robust_deviation',
                'field_log_ratio', 'rank_drop', 'recovery', 'neighbour_contrast',
                'profile_spread', 'field_spread', 'sample_count', 'duration',
                'missing', 'checkpoint', 'rank_percentile', 'sample_ratio', 'sector_fraction']
FEATURE_NAMES = [f'{name}_{stat}' for name in SECTOR_NAMES for stat in ['max', 'median', 'min', 'std']]
FEATURE_NAMES += ['measured_fraction', 'sector_count', 'largest_second_ratio']


def sector_features(times, medians, scales, counts, ranks):
    t = np.array([np.nan if v is None else v for v in times], dtype=float)
    m, mad, n = map(lambda v: np.asarray(v, dtype=float), (medians, scales, counts))
    r = np.array([np.nan if v is None else v for v in ranks], dtype=float)
    valid = np.isfinite(t) & (t > 0) & np.isfinite(m) & (m > 0) & (n >= 3)
    ratio = np.full(len(t), np.nan)
    ratio[valid] = np.log(t[valid] / m[valid])
    percentile = (r-1)/np.maximum(n-1, 1)
    out = np.zeros((len(t), len(SECTOR_NAMES)))
    for i in range(len(t)):
        other = valid.copy()
        other[i] = False
        baseline = float(np.median(ratio[other])) if other.any() else 0.
        spread = float(1.4826*np.median(np.abs(ratio[other]-baseline))) if other.any() else 0.
        if not valid[i]:
            out[i, 12] = 1
            out[i, 16] = i/max(1, len(t)-1)
            continue
        expected = m[i]*np.exp(baseline)
        excess = ratio[i]-baseline
        later = ratio[np.arange(len(t)) > i]
        later = later[np.isfinite(later)]
        recovery = max(0., ratio[i]-float(np.min(later))) if len(later) else 0.
        neighbours = [ratio[j] for j in [i-1, i+1] if 0 <= j < len(t) and valid[j]]
        rank_ref = percentile[other & np.isfinite(percentile)]
        out[i] = [excess, t[i]-expected, np.expm1(excess),
                  excess/max(.02, mad[i], spread), ratio[i],
                  percentile[i]-np.median(rank_ref) if len(rank_ref) and np.isfinite(percentile[i]) else 0.,
                  recovery, ratio[i]-np.median(neighbours) if neighbours else 0.,
                  spread, mad[i], n[i], m[i], 0., 0.,
                  percentile[i] if np.isfinite(percentile[i]) else .5,
                  n[i]/max(1., float(np.median(n))), i/max(1, len(t)-1)]
    for i in range(len(t)-1):
        if not (valid[i] and valid[i+1]):
            continue
        # A quality feature, NOT a crash label. Complementary checkpoint errors
        # preserve the pair's time while transferring time between the sectors.
        others = valid.copy()
        others[i:i+2] = False
        b = np.median(ratio[others]) if others.any() else 0.
        predicted = m[i:i+2]*np.exp(b)
        residual = t[i:i+2]-predicted
        if residual[0]*residual[1] < 0 and min(t[i:i+2]/predicted) < .65 and abs(sum(residual)) <= max(1., .05*sum(predicted)):
            out[i:i+2, 13] = 1
    return np.nan_to_num(out, nan=0., posinf=1e4, neginf=-1e4)


def aggregate_features(sectors):
    if len(sectors) == 0:
        result = np.zeros(len(FEATURE_NAMES))
        result[-3] = 0.
        return result
    result = []
    for col in sectors.T:
        result.extend([np.max(col), np.median(col), np.min(col), np.std(col)])
    positive = np.sort(np.maximum(sectors[:, 0], 0.))
    result.extend([1.-np.mean(sectors[:, 12]), len(sectors),
                   positive[-1]/max(.02, positive[-2] if len(positive)>1 else .02)])
    return np.nan_to_num(np.asarray(result), posinf=1e4, neginf=-1e4)


def explicit_sectors(description):
    return sorted({int(v) for v in re.findall(r'\b(?:sektor(?:ze|a|u|em)?|sector)\s*(\d+)\b', description, re.I)})


def read_explicit_labels(documents, fixture):
    labels = {}
    for year in range(2021, 2026):
        headers, index = [], 0
        for raw in (documents/f'RaceTrace_Incidents_{year}.md').read_text(encoding='utf-8-sig').splitlines():
            if not raw.strip().startswith('|'):
                continue
            cells = [c.strip() for c in raw.strip().strip('|').split('|')]
            if 'Zawodnik' in cells:
                headers = cells
                continue
            if len(cells) != len(headers) or not any(c in ('`CRASH`', '`OFF_COURSE`') for c in cells):
                continue
            index += 1
            description = next((cells[j] for j,h in enumerate(headers) if 'opis' in h.lower()), '')
            labels[f'{year}-{index:03}'] = explicit_sectors(description)
    with fixture.open(encoding='utf-8-sig', newline='') as handle:
        for row in csv.DictReader(handle):
            labels[row['incident_record_id']] = [int(v) for v in re.findall(r'\d+', row['sector_index'])]
    return labels


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_dataset(root, documents, fixture):
    study = root/'artifacts/incident-conservative'
    study_manifest = json.loads((study/'summary.json').read_text(encoding='utf-8-sig'))
    for source, digest in study_manifest['InputHashes'].items():
        path = Path(source)
        if path.name.startswith('RaceTrace_Incidents_'):
            path = documents/path.name
        if path.exists() and sha(path).lower() != digest.lower():
            raise ValueError(f'Frozen C# study source has changed: {path.name}')
    matched = list(csv.DictReader((study/'labels.csv').open(encoding='utf-8-sig', newline='')))
    positive, known_sectors = set(), {}
    explicit = read_explicit_labels(documents, fixture)
    for row in matched:
        if row['state'] == 'matched':
            key = f"{row['session_id']}/{row['rider_id']}"
            positive.add(key)
            known_sectors.setdefault(key, set()).update(explicit.get(row['record'], []))
    high_flags, high_z = set(), {}
    for row in csv.DictReader((root/'artifacts/incident-csharp/sectors.csv').open(encoding='utf-8-sig', newline='')):
        key = f"{row['session_id']}/{row['rider_id']}"
        if row['classification'] == 'LikelyIncident':
            high_flags.add(key)
        high_z[key] = max(high_z.get(key, 0.), float(row['dimensionless_deviation'] or 0.))
    meta, values, sectors_out, fingerprints = [], [], [], {}
    for year in range(2021, 2027):
        folder = root/f'artifacts/incident-multiseason/{year}'
        inputs, old, current = [folder/name for name in [f'inputs-{year}.json', 'baseline.json', 'conservative.json']]
        for path in [inputs, old, current]:
            fingerprints[str(path.relative_to(root))] = sha(path)
        bundle = json.loads(inputs.read_text(encoding='utf-8-sig'))
        original = {f"{r['SessionId']}/{r['RiderId']}": r['Result'] for r in json.loads(old.read_text(encoding='utf-8-sig'))}
        active = {f"{r['SessionId']}/{r['RiderId']}": r['Result'] for r in json.loads(current.read_text(encoding='utf-8-sig'))}
        sessions = {str(s['id']): s for s in bundle['Data']['Sessions']}
        names = {str(r['rider_id']): ' '.join([r['riders'].get('first_name') or '', r['riders'].get('last_name') or '']) for r in bundle['Data']['Results']}
        for run in bundle['PaceSessions']:
            key = f"{run['SessionId']}/{run['Run']['RiderId']}"
            if key not in original:
                continue
            s = run['Sectors']
            f = sector_features([v['TimeSeconds'] for v in s], [v['Statistics']['Median'] for v in s],
                                [v['Statistics']['LogMadScale'] for v in s], [v['Statistics']['Count'] for v in s], [v['Rank'] for v in s])
            features = aggregate_features(f)
            session = sessions[run['SessionId']]
            before, now = original[key], active[key]
            def score(result):
                flag = result['IncidentSectorIndex'] is not None
                zs = [v.get('Anomaly', {}).get('RobustDeviation') for v in result['Sectors'] if v.get('Anomaly')]
                return float(flag)*100 + min(50., max([0.] + [z for z in zs if z is not None]))
            localized = sorted(x for x in known_sectors.get(key, []) if 1 <= x <= len(s))
            # Identity/context are output metadata only; never part of features.
            meta.append(dict(key=key, year=year, group=f"{year}/{session['round_id']}",
                             category=session['categories']['code'], rider=names[run['Run']['RiderId']],
                             venue=session['rounds']['tracks']['venue'], session=session['session_type'],
                             status=run['Run'].get('Status'), positive=key in positive,
                             known_sectors=localized, production=before['IncidentSectorIndex'] is not None,
                             current=now['IncidentSectorIndex'] is not None, high_recall=key in high_flags,
                             production_score=score(before), current_score=score(now),
                             high_recall_score=100*float(key in high_flags)+min(50.,high_z.get(key,0.)),
                             times=[v['TimeSeconds'] for v in s],
                             best=[v['Statistics'].get('BestValue') for v in s]))
            values.append(features)
            sectors_out.append(f)
    assert len(meta) == 20340 and sum(m['positive'] for m in meta) == 341, 'Frozen corpus changed: inspect matching before training.'
    for path in [study/'labels.csv', root/'artifacts/incident-csharp/sectors.csv', fixture]:
        fingerprints[str(path.relative_to(root))] = sha(path)
    for year in range(2021, 2026):
        path = documents/f'RaceTrace_Incidents_{year}.md'
        fingerprints[path.name] = sha(path)
    return np.asarray(values), meta, sectors_out, fingerprints
