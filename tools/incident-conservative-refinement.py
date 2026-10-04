"""Offline follow-up experiment; not imported by the API or promoted to Pace."""
import csv
import hashlib
import importlib.util
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('incident_experiments', ROOT/'tools/incident-experiments.py')
audit = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = audit
spec.loader.exec_module(audit)

OLD = audit.Candidate('local', 1.5, 4, 'any', 1)
LARGE = audit.Candidate('local', 4, 4, 'any', 1)
TOP19 = ['Loic Bruni', 'Asa Vermette', 'Jordan Williams', 'Jackson Goldstone', 'Finn Iles',
         'Max Alran', 'Amaury Pierron', 'Andreas Kolb', 'Ryan Pinkerton', 'Lachlan Stevens-McNab',
         'Luca Shaw', 'Till Alran', 'Troy Brosnan', 'Loris Vergier', 'Jakob Jewett',
         'Antoine Pierron', 'Martin Maes', 'Henri Kiefer', 'Nathan Pontvianne']
SESSION_NAMES = {0: 'Final', 1: 'Semi', 2: 'Quali 1', 3: 'Quali 2', 4: 'Quali', 5: 'Seeding'}


def detect(run, features):
    flagged = {f['index']: dict(f, rule='large-loss') for f in audit.detect(features, LARGE)}
    sectors = run['Sectors']
    usable = {f['index'] for f in features}
    for f in features:
        i = f['index']
        # A modest loss needs measured pace both before and after it, rather than
        # treating the final checkpoint's lack of recovery as positive evidence.
        if not (i-1 in usable and i+1 in usable and f['loss'] >= 2 and f['z'] >= 2
                and f['clean'] >= 3 and f['rank_drop'] >= .5
                and f['bracketed'] and f['recovery'] and sectors[i]['Statistics']['Count'] >= 6):
            continue
        others = [s for j, s in enumerate(sectors) if j != i and j in usable and s['Rank'] is not None
                  and audit.valid(s['TimeSeconds']) and s['Statistics']['Count'] >= 6]
        if len(others) < 3:
            continue
        ranks = [(s['Rank']-1)/(s['Statistics']['Count']-1) for s in others]
        if audit.median(ranks) > .15:
            continue
        # Both neighbouring checkpoints must actually return a top-field rank.
        neighbours = [sectors[i-1], sectors[i+1]]
        if not all(s['Rank'] is not None and s['Statistics']['Count'] >= 6
                   and (s['Rank']-1)/(s['Statistics']['Count']-1) <= .2 for s in neighbours):
            continue
        flagged.setdefault(i, dict(f, rule='isolated-top-pace'))
    return list(flagged.values())


def self_check():
    def fixture(loss):
        return {'Sectors': [{'TimeSeconds': 50+loss if i == 2 else 50, 'Rank': 25 if i == 2 else 1,
                            'Statistics': {'Median': 50, 'Count': 30, 'LogMadScale': .01, 'LinearMadScale': .5}}
                           for i in range(5)]}
    for loss, expected in [(0, []), (1.6, []), (2.5, [2]), (8, [2])]:
        run = fixture(loss)
        assert [f['index'] for f in detect(run, audit.features(run, 'local'))] == expected
    run = fixture(2.5)
    assert not detect(run, audit.features(run, 'local', [1]))
    for i in (0, 1, 3, 4):
        run['Sectors'][i]['Rank'] = 15
    assert not detect(run, audit.features(run, 'local'))


