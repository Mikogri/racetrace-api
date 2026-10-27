"""Same-course, same-rider earlier-session references; no labels or later sessions."""
import json
from collections import defaultdict
import numpy as np


def earlier_session(previous_date, current_date):
    return bool(previous_date and current_date and previous_date < current_date)


def paired_reference(previous, current, row):
    pvalid = np.isfinite(previous) & (previous > 0)
    cvalid = np.isfinite(current) & (current > 0)
    scales, counts = np.full(5,np.nan), np.zeros(5,int)
    for j in range(5):
        peers = pvalid[:,j] & cvalid[:,j] & (np.arange(len(current)) != row)
        counts[j] = int(peers.sum())
        if peers.any():
            scales[j] = np.median(current[peers,j]/previous[peers,j])
    raw = previous[row]*scales
    usable = pvalid[row] & np.isfinite(raw) & (raw > 0)
    expected = np.full(5,np.nan)
    for j in np.flatnonzero(usable):
        other = usable & cvalid[row] & (np.arange(5) != j)
        adjustment = np.median(np.log(current[row,other]/raw[other])) if other.any() else 0.
        expected[j] = raw[j]*np.exp(adjustment)
    return expected, counts


def change_profile(previous, current, row):
    valid = np.isfinite(previous) & (previous > 0) & np.isfinite(current) & (current > 0)
    ratios = np.full_like(previous,np.nan)
    ratios[valid] = np.log(current[valid]/previous[valid])
    changes = np.full_like(previous,np.nan)
    rank_changes = np.full_like(previous,np.nan)
    rank_fraction = np.full_like(previous,np.nan)
    for j in range(5):
        cohort = np.flatnonzero(valid[:,j])
        if not len(cohort):
            continue
        before = previous[cohort,j]
        after = current[cohort,j]
        movement = (np.searchsorted(np.sort(after),after,side='left')-
                    np.searchsorted(np.sort(before),before,side='left'))
        rank_changes[cohort,j] = movement
        rank_fraction[cohort,j] = movement/max(1,len(cohort)-1)
        for i in cohort:
            other = (np.arange(5) != j) & valid[i]
            anchor = np.median(ratios[i,other]) if other.any() else 0.
            changes[i,j] = ratios[i,j]-anchor
    result = np.full((5,8),np.nan)
    for j in np.flatnonzero(valid[row]):
        peer_mask = np.isfinite(changes[:,j]) & (np.arange(len(previous)) != row)
        peers = changes[peer_mask,j]
        if not len(peers):
            continue
        center = float(np.median(peers))
        scale = max(.004,float(1.4826*np.median(np.abs(peers-center))))
        excess = changes[row,j]-center
        other = (np.arange(5) != j) & np.isfinite(rank_changes[row])
        baseline_rank = np.median(rank_changes[row,other]) if other.any() else 0.
        baseline_fraction = np.median(rank_fraction[row,other]) if other.any() else 0.
        result[j] = [excess,current[row,j]*(1.-np.exp(-excess)),excess/scale,
                     rank_changes[row,j]-baseline_rank,rank_fraction[row,j]-baseline_fraction,
                     (1.+np.sum(peers>=changes[row,j]))/(1.+len(peers)),scale,len(peers)]
    return result


def prepare(root, meta, enhanced=False, placebo=False):
    sessions, courses = {}, defaultdict(list)
    for year in range(2021,2027):
        bundle = json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        lookup = {str(s['id']):s for s in bundle['Data']['Sessions']}
        for run in bundle['PaceSessions']:
            sid, rider = str(run['SessionId']), str(run['Run']['RiderId'])
            if sid not in sessions:
                sessions[sid] = dict(date=lookup[sid]['session_date'],course=run['CourseKey'],rows={},layout=0)
                courses[run['CourseKey']].append(sid)
            t = np.full(5,np.nan)
            for s in run['Sectors']:
                if s['TimeSeconds'] is not None and s['TimeSeconds'] > 0:
                    t[s['SectorIndex']] = s['TimeSeconds']
            sessions[sid]['rows'][rider] = t
            sessions[sid]['layout'] = max(sessions[sid]['layout'],len(run['Sectors']))
    features, availability, coverage = {}, {}, {}
    for sid, current in sessions.items():
        references = defaultdict(list)
        changes = defaultdict(list)
        prior_ids = [p for p in courses[current['course']] if
            earlier_session(sessions[p]['date'],current['date']) and sessions[p]['layout'] == current['layout']]
        for prior_id in prior_ids:
            prior = sessions[prior_id]
            common = sorted(current['rows'].keys() & prior['rows'].keys())
            if len(common) < 2:
                continue
            previous = np.array([prior['rows'][r] for r in common])
            present = np.array([current['rows'][r] for r in common])
            for i,rider in enumerate(common):
                view = previous
                if placebo:
                    candidates = np.flatnonzero(np.all(np.isfinite(previous) & (previous>0),axis=1) &
                                                (np.arange(len(previous)) != i))
                    if len(candidates):
                        view = previous.copy()
                        view[i] = previous[candidates[i%len(candidates)]]
                        view[i,~np.isfinite(previous[i])] = np.nan
                expected, counts = paired_reference(view,present,i)
                references[rider].append((expected,counts))
                if enhanced:
                    changes[rider].append(change_profile(view,present,i))
        for rider,times in current['rows'].items():
            f = np.zeros((5,9)); f[:,8] = 1.
            available = np.zeros((5,2))
            for j in range(5):
                ref = [(e[j],c[j]) for e,c in references[rider] if np.isfinite(e[j]) and e[j] > 0]
                if not ref:
                    continue
                values = np.array([r[0] for r in ref])
                center, minimum = float(np.median(values)), float(np.min(values))
                available[j] = [len(ref),float(np.median([r[1] for r in ref]))]
                if not np.isfinite(times[j]) or times[j] <= 0:
                    continue
                f[j] = [times[j]-center,np.log(times[j]/center),times[j]-minimum,
                    np.log(times[j]/minimum),(np.quantile(values,.75)-np.quantile(values,.25))/center,
                    len(ref),available[j,1],center,0.]
            key = f'{sid}/{rider}'
            features[key] = np.concatenate([f.ravel(),f.max(axis=0),np.median(f,axis=0),f.min(axis=0),f.std(axis=0)])
            if enhanced:
                added = np.zeros((5,8))
                for j in range(5):
                    for col in range(8):
                        values = [c[j,col] for c in changes[rider] if np.isfinite(c[j,col])]
                        added[j,col] = np.median(values) if values else 0.
                features[key] = np.concatenate([features[key],added.ravel(),added.max(axis=0),
                    np.median(added,axis=0),added.min(axis=0),added.std(axis=0)])
            availability[key] = np.concatenate([available.ravel(),[np.mean(available[:,0]>0),np.max(available[:,0])]])
            coverage[key] = bool(np.any((f[:,8] == 0)))
    for row in meta:
        row['paired_reference'] = coverage[row['key']]
    return np.array([features[r['key']] for r in meta]), np.array([availability[r['key']] for r in meta])
