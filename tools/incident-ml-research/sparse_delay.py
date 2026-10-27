"""Robust normal-tempo covariance versus a nonnegative local delay; offline only."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from data import load_dataset, sha
from objectives import calibration_partition, train, score
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def design(indices, trend):
    columns = [np.ones(len(indices))]
    if trend:
        columns.append(np.asarray(indices, float) / 2. - 1.)
    return np.column_stack(columns)


def fit_covariance(profiles, support, trend=False):
    """Fit a robust residual covariance to the fit mixture, never a 'clean' subset.

    Residual contrasts eliminate arbitrary global pace (and optional linear trend).
    Trimming is a model assumption about the majority, not incident supervision.
    """
    basis = np.linalg.qr(design(np.arange(5), trend), mode='complete')[0]
    nuisance = 2 if trend else 1
    contrasts = basis[:, nuisance:]
    complete = profiles[np.isfinite(profiles).all(axis=1)]
    if len(complete) < 20:
        raise ValueError('Insufficient complete fit profiles for covariance estimation')
    residuals = complete @ contrasts
    center = np.median(residuals, axis=0)
    scale = np.maximum(.002, 1.4826 * np.median(np.abs(residuals - center), axis=0))
    covariance = np.diag(scale ** 2)
    for _ in range(8):
        difference = residuals - center
        distance = np.einsum('ij,jk,ik->i', difference, np.linalg.inv(covariance), difference)
        keep = np.argsort(distance, kind='stable')[:max(20, int(len(distance) * support))]
        center = residuals[keep].mean(axis=0)
        difference = residuals[keep] - center
        covariance = difference.T @ difference / len(keep)
        covariance += np.eye(len(scale)) * max(.002 ** 2, .05 * np.trace(covariance) / len(scale))
    return contrasts @ covariance @ contrasts.T + basis[:, :nuisance] @ basis[:, :nuisance].T


def delay_features(profiles, covariance, trend=False, max_length=1):
    """Gaussian/Student-t improvement after fitting a positive interval delay.

    Columns: Gaussian gain, fitted log delay, null squared distance, Student-t
    gain (nu=4), remaining squared distance. Missing dimensions are marginalized,
    not zero-filled or conditioned on unobserved measurements. No crash likelihood
    or corrected finish time is inferred from these diagnostic model comparisons.
    """
    result = np.zeros((len(profiles), 5, 5))
    masks = np.isfinite(profiles)
    for mask in np.unique(masks, axis=0):
        indices = np.flatnonzero(mask)
        x = design(indices, trend)
        rank = np.linalg.matrix_rank(x) if len(indices) else 0
        degrees = len(indices) - rank
        if degrees <= 0:
            continue
        take = np.flatnonzero((masks == mask).all(axis=1))
        # Invert the marginal covariance, not a subset of its full precision.
        precision = np.linalg.inv(covariance[np.ix_(indices, indices)])
        projector = precision - precision @ x @ np.linalg.pinv(x.T @ precision @ x) @ x.T @ precision
        projector = (projector + projector.T) / 2.
        values = profiles[take][:, indices]
        projected = values @ projector
        null = np.maximum(0., np.einsum('ij,ij->i', values, projected))
        null[null < 1e-18] = 0.
        for j in indices:
            best = np.zeros((len(take), 5))
            for length in range(1, min(max_length, 5 - j) + 1):
                pattern = ((indices >= j) & (indices < j + length)).astype(float)
                denominator = float(pattern @ projector @ pattern)
                if denominator < 1e-10:
                    continue
                numerator = np.maximum(0., projected @ pattern)
                delay = numerator / denominator
                gain = np.minimum(null / 2., numerator ** 2 / denominator / 2.)
                remaining = np.maximum(0., null - 2. * gain)
                heavy_gain = (4. + degrees) / 2. * np.log1p(2. * gain / (4. + remaining))
                candidate = np.column_stack([gain, delay, null, heavy_gain, remaining])
                improve = gain > best[:, 0]
                best[improve] = candidate[improve]
            result[take, j] = best
    return result


def prepare_profiles(sectors):
    profiles = np.full((len(sectors), 5), np.nan)
    for i, values in enumerate(sectors):
        for j, value in enumerate(values[:5]):
            if value[12] == 0:
                profiles[i, j] = value[4]
    return profiles


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-sparse-delay'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    original, meta, sectors, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    profiles = prepare_profiles(sectors)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    configs = [(support, trend, length) for support in [.7, .9] for trend in [False, True] for length in [1, 3]]
    rows, tuning, predictions, calibration_scores, subgroups, selections = [], [], [], [], [], []
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        best = {}
        for support, trend, length in configs:
            covariance = fit_covariance(profiles[fit], support, trend)
            local = delay_features(profiles, covariance, trend, length)
            features = local.reshape(len(meta), -1)
            # Fit-only scale estimates, no final rank, stage or label as a predictor.
            for name, values in [('gaussian-delay', local[:, :, 0].max(axis=1)),
                                 ('heavy-tail-delay', local[:, :, 3].max(axis=1))]:
                threshold = threshold_for_budget(values[cal], 700 / 20340)
                objective = float(np.mean(values[cal][y[cal]] > threshold))
                parameters = dict(support=support, trend=trend, max_length=length)
                tuning.append(dict(split=split, model=name, parameters=json.dumps(parameters), objective=objective))
                if name not in best or objective > best[name][0]:
                    best[name] = objective, values, dict(covariance=covariance, **parameters)
            for name, matrix in [('delay-only-learned', features),
                                 ('delay-original-learned', np.hstack([original, features]))]:
                for leaves in [7, 15]:
                    model = train('all-unlabelled', leaves, matrix[fit], y[fit], years[fit])
                    values = score(model, matrix)
                    threshold = threshold_for_budget(values[cal], 700 / 20340)
                    objective = float(np.mean(values[cal][y[cal]] > threshold))
                    parameters = dict(support=support, trend=trend, max_length=length, leaves=leaves)
                    tuning.append(dict(split=split, model=name, parameters=json.dumps(parameters), objective=objective))
                    if name not in best or objective > best[name][0]:
                        best[name] = objective, values, dict(covariance=covariance, model=model, **parameters)
            print(f'{split}: covariance support={support}, trend={trend}, length={length} evaluated', flush=True)
        for name, (objective, values, model) in best.items():
            with (out / f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(model, handle)
            selections.append(dict(split=split, model=name, calibration_objective=objective,
                parameters={k: v for k, v in model.items() if k not in ['model', 'covariance']},
                fit_rows=len(fit), calibration_rows=len(cal)))
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
    pooled = []
    for name in best:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                recall=hits / int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('pooled', pooled), ('predictions', predictions),
                         ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', values)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hash=sha(Path(__file__)), selections=selections,
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter() - started,
        goal_passed=any(r['goal_passed'] for r in pooled), exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Robust normal-tempo covariance and sparse positive-delay model', '',
        'Sector log times relative to their field medians form five-dimensional profiles. Fit-only '
        'robust covariance estimation removes arbitrary uniform pace, optionally a linear sector trend, '
        'then iteratively retains the closest 70% or 90% of the contaminated fit mixture. This assumes '
        'a normal majority; retained rows are not verified clean labels. Missing dimensions use marginal '
        'covariances. One measured sector has no identifiable local delay with a free pace intercept.', '',
        'Generalized model comparisons fit a nonnegative log-time delay on one sector or up to three '
        'contiguous sectors. Gaussian and heavy-tailed Student-t (nu=4) residual improvements are '
        'diagnostic scores, not calibrated crash probabilities. Smooth tempo and partial observations '
        'are covered by numerical tests. Learned variants use these diagnostics alone or with original '
        'timing features; no explicit stage, identity or final-result predictor. Calibration selects '
        'support, trend, interval length and LightGBM leaves 7/15, never test labels or quotas.', '',
        'Same eight held-season/chronological tests, grouped-round calibration and five fixed budgets. '
        'Previously inspected years remain exploratory, positive-only reporting bias remains unresolved. '
        'No production algorithm was changed.', '',
        table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
            [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Sparse-delay study: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. All scores/thresholds and selections are retained "
              'under `artifacts/incident-ml-sparse-delay/`. No detector was promoted.', '']
    (out / 'sparse-delay.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r: r['hits']) if eligible else None,
                         elapsed_seconds=manifest['elapsed_seconds']), indent=2), flush=True)


if __name__ == '__main__':
    main()