def main():
    self_check()
    inputs = ROOT/'artifacts/incident-audit'
    data = json.loads((inputs/'inputs-2026.json').read_text(encoding='utf-8-sig'))
    baseline = {(r['SessionId'], r['RiderId']): r['Result']
                for r in json.loads((inputs/'current.json').read_text(encoding='utf-8-sig'))}
    runs = {(r['SessionId'], r['Run']['RiderId']): r for r in data['PaceSessions']
            if (r['SessionId'], r['Run']['RiderId']) in baseline}
    sessions = {str(s['id']): s for s in data['Data']['Sessions']}
    names = {str(r['rider_id']): r['riders']['first_name']+' '+r['riders']['last_name'] for r in data['Data']['Results']}
    with (ROOT/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv').open(encoding='utf-8-sig', newline='') as file:
        labels, excluded = audit.match_labels(list(csv.DictReader(file)), runs, sessions, names)
    features = {k: audit.features(r, 'local', [s['SectorIndex'] for s in baseline[k]['Sectors'] if s['Anomaly']['Kind'] == 3])
                for k, r in runs.items()}
    old = {k: audit.detect(fs, OLD) for k, fs in features.items()}
    new = {k: detect(runs[k], fs) for k, fs in features.items()}
    if '--verify-production' in sys.argv:
        production = {(r['SessionId'], r['RiderId']): r['Result'] for r in
                      json.loads((inputs/'refined-production.json').read_text(encoding='utf-8-sig'))}
        assert production.keys() == runs.keys()
        for key, detected in new.items():
            expected = {f['index']: f['loss'] for f in detected}
            actual = {s['SectorIndex']: s['Anomaly']['LossSeconds'] for s in production[key]['Sectors']
                      if s['Anomaly']['Kind'] == 2}
            assert expected.keys() == actual.keys(), (key, expected, actual)
            assert all(abs(expected[i]-actual[i]) < 1e-7 for i in expected), key
        print(f'Production parity: {len(runs)} runs, identical detected sectors and estimated losses.')
    old_hits = {k for k, fs in old.items() if fs}
    new_hits = {k for k, fs in new.items() if fs}
    assert len(old_hits) == 349 and len(old_hits & labels.keys()) == 53

    def key_for(name, session):
        matching = [k for k in runs if k[0] == session and audit.norm(names[k[1]]) == audit.norm(name)]
        assert len(matching) == 1, (name, session)
        return matching[0]

    # Explicit development feedback, NOT a held-out validation set. Labels are
    # never used by detect(), and the original positive-only CSV stays unchanged.
    for name, session, sector in [('Nathan Pontvianne', '143', 4), ('Luca Shaw', '107', 4), ('Andreas Kolb', '133', 4)]:
        assert sector not in {f['index'] for f in new[key_for(name, session)]}
    for name, session, sector in [('Amaury Pierron', '113', 3), ('Max Alran', '465', 2)]:
        assert sector in {f['index'] for f in new[key_for(name, session)]}

    def fmt(fs):
        return '; '.join(f"S{f['index']+1}: {f['loss']:.2f} s" for f in sorted(fs, key=lambda f: f['index'])) or '—'

    report = ['# Conservative detector refinement — 2026-10-01', '',
              'Offline experiment only. Timing estimates are not confirmed accident losses or applied Pace corrections.',
              'Rules were designed using the five user-feedback examples. Those are development acceptance checks, NOT independent validation.',
              'A rejected small loss is not evidence that no incident occurred; original CSV labels are preserved.', '',
              f'All official runs: {len(runs)}. CSV eligible positives: {len(labels)}; excluded rows: {len(excluded)}.',
              f'Old: {len(old_hits)} alarms, {len(old_hits & labels.keys())}/86 labelled runs.',
              f'New: {len(new_hits)} alarms, {len(new_hits & labels.keys())}/86 labelled runs.', '',
              f'All-run changes: {len(new_hits-old_hits)} added, {len(old_hits-new_hits)} removed.', '',
              '## Top 19 — every flagged run', '',
              '| Rider | Venue | Session | Old conservative | New conservative |',
              '|---|---|---|---|---|']
    top_keys = set()
    for name in TOP19:
        ids = [i for i, n in names.items() if audit.norm(n) == audit.norm(name)]
        assert len(ids) == 1, (name, ids)
        keys = [k for k in runs if k[1] == ids[0] and sessions[k[0]]['categories']['code'] == 'EM']
        top_keys.update(keys)
        flagged = sorted((k for k in keys if old[k] or new[k]), key=lambda k: (runs[k]['SessionDate'], k[0]))
        if not flagged:
            report.append(f'| {name} | — | — | — | — |')
        for k in flagged:
            venue = sessions[k[0]]['rounds']['tracks']['venue']
            report.append(f'| {name} | {venue} | {SESSION_NAMES[runs[k]["SessionType"]]} | {fmt(old[k])} | {fmt(new[k])} |')
    report += ['', f'Top 19 runs: {len(top_keys & old_hits)} → {len(top_keys & new_hits)}.',
               f'Top 19 sectors: {sum(len(old[k]) for k in top_keys)} → {sum(len(new[k]) for k in top_keys)}.', '',
               '## CSV gains and losses', '']
    for title, keys in [('Added', (new_hits-old_hits) & labels.keys()), ('Removed', (old_hits-new_hits) & labels.keys())]:
        for k in sorted(keys):
            row = labels[k]
            report.append(f"- {title}: {row['incident_record_id']} {row['rider_name']}, {row['venue']}, {row['session']}: {fmt(old[k])} → {fmt(new[k])}")
    output = ROOT/'artifacts/incident-conservative-refinement'
    output.mkdir(parents=True, exist_ok=True)
    (output/'report.md').write_text('\n'.join(report)+'\n', encoding='utf-8')
    with (output/'all-changes.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(('session_id', 'rider_id', 'rider', 'venue', 'category', 'session', 'old', 'new', 'csv_record'))
        for k in sorted(runs):
            if {f['index'] for f in old[k]} == {f['index'] for f in new[k]}:
                continue
            meta = sessions[k[0]]
            writer.writerow((k[0], k[1], names[k[1]], meta['rounds']['tracks']['venue'], meta['categories']['code'],
                             SESSION_NAMES[runs[k]['SessionType']], fmt(old[k]), fmt(new[k]), labels.get(k, {}).get('incident_record_id', '')))
    summary = dict(input_sha256=hashlib.sha256((inputs/'inputs-2026.json').read_bytes()).hexdigest(),
                   runs=len(runs), labelled=len(labels), old_alarms=len(old_hits), new_alarms=len(new_hits),
                   old_hits=len(old_hits & labels.keys()), new_hits=len(new_hits & labels.keys()),
                   added_runs=len(new_hits-old_hits), removed_runs=len(old_hits-new_hits),
                   top19_old_runs=len(top_keys & old_hits), top19_new_runs=len(top_keys & new_hits),
                   top19_old_sectors=sum(len(old[k]) for k in top_keys), top19_new_sectors=sum(len(new[k]) for k in top_keys))
    (output/'summary.json').write_text(json.dumps(summary, indent=2)+'\n', encoding='utf-8')
    print('\n'.join(report))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
