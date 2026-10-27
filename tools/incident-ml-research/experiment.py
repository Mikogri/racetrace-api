"""Positive/unlabelled ranking experiment. No crash probabilities or clean labels."""
import argparse
import csv
import hashlib
import importlib.metadata as metadata
import json
import os
import pickle
import platform
import time
from pathlib import Path

os.environ.setdefault('OMP_NUM_THREADS', '2')
import numpy as np
from lightgbm import LGBMClassifier
from xgboost import XGBClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import RobustScaler
from data import load_dataset, FEATURE_NAMES, SECTOR_NAMES, sha

SEED = 20261026
BUDGETS = [.075, .10, .125, .15, .20]


def threshold_for_budget(scores, budget):
    """Strict > means ties cannot silently exceed the calibration budget."""
    return float(np.sort(scores)[max(0, min(len(scores)-1, int(np.ceil((1-budget)*len(scores)))-1))])


def evaluation_splits(years):
    for year in sorted(set(years)):
        yield f'leave-{year}-out', np.flatnonzero(years != year), np.flatnonzero(years == year)
    yield 'chronological-2025', np.flatnonzero(years <= 2024), np.flatnonzero(years == 2025)
    yield 'chronological-2026', np.flatnonzero(years <= 2025), np.flatnonzero(years == 2026)


def train_bags(kind, parameter, x, y):
    """Case-control PU bagging: zeros mean UNLABELLED, never verified clean.

    Learns P(observed label | features) under contaminated controls. Arbitrary,
    non-random reporting of crashes prevents identifying P(actual crash).
    """
    models = []
    p, u = np.flatnonzero(y), np.flatnonzero(~y)
    if len(p) < 10:
        raise ValueError('Insufficient positive runs in training partition')
    for bag in range(3):
        rng = np.random.default_rng(SEED+bag)
        sampled = np.concatenate([p, rng.choice(u, min(len(u), max(1000, len(p)*15)), replace=False)])
        target = y[sampled].astype(int)
        weights = np.where(target == 1, .5/len(p), .5/np.sum(target == 0))*len(sampled)
        if kind == 'logistic':
            model = make_pipeline(RobustScaler(), LogisticRegression(C=parameter, max_iter=1500, random_state=SEED+bag))
            model.fit(x[sampled], target, logisticregression__sample_weight=weights)
        elif kind == 'lightgbm':
            model = LGBMClassifier(n_estimators=100, num_leaves=parameter, learning_rate=.04,
                                   min_child_samples=35, reg_lambda=2., n_jobs=2,
                                   deterministic=True, force_col_wise=True, verbosity=-1, random_state=SEED+bag)
            model.fit(x[sampled], target, sample_weight=weights)
        else:
            model = XGBClassifier(n_estimators=100, max_depth=parameter, learning_rate=.04,
                                  min_child_weight=8, reg_lambda=3., tree_method='hist',
                                  n_jobs=2, random_state=SEED+bag)
            model.fit(x[sampled], target, sample_weight=weights)
        models.append(model)
    return models


def predict(models, x):
    return np.mean([m.booster_.predict(x) if isinstance(m, LGBMClassifier)
                    else m.predict_proba(x)[:, 1] for m in models], axis=0)


def metrics(y, predicted, baseline):
    hits, total = int(np.sum(y & predicted)), int(np.sum(y))
    gain = int(np.sum(y & predicted & ~baseline))
    lost = int(np.sum(y & baseline & ~predicted))
    new = int(np.sum(predicted & ~baseline))
    return dict(runs=len(y), positives=total, hits=hits, recall=hits/total if total else None,
                alarms=int(sum(predicted)), alarm_rate=float(np.mean(predicted)) if len(y) else None,
                additional_hits=gain, lost_hits=lost, additional_alarms=new,
                net_alarm_change=int(sum(predicted)-sum(baseline)),
                new_alarms_per_added_hit=new/gain if gain else None)


def localize(features):
    valid = (features[:, 12] == 0) & (features[:, 13] == 0) & (features[:, 0] > 0)
    if np.sum(features[:, 12] == 0) < 2 or not valid.any():
        return []
    # Diagnostic localization, NOT sector training from bag/run labels. This is
    # deliberately reported separately from the learned run-ranking model.
    score = np.maximum(features[:, 3], 0)*np.maximum(features[:, 0], 0)
    return sorted(np.flatnonzero(valid), key=lambda i: (-score[i], i))


