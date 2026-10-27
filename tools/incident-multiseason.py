"""Positive-only, offline incident research. Does not change API calculators.

Reuses the existing timing features. Unlabelled runs are unknown, not negatives.
Year folds separate whole events/categories; alarm budgets are review policies,
not precision estimates. Source narratives/URLs are not copied to outputs.
"""
import argparse
import csv
import hashlib
import importlib.util
import itertools
import json
import re
import sys
from collections import Counter
from pathlib import Path

spec = importlib.util.spec_from_file_location('incident_experiments', Path(__file__).with_name('incident-experiments.py'))
audit = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = audit
spec.loader.exec_module(audit)


def session_types(value):
    return {'FINAL': (0,), 'SEMIFINAL': (1,), 'QUALIFYING': (2, 4),
            'QUALI': (2, 4), 'Q1': (2,), 'Q2': (3,), 'SEEDING': (5,)}.get(audit.norm(value))


def parse_markdown(text, year):
    rows, headers, heading = [], None, ''
    categories = {'ELITEMEN': 'EM', 'ELITEWOMEN': 'EW', 'JUNIORMEN': 'JM', 'JUNIORWOMEN': 'JW',
                  'ME': 'EM', 'WE': 'EW', 'MJ': 'JM', 'WJ': 'JW'}
    for line in text.splitlines():
        if line.startswith('#'):
            heading = re.sub(r'^#+\s*', '', line)
        if not line.startswith('|'):
            continue
        cells = [c.strip() for c in line.strip().strip('|').split('|')]
        if 'Zawodnik' in cells:
            headers = cells
            continue
        if not headers or len(cells) != len(headers) or not any(c in ('`CRASH`', '`OFF_COURSE`') for c in cells):
            continue
        row = dict(zip(headers, cells))
        rows.append(dict(record=f'{year}-{len(rows)+1:03}', season=year, event=heading,
                         date=row['Data'], rider=row['Zawodnik'],
                         category=categories.get(audit.norm(row.get('Kategoria', row.get('Kat.', ''))), ''),
                         session=row['Sesja'], session_types=session_types(row['Sesja'])))
    return rows


def unique_positives(rows):
    return {r['key'] for r in rows if r.get('key') is not None}


def passes_development_controls(sectors):
    # Fixed prior user feedback, NOT held-out labels and never a production rule.
    return (3 in sectors[('113', '15')] and 1 in sectors[('123', '15')]
            and 4 not in sectors[('143', '28')])


def event_matches(row, event):
    heading = audit.norm(row['event'])
    venue = audit.norm(event['tracks']['venue'])
    championship = any(word in heading for word in ('WCH', 'MISTRZOSTWASWIATA'))
    if (audit.class_key(event['competition_levels']['code']) == 'WCH') != championship:
        return False
    venues = ('LEOGANG', 'LESGETS', 'MARIBOR', 'VALDISOLE', 'LENZERHEIDE', 'SNOWSHOE', 'LOURDES',
              'FORTWILLIAM', 'PALARINSAL', 'MONTSAINTEANNE', 'LOUDENVIELLE', 'BIELSKO',
              'LATHUILE', 'LAKEPLACID', 'CHAMPERY')
    if not any(v in heading and v in venue for v in venues):
        return False
    # Only 2021 has two rounds at the same venue; do not guess from a fuzzy date.
    if row['season'] == 2021 and 'SNOWSHOE' in heading:
        return event['round_number'] == (5 if '#1' in row['event'] else 6 if '#2' in row['event'] else -1)
    return True


def rider_key(value):
    key = audit.rider_key(value)
    return {'EDMASTERS': 'EDWARDMASTERS', 'JESSICABLEWITT': 'JESSBLEWITT',
            'MIKEHUTER': 'MIKELEONHUTER', 'LOUISEFERGUSON': 'LOUISEANNAFERGUSON',
            'JUANFERNANDOMUNOZ': 'JUANMUNOZ'}.get(key, key)


