"""Auxiliary human adjudication; frozen benchmark positives are never overwritten."""
import math
import numpy as np


def match_reviews(meta, rows):
    result, audit = {}, []
    for row in rows:
        matches = []
        for i, item in enumerate(meta):
            if not all(str(item[k]).strip().casefold() == str(row[k]).strip().casefold()
                       for k in ['year', 'rider', 'venue', 'category', 'session']):
                continue
            times = list(item['times']) + [None] * (5-len(item['times']))
            if all((a is None and b is None) or
                   (a is not None and b is not None and math.isclose(a, b, abs_tol=1e-7, rel_tol=1e-9))
                   for a, b in zip(times, row['times'])):
                matches.append(i)
        if len(matches) != 1:
            raise ValueError(f'Review {row["review_id"]} has {len(matches)} frozen run matches')
        i = matches[0]
        if i in result:
            raise ValueError('Multiple reviews matched the same frozen run')
        original = bool(meta[i]['positive'])
        conflict = original and row['decision'] == 'no'
        strict = row['decision'] in ['yes', 'no'] and not conflict and not row['evidence_flags']
        result[i] = dict(decision=row['decision'], sectors=row['sectors'], strict=strict,
                         conflict=conflict, review_id=row['review_id'])
        before = set(meta[i]['known_sectors'])
        after = set(row['sectors'])
        audit.append(dict(review_id=row['review_id'], key=meta[i]['key'], year=meta[i]['year'],
            original_positive=original, reviewed=row['decision'], sectors=row['sectors'],
            strict_eligible=strict, conflict=conflict, evidence_flags=row['evidence_flags'],
            sector_disagreement=bool(before and after and not before & after)))
    return result, audit


def training_labels(original, fit, reviews, promote):
    target = original.copy()
    clean = np.zeros(len(original), bool)
    for i in fit:
        row = reviews.get(int(i))
        if not row or not row.get('strict', False):
            continue
        if row['decision'] == 'yes' and promote:
            target[i] = True
        elif row['decision'] == 'no' and not original[i]:
            clean[i] = True
    return target, clean
