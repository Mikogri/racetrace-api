"""Nonnegative PU logistic-risk experiment, without session-stage predictors."""
import argparse
import itertools
import json
import pickle
import time
from pathlib import Path
import numpy as np
import lightgbm as lgb
from scipy.special import expit
from data import load_dataset, sha
from trajectory import prepare, historical_features
from objectives import calibration_partition
from experiment import SEED, evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def nonnegative_pu(raw, positive_sample, prior):
    if not 0 < prior < 1 or not positive_sample.any() or positive_sample.all():
        raise ValueError('A nondegenerate prior and both P/U samples are required')
    p = expit(raw)
    pos, unknown = positive_sample, ~positive_sample
    np_, nu = int(pos.sum()), int(unknown.sum())
    positive_risk = prior*np.logaddexp(0., -raw[pos]).mean()
    negative_risk = np.logaddexp(0., raw[unknown]).mean()-prior*np.logaddexp(0., raw[pos]).mean()
    gradient, curvature = np.zeros(len(raw)), np.full(len(raw), 1e-8)
    if negative_risk >= 0:
        gradient[pos] = -prior/np_
        gradient[unknown] = p[unknown]/nu
        curvature[unknown] = np.maximum(1e-8, p[unknown]*(1.-p[unknown])/nu)
    else:
        gradient[pos] = prior*(p[pos]-1.)/np_
    # Positive diagonal preconditioner: not the exact Hessian in the active
    # risk branch, where the positive-sample loss is locally linear.
    curvature[pos] = np.maximum(1e-8, prior*p[pos]*(1.-p[pos])/np_)
    return float(positive_risk+max(0., negative_risk)), gradient, curvature, float(negative_risk)


def fit_model(features, positive, prior, leaves):
    p = np.flatnonzero(positive)
    # U is the complete fit population, including observed positives. The
    # duplicated P rows estimate the positive expectation, not synthetic data.
    samples = np.vstack([features[p], features])
    sample_role = np.arange(len(samples)) < len(p)
    offset = float(np.log(prior/(1.-prior)))
    data = lgb.Dataset(samples, label=np.zeros(len(samples)))
    diagnostics = []
    def objective(raw, dataset):
        risk, gradient, curvature, negative = nonnegative_pu(raw+offset, sample_role, prior)
        diagnostics.append(dict(risk=risk, negative_component=negative))
        return gradient*len(samples), curvature*len(samples)
    model = lgb.train(dict(objective=objective, metric='None', num_leaves=leaves,
        learning_rate=.035, min_data_in_leaf=35, lambda_l2=5., max_delta_step=1.,
        num_threads=2, deterministic=True, force_col_wise=True, verbosity=-1, seed=SEED),
        data, num_boost_round=300)
    model.params['objective'] = 'none'
    return model, offset, diagnostics


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-nnpu'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    shape, records, _ = prepare(root, meta)
    y = np.array([r['positive'] for r in meta])
    years = np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, predictions, tuning, groups, selections = [], [], [], [], []
    grid = list(itertools.product([.02, .05, .1, .2], [7, 15]))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        history = historical_features(records, meta, fit)
        views = {'nnpu-timing': x, 'nnpu-shape-history': np.hstack([x, shape, history])}
        scores, overall = {}, None
        for name, features in views.items():
            best = None
            for prior, leaves in grid:
                model, offset, diagnostics = fit_model(features[fit], y[fit], prior, leaves)
                values = model.predict(features[cal])+offset
                threshold = threshold_for_budget(values, 700/20340)
                objective = float(np.mean(values[y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, assumed_prior=prior, leaves=leaves,
                    calibration_recall=objective, final_risk=diagnostics[-1]['risk'],
                    clipped_iterations=sum(d['negative_component'] < 0 for d in diagnostics)))
                if best is None or objective > best[0]:
                    best = objective, model, offset, prior, leaves, diagnostics
            scores[name] = best[1].predict(features)+best[2]
            if overall is None or best[0] > overall[0]:
                overall = best[0], name
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(model=best[1], offset=best[2], prior=best[3], leaves=best[4],
                                 optimization=best[5]), handle)
            print(f'{split}: {name}, 8 prior/leaf candidates evaluated', flush=True)
        scores['selected-nnpu'] = scores[overall[1]]
        selections.append(dict(split=split, selected=overall[1], calibration_recall=overall[0]))
        for name, values in scores.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'status', 'session']:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        groups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
        write_csv(out/'metrics.csv', rows)
        write_csv(out/'tuning.csv', tuning)
    pooled = []
    for name in scores:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                               recall=hits/int(y.sum()), goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, values in [('pooled', pooled), ('predictions', predictions), ('subgroups', groups)]:
        write_csv(out/f'{name}.csv', values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(input_hashes=hashes, source_hash=sha(Path(__file__)), grid=grid, selections=selections,
        protected_sources_unchanged=True, elapsed_seconds=time.perf_counter()-started,
        goal_passed=any(r['goal_passed'] for r in pooled),
        exploratory_reuse_of_previously_inspected_years=True)
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Nonnegative positive-unlabelled risk search', '',
        'This implements nonnegative PU logistic risk, following the estimator of '
        '[Kiryo et al. (2017)](https://proceedings.neurips.cc/paper/2017/hash/7cce53cf90577442771720a370c3c723-Abstract.html). '
        'The fit population supplies the U expectation, including observed positives; a separate copy '
        'of observed P rows supplies the P expectation. No control is declared clean. Risk is '
        'pi * mean_P softplus(-f) + max(0, mean_U softplus(f) - pi * mean_P softplus(f)). '
        'Finite-difference tests cover both active and clipped branches. LightGBM uses exact gradients '
        'with a positive diagonal curvature preconditioner and bounded leaf updates, not an exact Hessian '
        'or the original paper neural-network training algorithm.', '',
        'Assumed priors 2/5/10/20% and leaves 7/15 are selected on grouped calibration rounds only. '
        'The true prior is not identified by these labels, and positive annotations are not a random '
        'positive sample: the usual PU consistency assumptions are not established. Scores are not '
        'calibrated crash probabilities. Views are original timing and timing plus aligned shape/history. '
        'No stage, category label, rider identity, outcome or annotation enters predictors. History joins '
        'strictly earlier same-category fit runs only. All measurements remain real frozen observations; '
        'no synthetic benchmark incidents are created.', '',
        'Same eight held-season/chronological tests, five predeclared operating targets, no test quota '
        'or test-top-700 selection. Reused years make this exploratory, not fresh validation.', '',
        table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
            [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    for split in ['chronological-2025','chronological-2026']:
        lines += [f'### nnPU: {split}', '', table(['Model','Target','Hits','Positives','Alarms'],
            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms']) for r in rows if r['split']==split]), '']
    lines += [f"Goal met: **{manifest['goal_passed']}**. Protected production code is unchanged. "
              'Metrics, risk diagnostics, subgroup counts (including session stage) and models stay in '
              '`artifacts/incident-ml-nnpu/`.', '']
    (out/'nnpu.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if r['alarms'] <= 700]
    print(json.dumps(dict(best_eligible=max(eligible, key=lambda r:r['hits']) if eligible else None,
                          elapsed_seconds=manifest['elapsed_seconds'], goal_passed=manifest['goal_passed']), indent=2), flush=True)


if __name__ == '__main__':
    main()