def load_data(root, documents):
    runs, baseline, sessions, names, years, rows, hashes, excluded_results = {}, {}, {}, {}, {}, [], {}, []
    for year in range(2021, 2027):
        directory = root / str(year)
        path = directory / f'inputs-{year}.json'
        hashes[str(year)] = hashlib.sha256(path.read_bytes()).hexdigest()
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        scores = json.loads((directory / 'baseline.json').read_text(encoding='utf-8-sig'))
        baseline.update({(r['SessionId'], r['RiderId']): r['Result'] for r in scores})
        sessions.update({str(s['id']): s for s in data['Data']['Sessions']})
        names.update({str(r['rider_id']): r['riders']['first_name'] + ' ' + r['riders']['last_name']
                      for r in data['Data']['Results']})
        excluded_results.extend((str(r['session_id']), str(r['rider_id']), year, r['status'])
                                for r in data['Data']['Results'] if (r['status'] or '').upper() in ('DNS', 'DSQ'))
        for run in data['PaceSessions']:
            key = (run['SessionId'], run['Run']['RiderId'])
            if key in baseline:
                assert key not in runs, 'Session identifiers unexpectedly overlap between years'
                runs[key] = run
                years[key] = year
        if year <= 2025:
            source = documents / f'RaceTrace_Incidents_{year}.md'
            hashes[source.name] = hashlib.sha256(source.read_bytes()).hexdigest()
            rows.extend(parse_markdown(source.read_text(encoding='utf-8-sig'), year))
    for row in rows:
        row['key'] = None
        if row['session_types'] is None:
            row['state'] = 'training or ambiguous session'
            continue
        matches = [key for key, run in runs.items()
                   if years[key] == row['season'] and run['SessionType'] in row['session_types']
                   and rider_key(names[key[1]]) == rider_key(row['rider'])
                   and sessions[key[0]]['categories']['code'] == row['category']
                   and event_matches(row, sessions[key[0]]['rounds'])]
        if len(matches) != 1:
            row['state'] = f'matched {len(matches)} scored runs'
            for sid, rid, year, status in excluded_results:
                meta = sessions[sid]
                stype = {'FINAL': 0, 'SEMI': 1, 'Q1': 2, 'Q2': 3, 'QUALI': 4, 'SEEDING': 5}.get(meta['session_type'])
                if (year == row['season'] and stype in row['session_types']
                        and rider_key(names[rid]) == rider_key(row['rider'])
                        and meta['categories']['code'] == row['category'] and event_matches(row, meta['rounds'])):
                    row['state'] = f'{status}: excluded from Pace scoring'
        else:
            row['key'] = matches[0]
            row['state'] = 'matched'
    return runs, baseline, sessions, names, years, rows, hashes


def select(results, keys, positives, budget):
    viable = [(name, hits) for name, hits in results if len(hits & keys) <= budget * len(keys)]
    # Labelled coverage first, review workload second, deterministic final tie.
    return max(viable, key=lambda p: (len(p[1] & keys & positives), -len(p[1] & keys), p[0]))


