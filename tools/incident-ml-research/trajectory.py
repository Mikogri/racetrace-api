"""Retrospective run shape and strictly earlier, same-category sporting history."""
import json
from collections import defaultdict
import numpy as np


def history_reference(records, date, round_id, category):
    prior = [r for r in records if r[0] < date and r[1] != round_id and r[2] == category]
    values = np.array([r[3] for r in sorted(prior, key=lambda r: r[0])[-8:]])
    if not len(values):
        return 0., 0., 0
    center = float(np.median(values))
    return center, float(1.4826*np.median(np.abs(values-center))), len(values)


def run_shape(times, medians, best, history=(0., 0., 0)):
    t = np.array([np.nan if v is None else v for v in times], dtype=float)
    m, b = np.asarray(medians, float), np.asarray(best, float)
    valid = np.isfinite(t) & (t > 0) & np.isfinite(m) & (m > 0)
    ratio = np.full(len(t), np.nan)
    ratio[valid] = np.log(t[valid]/m[valid])
    result = np.zeros((len(t), 16))
    for j in range(len(t)):
        if not valid[j]:
            result[j, -1] = 1.
            continue
        before, after = ratio[:j][valid[:j]], ratio[j+1:][valid[j+1:]]
        other = np.concatenate([before, after])
        anchor = float(np.median(other)) if len(other) else ratio[j]
        pre = float(np.median(before)) if len(before) else anchor
        post = float(np.median(after)) if len(after) else anchor
        spread = float(1.4826*np.median(np.abs(other-anchor))) if len(other) else 0.
        floor = b[j] if np.isfinite(b[j]) and b[j] > 0 else 0.
        loss = lambda base: t[j]-max(floor, m[j]*np.exp(base))
        jump = ratio[j]-pre
        recovery = (ratio[j]-post)/max(.001, abs(jump)) if len(before) and len(after) else 0.
        result[j] = [loss(anchor), loss(pre), loss(post), jump, abs(post-pre),
                     np.clip(recovery, -5., 5.), spread, t[j]-floor if floor else 0.,
                     loss(history[0]) if history[2] else 0.,
                     (ratio[j]-history[0])/max(.02, history[1]) if history[2] else 0.,
                     float(len(before)>0), float(len(after)>0), len(other)/max(1, len(t)-1),
                     loss(float(np.quantile(other, .25))) if len(other) else 0.,
                     jump/max(.02, spread), 0.]
    return result


def paired_rank_drop(field, row, sector):
    """Rank contrast uses an identical cohort in each sector pair."""
    if not np.isfinite(field[row, sector]):
        return 0., 0
    drops, counts = [], []
    for other in range(field.shape[1]):
        if other == sector or not np.isfinite(field[row, other]):
            continue
        take = np.isfinite(field[:, sector]) & np.isfinite(field[:, other])
        count = int(take.sum())
        if count < 2:
            continue
        current = np.sum(field[take, sector] < field[row, sector])
        reference = np.sum(field[take, other] < field[row, other])
        drops.append((current-reference)/(count-1))
        counts.append(count)
    return (float(np.median(drops)), min(counts)) if drops else (0., 0)


def historical_features(records, meta, fit):
    histories = defaultdict(list)
    for i in fit:
        t, m, b, rider, date, round_id, category = records[meta[i]['key']]
        valid = np.isfinite(t) & (m > 0)
        if valid.sum() >= 2 and date:
            ability = float(np.quantile(np.log(t[valid]/m[valid]), .35))
            histories[rider].append((date, round_id, category, ability))
    features = []
    for record in meta:
        t, m, b, rider, date, round_id, category = records[record['key']]
        history = history_reference(histories[rider], date, round_id, category) if date else (0., 0., 0)
        losses = np.zeros((5, 2))
        valid = np.isfinite(t) & (t > 0) & (m > 0)
        if history[2]:
            losses[valid, 0] = t[valid]-np.maximum(b[valid], m[valid]*np.exp(history[0]))
            losses[valid, 1] = (np.log(t[valid]/m[valid])-history[0])/max(.02, history[1])
        features.append(np.concatenate([losses.ravel(), np.array(history)]))
    return np.array(features)


def prepare(root, meta):
    records, sessions = {}, defaultdict(list)
    for year in range(2021, 2027):
        bundle = json.loads((root/f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        lookup = {str(s['id']): s for s in bundle['Data']['Sessions']}
        for run in bundle['PaceSessions']:
            sid, rider = str(run['SessionId']), str(run['Run']['RiderId'])
            session = lookup[sid]
            t, m, b = np.full(5, np.nan), np.zeros(5), np.zeros(5)
            for s in run['Sectors']:
                j = s['SectorIndex']
                value = s['TimeSeconds']
                if value is not None and np.isfinite(value) and value > 0:
                    t[j] = value
                m[j] = s['Statistics']['Median'] or 0.
                b[j] = s['Statistics']['BestValue'] or 0.
            key = f'{sid}/{rider}'
            assert key not in records
            date, round_id, category = session['session_date'] or '', str(session['round_id']), str(session['category_id'])
            records[key] = (t, m, b, rider, date, round_id, category)
            sessions[sid].append(key)
    ranks = {}
    for keys in sessions.values():
        field = np.array([records[k][0] for k in keys])
        for i, key in enumerate(keys):
            ranks[key] = np.array([paired_rank_drop(field, i, j) for j in range(5)])
    current, diagnostics = [], []
    for record in meta:
        key = record['key']
        t, m, b, rider, date, round_id, category = records[key]
        shape = run_shape(t, m, b)
        no_history = np.delete(shape, [8, 9], axis=1)
        current.append(np.concatenate([no_history.ravel(), ranks[key].ravel()]))
        valid = np.flatnonzero(shape[:, -1] == 0)
        candidate = int(valid[np.argmax(shape[valid, 0])]) if len(valid) > 1 else None
        diagnostics.append(dict(key=key, sector=candidate+1 if candidate is not None else None,
            estimated_loss=float(shape[candidate, 0]) if candidate is not None else None))
    return np.array(current), records, diagnostics
