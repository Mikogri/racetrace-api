"""Offline research spike, not imported by the API. Python standard library only.

Positive-only labels: unlabelled runs are UNKNOWN, never negative examples.
Candidate selection uses labelled recall subject to an explicit alarm budget;
leave-one-event-out selection checks threshold transfer, not unbiased accuracy.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics as st
import unicodedata
from collections import Counter, defaultdict
from dataclasses import dataclass, asdict
from pathlib import Path


def norm(value):
    return ''.join(c.upper() for c in unicodedata.normalize('NFD', value or '')
                   if c.isalnum() and not unicodedata.combining(c))


def rider_key(value):
    aliases = {'VALIHOLL': 'VALENTINAHOLL', 'ANGELSUAREZ': 'ANGELSUAREZALONSO',
               'RICHIERUDE': 'RICHARDRUDEJR', 'HATTIEHARNDEN': 'HARRIETHARNDEN',
               'ROSAMARIEJENSEN': 'ROSAJENSEN'}
    return aliases.get(norm(value), norm(value))


def class_key(value):
    value = norm(value)
    if value in ('WORLDCUP', 'WC'):
        return 'WC'
    if value in ('WORLDCHAMPIONSHIP', 'WORLDCHAMPIONSHIPS', 'WORLDCHAMPS', 'WCH'):
        return 'WCH'
    return value


def valid(t):
    return t is not None and math.isfinite(t) and t > 0


def median(values):
    return st.median(values)


def quantile(values, fraction):
    values = sorted(values)
    x = (len(values) - 1) * fraction
    a, b = math.floor(x), math.ceil(x)
    return values[a] + (values[b] - values[a]) * (x - a)


@dataclass(frozen=True)
class Candidate:
    family: str
    seconds: float
    z: float
    support: str
    rank_bonus: float = 0

    @property
    def name(self):
        return f'{self.family};loss={self.seconds:g};z={self.z:g};{self.support};rank={self.rank_bonus:g}'


def features(run, family, invalid=()):
    sectors = run['Sectors']
    usable = [i for i, s in enumerate(sectors) if i not in invalid and valid(s['TimeSeconds'])
              and valid(s['Statistics']['Median']) and s['Statistics']['Count'] >= 3]
    ratios = {i: math.log(sectors[i]['TimeSeconds'] / sectors[i]['Statistics']['Median']) for i in usable}
    result = []
    for i in usable:
        other = [j for j in usable if j != i]
        if len(other) < 2:
            continue
        references = [ratios[j] for j in other]
        if family == 'local':
            nearby = sorted(other, key=lambda j: (abs(j-i), j))[:3]
            references = [ratios[j] for j in nearby]
        baseline = quantile(references, .35) if family == 'lower-profile' else median(references)
        spread = 1.4826 * median([abs(x-baseline) for x in references])
        sector = sectors[i]
        stats = sector['Statistics']
        expected = stats['Median'] * math.exp(baseline)
        loss = sector['TimeSeconds'] - expected
        if family in ('seconds', 'self-seconds'):
            field_scale = stats['LinearMadScale'] * math.exp(baseline) if family == 'seconds' else 0
            scale_seconds = max(.4, field_scale, expected * spread)
            z = loss / scale_seconds
            scale = scale_seconds / expected
        else:
            scale = max(.02, 0 if family == 'self-profile' else stats['LogMadScale'], spread)
            z = (ratios[i]-baseline) / scale
        ranked = [(sectors[j]['Rank']-1) / max(1, sectors[j]['Statistics']['Count']-1)
                  for j in other if sectors[j]['Rank'] is not None]
        rank_drop = ((sector['Rank']-1) / max(1, stats['Count']-1) - median(ranked)
                     if ranked and sector['Rank'] is not None else 0)
        clean = [j for j in other if abs(ratios[j]-baseline) <= 1.5 * scale]
        # Returning to baseline is timing evidence, independent of finishing rank.
        later = [j for j in usable if j > i]
        recovery = any(j in clean for j in later)
        previous = [j for j in usable if j < i]
        bracketed = (not previous or previous[-1] in clean) and (not later or later[0] in clean)
        result.append(dict(index=i, expected=expected, loss=loss, z=z, rank_drop=rank_drop,
                           recovery=recovery, terminal=not later, bracketed=bracketed,
                           clean=len(clean), observed=len(usable)))
    return result


def detect(fs, candidate):
    flagged = []
    for f in fs:
        support = (candidate.support == 'any' or
                   candidate.support == 'recovery' and (f['recovery'] or f['terminal']) or
                   candidate.support == 'bracketed' and f['bracketed'])
        score = f['z'] + candidate.rank_bonus * max(0, f['rank_drop'])
        if (f['loss'] >= candidate.seconds and f['z'] >= 1.5 and score >= candidate.z
                and f['clean'] >= 2 and support):
            flagged.append(f)
    return flagged


def cohort_features(runs, profile_cache, neighbours):
    """Leave-sector-out peer profiles. No labels or final rank enter prediction."""
    sessions = defaultdict(list)
    ratios = {}
    for key, run in sorted(runs.items()):
        # Reuse the shared timing-validity guard, including invalid split pairs.
        usable = {f['index'] for f in profile_cache[key]}
        ratios[key] = {i: math.log(s['TimeSeconds'] / s['Statistics']['Median'])
                       for i, s in enumerate(run['Sectors']) if i in usable}
        sessions[key[0]].append(key)
    output = {}
    for key, run in runs.items():
        output[key] = []
        own = ratios[key]
        for original in profile_cache[key]:
            i = original['index']
            peers = []
            for peer_key in sessions[key[0]]:
                peer = ratios[peer_key]
                shared = sorted((own.keys() & peer.keys()) - {i})
                if peer_key == key or i not in peer or len(shared) < 2:
                    continue
                distance = median([abs(own[j]-peer[j]) for j in shared])
                # Transfer only the peer's sector-specific deviation, not pace.
                prediction = median([own[j] for j in shared]) + peer[i] - median([peer[j] for j in shared])
                peers.append((distance, peer_key, prediction))
            nearest = sorted(peers)[:neighbours]
            if len(nearest) < 3:
                output[key].append(original)
                continue
            predictions = [p[2] for p in nearest]
            centre = median(predictions)
            scale = max(.02, 1.4826 * median([abs(p-centre) for p in predictions]))
            expected = run['Sectors'][i]['Statistics']['Median'] * math.exp(centre)
            output[key].append(dict(original, expected=expected,
                                    loss=run['Sectors'][i]['TimeSeconds']-expected,
                                    z=(own[i]-centre)/scale))
    return output


def rank_conditioned_features(runs, profile_cache):
    """Predict a sector time at the rider's usual within-field percentile.

    Rank establishes the expected performance band; detection still requires
    excess seconds and time deviation. Tight fields cannot trigger on rank alone.
    """
    fields = defaultdict(list)
    for key, run in runs.items():
        for f in profile_cache[key]:
            fields[(key[0], f['index'])].append((key, run['Sectors'][f['index']]['TimeSeconds']))
    output = {}
    for key, run in runs.items():
        fs = profile_cache[key]
        sectors = run['Sectors']
        ranks = {f['index']: (sectors[f['index']]['Rank']-1) / max(1, sectors[f['index']]['Statistics']['Count']-1)
                 for f in fs if sectors[f['index']]['Rank'] is not None}
        output[key] = []
        for f in fs:
            i = f['index']
            other = {j: r for j, r in ranks.items() if j != i}
            times = sorted(t for peer, t in fields[(key[0], i)] if peer != key)
            if len(other) < 2 or len(times) < 5:
                output[key].append(f)
                continue
            centre = median(list(other.values()))
            spread = max(.05, 1.4826*median([abs(r-centre) for r in other.values()]))
            expected = quantile(times, min(1, max(0, centre)))
            lower = quantile(times, max(0, centre-spread))
            upper = quantile(times, min(1, centre+spread))
            scale = max(.4, .02*expected, (upper-lower)/2)
            clean = [j for j, r in other.items() if abs(r-centre) <= 1.5*spread]
            later, previous = sorted(j for j in other if j > i), sorted(j for j in other if j < i)
            loss = sectors[i]['TimeSeconds']-expected
            output[key].append(dict(f, expected=expected, loss=loss, z=loss/scale,
                                    clean=len(clean), recovery=any(j in clean for j in later),
                                    bracketed=(not previous or previous[-1] in clean) and (not later or later[0] in clean)))
    return output


def match_labels(rows, runs, sessions, names):
    labels, excluded = {}, []
    session_types = {'FINAL': (0,), 'Q1': (2,), 'Q2': (3,), 'QUALIFYING': (2, 4)}
    for row in rows:
        if row['session'] not in session_types:
            excluded.append((row, 'training/unspecified'))
            continue
        matches = []
        for key, run in runs.items():
            session = sessions[key[0]]
            r = session['rounds']
            if (run['SessionType'] in session_types[row['session']]
                    and str(r['season_year']) == row['season']
                    and norm(row['venue']) in norm(r['tracks']['venue'])
                    and session['categories']['code'] == row['category']
                    and class_key(r['competition_levels']['code']) == class_key(row['competition_class'])
                    and rider_key(names[key[1]]) == rider_key(row['rider_name'])):
                matches.append(key)
        if len(matches) != 1:
            excluded.append((row, f'matches={len(matches)}'))
        elif (runs[matches[0]]['Run'].get('Status') or '').upper() in ('DNS', 'DSQ'):
            excluded.append((row, 'DNS/DSQ'))
        else:
            labels[matches[0]] = row
    return labels, excluded


def choose(results, train_keys, positive_keys, budget):
    train_pos = positive_keys & train_keys
    viable = [(candidate, hits) for candidate, hits in results
              if len(hits & train_keys) <= budget * len(train_keys)]
    if not viable:
        # Abstain when no tested threshold meets the review budget.
        return Candidate(results[0][0].family, 0, 0, 'disabled'), set()
    # Match positives subject to a fixed review burden; break ties by fewer alarms,
    # then prefer recovery and higher thresholds. No hidden ground-truth negatives.
    return max(viable, key=lambda p: (len(p[1] & train_pos), -len(p[1] & train_keys),
                                     p[0].support != 'any', p[0].z, p[0].seconds))


def self_check():
    def run(times):
        return {'Sectors': [{'TimeSeconds': t, 'Rank': 20,
                            'Statistics': {'Median': 50, 'Count': 30, 'LogMadScale': .01, 'LinearMadScale': .5}}
                           for t in times]}
    c = Candidate('profile', 1.5, 3, 'recovery', 1)
    assert not detect(features(run([50]*5), c.family), c)
    assert not detect(features(run([80]*5), c.family), c)
    assert not detect(features(run([50, 50.5, 50, 50, 50]), c.family), c)
    assert [f['index'] for f in detect(features(run([50, 55, 50, 50, 50]), c.family), c)] == [1]
    assert not features(run([None, None, 50, None, None]), c.family)
    # A position collapse alone is insufficient; seconds and timing evidence
    # must pass independently of the rank bonus.
    fs = features(run([50, 50.5, 50, 50, 50]), c.family)
    assert not detect([dict(f, rank_drop=1) for f in fs], c)
    assert not detect(features(run([50, 55, 50, 50, 50]), c.family),
                      Candidate('profile', 0, 0, 'disabled'))
    assert choose([(c, {1, 2})], {1, 2}, {1}, .1)[1] == set()
    # Peer/rank models must not manufacture anomalies in a stable field.
    steady = {('test', str(i)): run([50+i]*5) for i in range(8)}
    for i, r in enumerate(steady.values()):
        for s in r['Sectors']:
            s['Rank'] = i+1
            s['Statistics']['Count'] = 8
    profile = {key: features(r, 'profile') for key, r in steady.items()}
    for predictions in (cohort_features(steady, profile, 7), rank_conditioned_features(steady, profile)):
        assert not any(detect(fs, c) for fs in predictions.values())
    safe = dict(run([50, 55, 50, 50, 50]), Run={'IsClassified': True, 'FinishTimeSeconds': 255})
    flags = detect(features(safe, c.family), c)
    assert correction_preview(safe, flags, [1])[0] is None
    assert correction_preview(dict(safe, Run={'IsClassified': False, 'FinishTimeSeconds': None}), flags, [])[0] is None
    assert correction_preview(safe, flags, [])[0] == 250
    assert correction_preview(safe, [dict(flags[0], recovery=False, terminal=False)], [])[0] is None


def correction_preview(run, flags, invalid):
    """Research estimate only. DNF/invalid or persistent losses are not repaired."""
    times = [s['TimeSeconds'] for s in run['Sectors']]
    finish = run['Run']['FinishTimeSeconds']
    clean_required = max(2, math.ceil(len(times)/2))
    if not flags:
        return None, 'no anomaly'
    if not run['Run']['IsClassified'] or not valid(finish):
        return None, 'unclassified/no finish'
    if invalid or not all(valid(t) for t in times):
        return None, 'invalid/missing timing'
    if abs(sum(times)-finish) > .01:
        return None, 'sector/finish mismatch'
    if len(times)-len(flags) < clean_required:
        return None, 'insufficient clean sectors'
    if any(not (f['recovery'] or f['terminal']) for f in flags):
        return None, 'no demonstrated recovery'
    return finish-sum(f['loss'] for f in flags), 'eligible research estimate'


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--labels', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    self_check()
    data = json.loads(args.input.read_text(encoding='utf-8-sig'))
    baseline = {(r['SessionId'], r['RiderId']): r['Result'] for r in json.loads(args.baseline.read_text(encoding='utf-8-sig'))}
    sessions = {str(s['id']): s for s in data['Data']['Sessions']}
    names = {str(r['rider_id']): r['riders']['first_name']+' '+r['riders']['last_name'] for r in data['Data']['Results']}
    all_runs = {(s['SessionId'], s['Run']['RiderId']): s for s in data['PaceSessions']}
    with args.labels.open(encoding='utf-8-sig', newline='') as file:
        labels, excluded = match_labels(list(csv.DictReader(file)), all_runs, sessions, names)
    runs = {key: run for key, run in all_runs.items() if key in baseline}
    keys, positives = set(runs), set(labels)
    assert positives <= keys
    groups = defaultdict(set)
    for key in keys:
        # Same venue's categories and qualifying/final sessions stay in one fold.
        groups[str(sessions[key[0]]['round_id'])].add(key)
    families = ('profile', 'seconds', 'local', 'lower-profile', 'self-profile', 'self-seconds')
    invalid = {key: [s['SectorIndex'] for s in score['Sectors'] if s['Anomaly']['Kind'] == 3]
               for key, score in baseline.items()}
    cache = {family: {key: features(run, family, invalid[key])
                      for key, run in runs.items()} for family in families}
    for count in (7, 15):
        cache[f'cohort{count}'] = cohort_features(runs, cache['profile'], count)
    cache['rank-conditioned'] = rank_conditioned_features(runs, cache['profile'])
    families += ('cohort7', 'cohort15', 'rank-conditioned')
    candidates = [Candidate(family, seconds, z, support, bonus)
                  for family in families for seconds in (1, 1.5, 2, 3)
                  for z in (2, 2.5, 3, 3.5, 4, 5, 6, 8)
                  for support in ('recovery', 'bracketed', 'any') for bonus in (0, 1, 2)]
    results = [(c, {key for key, fs in cache[c.family].items() if detect(fs, c)}) for c in candidates]
    args.output.mkdir(parents=True, exist_ok=True)
    old_hits = {key for key, score in baseline.items() if score['IncidentSectorIndex'] is not None}
    report = ['# Incident detector experiment — 2026', '',
              'Offline research only: no production calculator, database or API changes.', '',
              f'Inputs: {len(keys)} official runs, {len(groups)} events, {len(positives)} positive labelled runs; {len(excluded)} excluded CSV rows.',
              'The CSV is incomplete and positive-only. Recall is measurable; precision/false-positive rate are NOT. Unlabelled alarms are a review workload, not false positives.',
              'Fixed alarm budgets prevent a trivial flag-everything winner. Leave-one-event-out selects parameters using only the other events. All events informed prior model development: this is a transfer stress test, not an untouched external test.', '',
              '| Model / max alarm rate | Labelled hits | All alarms | Unlabelled alarms | Leave-one-event-out hits | Held-out alarms |',
              '|---|---:|---:|---:|---:|---:|',
              f'| Current C# detector | {len(old_hits & positives)}/{len(positives)} | {len(old_hits)} | {len(old_hits-positives)} | — | — |']
    summary = []
    for budget in (.05, .10, len(old_hits)/len(keys), .125, .15, .20):
        for family in families:
            family_results = [(c, hits) for c, hits in results if c.family == family]
            best, hits = choose(family_results, keys, positives, budget)
            oof_hits, folds = set(), []
            for group, held in sorted(groups.items()):
                selected, predicted = choose(family_results, keys-held, positives-held, budget)
                oof_hits |= predicted & held
                folds.append(dict(event=group, candidate=asdict(selected),
                                  positives=len(held & positives), hits=len(predicted & held & positives),
                                  alarms=len(predicted & held), runs=len(held)))
            summary.append(dict(budget=budget, candidate=asdict(best), hits=len(hits & positives), alarms=len(hits),
                                oof_hits=len(oof_hits & positives), oof_alarms=len(oof_hits), folds=folds))
            report.append(f'| {family} / {budget:.2%} | {len(hits & positives)}/{len(positives)} | {len(hits)} | {len(hits-positives)} | {len(oof_hits & positives)}/{len(positives)} | {len(oof_hits)} |')
    # Explicit starting policy: review <=12.5% of runs, favour transfer then recall.
    shortlist = [s for s in summary if s['budget'] == .125]
    winner = max(shortlist, key=lambda s: (s['oof_hits'], s['hits'], -s['alarms']))
    chosen = Candidate(**winner['candidate'])
    chosen_flags = {key: detect(fs, chosen) for key, fs in cache[chosen.family].items()}
    selected_hits = {key for key, flags in chosen_flags.items() if flags}
    previews = {key: correction_preview(run, chosen_flags[key], invalid[key]) for key, run in runs.items()}
    eligible = {key for key, preview in previews.items() if preview[0] is not None}
    lost = sorted((old_hits & positives)-selected_hits)
    gained = sorted((selected_hits & positives)-old_hits)
    max_candidate, max_hits = choose(results, keys, positives, 1)
    report += ['', '## Provisional candidate under 12.5% alarm budget', '', chosen.name, '',
               'This policy is explicit, not an estimated acceptable false-positive rate. Higher-recall alternatives above need more manual review.', '',
               f'Labelled gains: {len(gained)}; losses versus current detector: {len(lost)}. Research finish-correction gates accept {len(eligible)} runs, including {len(eligible & positives)} labelled positives.',
               f'Without an alarm budget, grid maximum is {len(max_hits & positives)}/{len(positives)} hits with {len(max_hits)} alarms ({max_candidate.name}). This is NOT the recommended model.',
               'No feasible configuration at a budget is reported as zero alarms (abstention), not a successful detector.', '',
               '| Record | Event / rider | Status | Current | Candidate sectors | Estimated loss | CSV zone | Correction preview |',
               '|---|---|---|---|---|---|---|---|']
    for key, row in labels.items():
        flags = chosen_flags[key]
        corrected, reason = previews[key]
        correction = f'{corrected:.3f} s' if corrected is not None else reason
        report.append(f"| {row['incident_record_id']} | {row['venue']} / {row['rider_name']} | {runs[key]['Run']['Status']} | {'yes' if key in old_hits else 'no'} | {', '.join('S'+str(f['index']+1) for f in flags) or '—'} | {sum(f['loss'] for f in flags):.3f} s | {row['sector_index']} | {correction} |")
    report += ['', 'CSV numbered split zones are not exact sector ground truth. Run-level hits may localize a different sector. DNF has no reconstructed finish; detection and safe correction are separate.', '',
               '## Excluded records', '']
    report.extend(f"- {row['incident_record_id']} {row['rider_name']}: {reason}" for row, reason in excluded)
    outputs = dict(input_sha256=hashlib.sha256(args.input.read_bytes()).hexdigest(),
                   labels_sha256=hashlib.sha256(args.labels.read_bytes()).hexdigest(),
                   baseline_sha256=hashlib.sha256(args.baseline.read_bytes()).hexdigest(),
                   runs=len(keys), positives=len(positives), grid_size=len(candidates),
                   current_hits=len(old_hits & positives), current_alarms=len(old_hits),
                   experiments=summary, provisional=winner,
                   new_labelled=[labels[k]['incident_record_id'] for k in gained],
                   lost_labelled=[labels[k]['incident_record_id'] for k in lost],
                   eligible_corrections=len(eligible), eligible_labelled_corrections=len(eligible & positives),
                   unconstrained=dict(candidate=asdict(max_candidate), hits=len(max_hits & positives), alarms=len(max_hits)))
    (args.output/'summary.json').write_text(json.dumps(outputs, indent=2), encoding='utf-8')
    (args.output/'report.md').write_text('\n'.join(report)+'\n', encoding='utf-8')
    with (args.output/'grid.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(('family', 'minimum_seconds', 'robust_z', 'support', 'rank_bonus', 'labelled_hits', 'all_alarms'))
        for c, hits in results:
            writer.writerow((c.family, c.seconds, c.z, c.support, c.rank_bonus, len(hits & positives), len(hits)))
    with (args.output/'candidates.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(('session', 'rider_id', 'venue', 'category', 'rider', 'known_positive', 'new_alarm', 'sector', 'time', 'expected', 'loss', 'z', 'rank_drop'))
        for key, flags in sorted(chosen_flags.items()):
            for f in flags:
                meta = sessions[key[0]]
                writer.writerow((key[0], key[1], meta['rounds']['tracks']['venue'], meta['categories']['code'], names[key[1]], key in positives,
                                 key not in old_hits, f['index']+1, runs[key]['Sectors'][f['index']]['TimeSeconds'],
                                 f['expected'], f['loss'], f['z'], f['rank_drop']))
    with (args.output/'missed.csv').open('w', encoding='utf-8-sig', newline='') as file:
        writer = csv.writer(file)
        writer.writerow(('record', 'rider', 'venue', 'status', 'sector_seconds', 'sector_ranks', 'closest_sector', 'excess_seconds', 'robust_z', 'rank_drop', 'clean_references', 'recovery', 'source_description'))
        for key, row in labels.items():
            if key in selected_hits:
                continue
            fs = cache[chosen.family][key]
            closest = max(fs, key=lambda f: f['z'] + chosen.rank_bonus*max(0, f['rank_drop'])) if fs else {}
            writer.writerow((row['incident_record_id'], names[key[1]], row['venue'], runs[key]['Run']['Status'],
                             json.dumps([s['TimeSeconds'] for s in runs[key]['Sectors']]),
                             json.dumps([s['Rank'] for s in runs[key]['Sectors']]),
                             closest.get('index', -1)+1, closest.get('loss'), closest.get('z'), closest.get('rank_drop'),
                             closest.get('clean'), closest.get('recovery'), row['summary_pl']))
    print(json.dumps({key: value for key, value in outputs.items() if key not in ('experiments', 'provisional')}, indent=2))
    print('Provisional:', chosen.name, 'hits:', winner['hits'], 'alarms:', winner['alarms'], 'held-out hits:', winner['oof_hits'])


if __name__ == '__main__':
    main()