def localization_metrics(meta, features, test, predicted):
    eligible = [i for i in test if meta[i]['positive'] and meta[i]['known_sectors']]
    exact, adjacent, detected, localized = 0, 0, 0, 0
    pred_by_id = dict(zip(test, predicted))
    for i in eligible:
        if not pred_by_id[i]:
            continue
        detected += 1
        ranking = localize(features[i])
        if not ranking:
            continue
        localized += 1
        s = ranking[0]+1
        exact += int(s in meta[i]['known_sectors'])
        adjacent += int(any(abs(s-k) <= 1 for k in meta[i]['known_sectors']))
    return dict(sector_labelled_runs=len(eligible), detected_with_sector_label=detected,
                localized=localized, exact_hits=exact, adjacent_hits=adjacent,
                exact_end_to_end=exact/len(eligible) if eligible else None,
                adjacent_end_to_end=adjacent/len(eligible) if eligible else None,
                exact_given_detection=exact/detected if detected else None,
                adjacent_given_detection=adjacent/detected if detected else None)


def cluster_interval(meta, indices, predicted, repeats=500):
    """Event-cluster bootstrap of observed-positive recall, not crash recall."""
    groups = {}
    for i, p in zip(indices, predicted):
        group = groups.setdefault(meta[i]['group'], [0, 0])
        group[0] += int(meta[i]['positive'] and p)
        group[1] += int(meta[i]['positive'])
    values = np.array(list(groups.values()))
    rng, samples = np.random.default_rng(SEED), []
    for _ in range(repeats):
        hits, positives = values[rng.integers(0, len(values), len(values))].sum(axis=0)
        if positives:
            samples.append(hits/positives)
    return [float(v) for v in np.quantile(samples, [.025, .975])] if samples else [None, None]


def write_csv(path, rows):
    if not rows:
        return
    with path.open('w', encoding='utf-8', newline='') as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def license_inventory(out):
    items = []
    for name in sorted(d.metadata['Name'] for d in metadata.distributions()
                       if d.metadata['Name'].lower() not in ['pip', 'setuptools']):
        dist = metadata.distribution(name)
        files = []
        for file in dist.files or []:
            if ('license' in str(file).lower() or 'copying' in str(file).lower()) and '.dist-info' in str(file):
                path = dist.locate_file(file)
                if path.is_file():
                    target = out/'licenses'/name/Path(*Path(file).parts[1:])
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(path.read_bytes())
                    files.append(dict(file=str(target.relative_to(out)), sha256=sha(target)))
        items.append(dict(package=name, version=dist.version, requires=dist.requires,
                          license=dist.metadata.get('License-Expression') or dist.metadata.get('License'), files=files))
    (out/'license-inventory.json').write_text(json.dumps(items, indent=2), encoding='utf-8')