def write_csv(path, headers, rows):
    with path.open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(headers)
        writer.writerows(rows)


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--match-only', action='store_true')
    parser.add_argument('--guarded-local', action='store_true',
                        help='Restrict local candidates using the three fixed 2026 development controls')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    runs, scores, sessions, names, years, rows, hashes = load_data(args.input, args.documents)
    write_csv(args.output/'matching.csv', ('record', 'season', 'event', 'rider', 'category', 'session', 'state', 'session_id', 'rider_id'),
              [(r['record'], r['season'], r['event'], r['rider'], r['category'], r['session'], r['state'],
                *(r['key'] or ('', ''))) for r in rows])
    print('Matching:', dict(Counter(r['state'] for r in rows)), flush=True)
    if args.match_only:
        for r in rows:
            if r['state'].startswith('matched '):
                print(r['record'], r['event'], r['rider'], r['session'], r['state'])
        return
    positives = unique_positives(rows)
    keys = {k for k in runs if years[k] <= 2025}
    baseline_hits = {k for k, s in scores.items()
                     if any(v['Anomaly']['Kind'] == 2 for v in s['Sectors'])}
    # Retain the production detector's independent, label-free small-loss branch.
    # This tests changing the large-loss rule without discarding top-pace evidence.
    isolated_sectors = {k: {v['SectorIndex'] for v in s['Sectors'] if
                        v['Anomaly']['Kind'] == 2 and v['Anomaly']['Reason'].startswith('Isolated top-pace')}
                        for k, s in scores.items()}
    isolated_top = {k for k, indices in isolated_sectors.items() if indices}
    invalid = {k: [s['SectorIndex'] for s in score['Sectors'] if s['Anomaly']['Kind'] == 3]
               for k, score in scores.items()}
    # Original conservative controls are retained as a development check only.
    families = ('local', 'profile', 'seconds', 'self-profile', 'lower-profile', 'rank-conditioned', 'cohort7', 'cohort15')
    if args.guarded_local:
        families = ('local',)
    cache, results, parameters = {}, [('abstain', set()), ('production', baseline_hits)], {}
    feature_signature = dict(hashes)
    feature_signature['feature_code'] = hashlib.sha256(Path(audit.__file__).read_bytes()).hexdigest()
    for year in range(2021, 2027):
        feature_signature[f'baseline-{year}'] = hashlib.sha256((args.input/str(year)/'baseline.json').read_bytes()).hexdigest()
    for family in families:
        feature_path = args.output / f'features-{family}.json'
        saved = json.loads(feature_path.read_text(encoding='utf-8')) if feature_path.exists() else None
        if saved and saved['hashes'] == feature_signature:
            cache[family] = {tuple(k): fs for k, fs in saved['features']}
        elif family.startswith('cohort'):
            cache[family] = audit.cohort_features(runs, cache['profile'], int(family[6:]))
        elif family == 'rank-conditioned':
            cache[family] = audit.rank_conditioned_features(runs, cache['profile'])
        else:
            cache[family] = {k: audit.features(run, family, invalid[k]) for k, run in runs.items()}
        if not saved or saved['hashes'] != feature_signature:
            feature_path.write_text(json.dumps(dict(hashes=feature_signature, features=list(cache[family].items()))), encoding='utf-8')
        for loss, z, support, bonus in itertools.product((2, 3, 3.5, 4, 4.5, 5, 6),
                                                        (2.5, 3, 3.5, 3.75, 4, 4.25, 4.5, 5, 6),
                                                        ('any', 'recovery'), (0, 1)):
            candidate = audit.Candidate(family, loss, z, support, bonus)
            hits = {k for k, fs in cache[family].items() if audit.detect(fs, candidate)}
            parameters[candidate.name] = candidate
            for retain in (False, True):
                if args.guarded_local:
                    controls = {k: {f['index'] for f in audit.detect(cache[family][k], candidate)} |
                                (isolated_sectors[k] if retain else set())
                                for k in (('113', '15'), ('123', '15'), ('143', '28'))}
                    if not passes_development_controls(controls):
                        continue
                results.append((candidate.name+(';retain-top' if retain else ''), hits | isolated_top if retain else hits))
        print(f'{family}: {len(cache[family])} runs, {len(results)-2} candidates evaluated', flush=True)
    budget = len(baseline_hits & keys) / len(keys)
    selected_name, selected_hits = select(results, keys, positives, budget)
    oof, folds = set(), []
    for year in range(2021, 2026):
        held = {k for k in keys if years[k] == year}
        train = keys-held
        train_budget = len(baseline_hits & train)/len(train)
        name, hits = select(results, train, positives, train_budget)
        oof |= hits & held
        folds.append(dict(year=year, model=name, hits=len(hits & held & positives),
                          positives=len(held & positives), alarms=len(hits & held), runs=len(held)))
    # Chronological test: candidate/threshold selection sees 2021-2024 only.
    train = {k for k in keys if years[k] <= 2024}
    held = keys-train
    temporal_name, temporal_hits = select(results, train, positives, len(baseline_hits & train)/len(train))
    best_by_budget = []
    for limit in (.05, budget, .10, .125, .15):
        name, hits = select(results, keys, positives, limit)
        best_by_budget.append(dict(budget=limit, model=name, hits=len(hits & positives), alarms=len(hits & keys),
                                   gained=len((hits-baseline_hits) & positives), lost=len((baseline_hits-hits) & positives)))
    model_hits = {name: hits for name, hits in results}
    transfer = []
    for limit in (.10, .125):
        fold_hits, fold_alarms = set(), set()
        for year in range(2021, 2026):
            test = {k for k in keys if years[k] == year}
            _, predictions = select(results, keys-test, positives, limit)
            fold_hits |= predictions & test & positives
            fold_alarms |= predictions & test
        name, predictions = select(results, train, positives, limit)
        transfer.append(dict(budget=limit, oof_hits=len(fold_hits), oof_alarms=len(fold_alarms),
                             temporal_model=name, temporal_hits=len(predictions & held & positives),
                             temporal_alarms=len(predictions & held)))
    shortlist = ['production', selected_name, temporal_name]
    for family in families:
        family_results = [r for r in results if r[0] == 'abstain' or r[0].startswith(family+';')]
        name, _ = select(family_results, keys, positives, budget)
        shortlist.append(name)
    shortlist = list(dict.fromkeys(shortlist))
    summary = dict(input_hashes=feature_signature, guarded_local=args.guarded_local,
                   rows=len(rows), positives=len(positives), runs=len(keys),
                   baseline_alarms=len(baseline_hits & keys), baseline_hits=len(baseline_hits & positives),
                   selected=selected_name, selected_hits=len(selected_hits & positives), selected_alarms=len(selected_hits & keys),
                   folds=folds, oof_hits=len(oof & positives), oof_alarms=len(oof),
                   temporal=dict(model=temporal_name, hits=len(temporal_hits & held & positives),
                                 positives=len(held & positives), alarms=len(temporal_hits & held),
                                 baseline_hits=len(baseline_hits & held & positives), baseline_alarms=len(baseline_hits & held)),
                   budgets=best_by_budget, sensitivity_transfer=transfer)
    (args.output/'summary.json').write_text(json.dumps(summary, indent=2)+'\n', encoding='utf-8')
    report = ['# Multi-season incident detector experiment', '',
              'Offline research only; production calculators, API, database and frontend are unchanged.', '',
              f'{len(rows)} source observations; {len(positives)} distinct matched positive runs; {len(keys)} scored runs (2021–2025).',
              'Source reports are supplied labels, not independently reverified. Training/ambiguous sessions are excluded; duplicate observations count once.',
              'A hit means at least one flagged sector in the labelled run, NOT a verified correct incident location.',
              'Unlabelled alarms are UNKNOWN: precision, false-positive rate and crash probabilities cannot be measured.',
              'The selection budget equals the existing detector\'s all-run alarm rate. It is a review-load policy, not proof of safety.',
              ('Fixed 2026 feedback controls constrain candidate eligibility; 2026 is not part of the coverage score.' if args.guarded_local else
               '2026 is development-only (the old detector was tuned there). It is not part of selection or the 2021–2025 score.'), '',
              '| Model | Labelled hits | All alarms | Alarm rate |', '|---|---:|---:|---:|']
    for name in shortlist:
        hits = model_hits[name]
        report.append(f'| {name} | {len(hits & positives)}/{len(positives)} | {len(hits & keys)} | {len(hits & keys)/len(keys):.2%} |')
    report += ['', '## Selected candidate by season', '',
               '| Year | Source rows | Matched distinct runs | Current hits | Candidate hits | Current alarms | Candidate alarms |',
               '|---|---:|---:|---:|---:|---:|---:|']
    for year in range(2021, 2026):
        cohort = {k for k in keys if years[k] == year}
        report.append(f'| {year} | {sum(r["season"]==year for r in rows)} | {len(cohort & positives)} | {len(cohort & positives & baseline_hits)} | {len(cohort & positives & selected_hits)} | {len(cohort & baseline_hits)} | {len(cohort & selected_hits)} |')
    report += ['', '## Whole-year holdouts', '',
               'Each fold selects both model family and thresholds using only the other four years. All categories/sessions of an event stay together; the same rider may appear in different years.',
               f'Out-of-fold: {len(oof & positives)}/{len(positives)} hits, {len(oof)} alarms. These are different fold-specific models, not one deployed algorithm.',
               'Alarm budget constrains training only; unseen-year alarm rates may differ.', '',
               '| Held-out year | Selected on other years | Hits | Alarms |', '|---|---|---:|---:|']
    report += [f'| {f["year"]} | {f["model"]} | {f["hits"]}/{f["positives"]} | {f["alarms"]}/{f["runs"]} |' for f in folds]
    t = summary['temporal']
    report += ['', '## Chronological test', '', f'Selected on 2021–2024: `{temporal_name}`.',
               f'2025: current {t["baseline_hits"]}/{t["positives"]} hits with {t["baseline_alarms"]} alarms; candidate {t["hits"]}/{t["positives"]} with {t["alarms"]} alarms.', '',
               '## Sensitivity / review load', '', '| Max alarm rate | Model | Hits | Alarms |', '|---|---|---:|---:|']
    report += [f'| {r["budget"]:.2%} | {r["model"]} | {r["hits"]}/{len(positives)} | {r["alarms"]} |' for r in best_by_budget]
    report += ['', '## Sensitivity transfer checks', '',
               '| Training alarm budget | Whole-year held-out hits | Held-out alarms | 2025 hits after 2021–2024 selection | 2025 alarms |',
               '|---|---:|---:|---:|---:|']
    report += [f'| {r["budget"]:.1%} | {r["oof_hits"]}/{len(positives)} | {r["oof_alarms"]} | {r["temporal_hits"]}/{len(held & positives)} | {r["temporal_alarms"]} |' for r in transfer]
    report += ['', '## Limits', '',
               'No name, incident label or finish rank is used by a candidate detector. Names/aliases are used only for source matching.',
               'Timing-invalid sectors are shared with the existing C# cleaning rules. DNS/DSQ are not scored; DNF without sufficient measured timing cannot be reconstructed.',
               'Do not equate a flagged sector with an applied finish-time correction. Promotion requires C# parity, correction/ranking impact checks and review of unlabelled alarms.',
               'The best full-data result is fitted performance. The chronological and whole-year results are the transfer checks; inspecting them during further tuning makes them development data.', '']
    (args.output/'report.md').write_text('\n'.join(report), encoding='utf-8')
    sensitive_name = next(r['model'] for r in best_by_budget if r['budget'] == .10)
    sensitive_hits = model_hits[sensitive_name]
    write_csv(args.output/'label-results.csv', ('record','year','event','rider','category','session','state','session_id','rider_id','current','same_budget_candidate','10_percent_candidate'),
              [(r['record'],r['season'],r['event'],r['rider'],r['category'],r['session'],r['state'],
                *(r['key'] or ('','')),r['key'] in baseline_hits,r['key'] in selected_hits,r['key'] in sensitive_hits) for r in rows])
    candidate_name = sensitive_name.removesuffix(';retain-top')
    if candidate_name in parameters:
        c = parameters[candidate_name]
        flags = {k: audit.detect(cache[c.family][k], c) for k in sensitive_hits}
        if sensitive_name.endswith(';retain-top'):
            for k in sensitive_hits & isolated_top:
                seen = {f['index'] for f in flags[k]}
                flags[k] += [dict(index=s['SectorIndex'], loss=s['Anomaly']['LossSeconds'],
                                  z=s['Anomaly']['RobustDeviation'], recovery=s['Anomaly']['AllowsFinishCorrection'], terminal=False)
                             for s in scores[k]['Sectors'] if s['SectorIndex'] not in seen
                             and s['Anomaly']['Kind'] == 2 and s['Anomaly']['Reason'].startswith('Isolated top-pace')]
        write_csv(args.output/'review-10-percent.csv', ('year','session_id','rider_id','rider','venue','category','known_positive','current_alarm','sector','loss_seconds','z','eligible_research_finish_reconstruction'),
                  [(years[k],k[0],k[1],names[k[1]],sessions[k[0]]['rounds']['tracks']['venue'],
                    sessions[k[0]]['categories']['code'],k in positives,k in baseline_hits,f['index']+1,f['loss'],f['z'],
                    audit.correction_preview(runs[k],flags[k],invalid[k])[0] is not None)
                   for k in sorted(sensitive_hits) for f in flags[k]])
    print(json.dumps({k:v for k,v in summary.items() if k != 'input_hashes'}, indent=2), flush=True)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