def svg_curve(path, rows):
    colors = ['#555555', '#999999', '#3388aa', '#339966', '#aa7733', '#8855bb', '#bb4466']
    names = sorted(set(r['model'] for r in rows))
    pieces = ['<svg xmlns="http://www.w3.org/2000/svg" width="900" height="560" viewBox="0 0 900 560">',
              '<rect width="900" height="560" fill="white"/>',
              '<text x="65" y="26" font-size="18">Known-incident recall vs actual alarm rate — leave-season-out</text>']
    for tick in range(0, 101, 20):
        yy = 450-4*tick
        pieces.append(f'<path d="M65,{yy} H650" stroke="#dddddd"/><text x="25" y="{yy+5}">{tick}%</text>')
    for tick in range(0, 41, 5):
        xx = 65+14*tick
        pieces.append(f'<text x="{xx}" y="475">{tick}%</text>')
    for color, name in zip(colors, names):
        points = []
        for row in sorted([r for r in rows if r['model']==name], key=lambda r:r['alarm_rate']):
            points.append(f"{65+1400*row['alarm_rate']:.2f},{450-400*row['recall']:.2f}")
        pieces.append(f'<polyline points="{" ".join(points)}" fill="none" stroke="{color}" stroke-width="2"/>')
        pieces.append(f'<text x="680" y="{75+25*names.index(name)}" fill="{color}">{name}</text>')
    pieces += ['<text x="240" y="515">Actual flagged-run percentage (not FPR)</text>', '</svg>']
    path.write_text('\n'.join(pieces), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml'))
    args = parser.parse_args()
    root, out = args.root.resolve(), args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    protected = {str(p.relative_to(root)): sha(p) for base in ['racetrace-api', 'racetrace-api.Tests']
                 for p in (root/base).rglob('*.cs') if 'obj' not in p.parts}
    started = time.perf_counter()
    x, meta, sector_data, input_hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    y = np.array([m['positive'] for m in meta])
    years = np.array([m['year'] for m in meta])
    baseline = np.array([m['production'] for m in meta])
    rows, subgroups, localization, tuning, timing, predictions = [], [], [], [], [], []
    sector_label_count = sum(m['positive'] and bool(m['known_sectors']) for m in meta)
    print(f'Data: {len(x)} runs, {sum(y)} positive labels, {sector_label_count} sector-labelled runs; {x.shape[1]} features', flush=True)
    for split, train, test in evaluation_splits(years):
        cal_year = max(years[train])
        fit, cal = train[years[train] != cal_year], train[years[train] == cal_year]
        assert not set(meta[i]['group'] for i in train) & set(meta[i]['group'] for i in test)
        assert not set(meta[i]['group'] for i in fit) & set(meta[i]['group'] for i in cal)
        model_bank = {}
        scores = {name: (np.array([m[f'{name}_score'] for m in meta]), None) for name in ['production', 'current', 'high_recall']}
        # Original high-recall experiment is available as immutable binary flags,
        # not retrained at test-specific budgets. Report it at its natural rate.
        for kind, parameters in [('logistic', [.1, 1.]), ('lightgbm', [7, 15]), ('xgboost', [2, 4])]:
            best = None
            for parameter in parameters:
                tick = time.perf_counter()
                models = train_bags(kind, parameter, x[fit], y[fit])
                cal_score = predict(models, x[cal])
                known_recall = np.mean([np.mean(cal_score[y[cal]] > threshold_for_budget(cal_score, b)) for b in BUDGETS])
                tuning.append(dict(split=split, model=kind, parameter=parameter,
                                   calibration_year=int(cal_year), fit_years=sorted(set(int(v) for v in years[fit])),
                                   calibration_recall=float(known_recall), fit_seconds=time.perf_counter()-tick))
                if best is None or known_recall > best[0]:
                    best = known_recall, models, parameter
            tick = time.perf_counter()
            full_score = predict(best[1], x)
            timing.append(dict(split=split, model=kind, predict_runs=len(x), seconds=time.perf_counter()-tick))
            scores[kind] = full_score, best[2]
            model_bank[kind] = best[1]
            with (out/f'{split}-{kind}.pickle').open('wb') as handle:
                pickle.dump(dict(models=best[1], feature_names=FEATURE_NAMES, seed=SEED), handle)
        scores['hybrid'] = .85*scores['lightgbm'][0] + .15*baseline.astype(float), None
        print(f'{split}: fit={len(fit)}, calibration={len(cal)}, test={len(test)}', flush=True)
        for name, (score, parameter) in scores.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(score[cal], budget)
                pred = score[test] > threshold
                detail = dict(split=split, model=name, budget=budget, threshold=threshold,
                              **metrics(y[test], pred, baseline[test]))
                if split.startswith('chronological'):
                    detail['recall_ci_low'], detail['recall_ci_high'] = cluster_interval(meta, test, pred)
                else:
                    detail['recall_ci_low'], detail['recall_ci_high'] = None, None
                rows.append(detail)
                localization.append(dict(split=split, model=name, budget=budget,
                                         **localization_metrics(meta, sector_data, test, pred)))
                for field in ['category', 'status']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                                              **metrics(y[test][take], pred[take], baseline[test][take])))
            for i in test:
                predictions.append(dict(split=split, model=name, key=meta[i]['key'], score=float(score[i])))
        # Save a full diagnostic review for the forward 2026 holdout at 15% target.
        if split == 'chronological-2026':
            error_rows = []
            score = scores['lightgbm'][0]
            threshold = threshold_for_budget(score[cal], .15)
            contributions = np.mean([m.booster_.predict(x[test], pred_contrib=True)
                                     for m in model_bank['lightgbm']], axis=0)
            contribution_by_row = dict(zip(test, contributions))
            for i in test:
                detected = bool(score[i] > threshold)
                if not ((y[i] and not detected) or (detected and not baseline[i])):
                    continue
                ranking = localize(sector_data[i])
                error_rows.append(dict(**meta[i], score=float(score[i]), detected=detected,
                    kind='missed-labelled' if y[i] and not detected else 'new-alarm-unverified',
                    predicted_sector=int(ranking[0]+1) if ranking else 'unknown',
                    candidate_sectors=[int(j+1) for j in ranking[:3]],
                    predicted_times=[None if m else float(t-loss) for t,loss,m in zip(
                        [0 if t is None else t for t in meta[i]['times']], sector_data[i][:,1], sector_data[i][:,12])],
                    sector_features=sector_data[i].tolist(),
                    top_contributions=[dict(feature=FEATURE_NAMES[j], value=float(x[i,j]),
                                            mean_logit_contribution=float(contribution_by_row[i][j]))
                        for j in np.argsort(np.abs(contribution_by_row[i][:-1]))[-5:][::-1]],
                    reason='Learned PU run score crossed calibration threshold; sector ranking is diagnostic, not a crash cause' if detected else
                    'Learned PU run score below calibration threshold; not proof of clean run'))
            (out/'error-analysis.json').write_text(json.dumps(error_rows, indent=2), encoding='utf-8')
    write_csv(out/'metrics.csv', rows)
    write_csv(out/'subgroups.csv', subgroups)
    write_csv(out/'localization.csv', localization)
    write_csv(out/'predictions.csv', predictions)
    pooled = []
    for model in sorted(set(r['model'] for r in rows)):
        for budget in BUDGETS:
            part = [r for r in rows if r['split'].startswith('leave-') and r['model']==model and r['budget']==budget]
            pooled.append(dict(model=model, budget=budget, hits=sum(r['hits'] for r in part),
                               positives=sum(r['positives'] for r in part), alarms=sum(r['alarms'] for r in part),
                               recall=sum(r['hits'] for r in part)/sum(r['positives'] for r in part),
                               alarm_rate=sum(r['alarms'] for r in part)/sum(r['runs'] for r in part)))
    write_csv(out/'pooled.csv', pooled)
    svg_curve(out/'recall-alarm.svg', pooled)
    fixed = {name: metrics(y, np.array([m[name] for m in meta]), baseline) for name in ['production','current','high_recall']}
    manifest = dict(seed=SEED, python=platform.python_version(), platform=platform.platform(),
                    cpu=platform.processor(), run_count=len(x), positive_count=int(sum(y)),
                    explicit_sector_runs=sector_label_count, features=FEATURE_NAMES, sector_features=SECTOR_NAMES,
                    source_observations=411, matched_unique_positives=341,
                    missing_only=sum(all(t is None for t in m['times']) for m in meta if m['positive']),
                    fixed_baselines=fixed, input_hashes=input_hashes, protected_source_hashes=protected,
                    research_source_hashes={p.name: sha(p) for p in Path(__file__).parent.glob('*.py')},
                    quality=dict(statuses={str(s): sum(m['status']==s for m in meta) for s in set(m['status'] for m in meta)},
                                 sector_counts={str(n): sum(len(f)==n for f in sector_data) for n in set(map(len, sector_data))},
                                 runs_with_checkpoint_feature=sum(bool(np.any(f[:,13])) for f in sector_data),
                                 runs_with_missing_feature=sum(bool(np.any(f[:,12])) or len(f)==0 for f in sector_data)),
                    tuning=tuning, timing=timing, elapsed_seconds=time.perf_counter()-started)
    for relative, digest in protected.items():
        assert sha(root/relative) == digest, f'Production source changed: {relative}'
    license_inventory(out)
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(dict(fixed=fixed, pooled=pooled, seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
