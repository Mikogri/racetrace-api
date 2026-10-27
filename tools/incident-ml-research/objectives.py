"""Compare observation-ranking objectives, retaining every unlabelled training run."""
import argparse
import json
import pickle
import time
from pathlib import Path
import numpy as np
from lightgbm import LGBMClassifier, LGBMRanker
from sklearn.ensemble import ExtraTreesClassifier
from sklearn.model_selection import StratifiedGroupKFold
from xgboost import XGBClassifier
from data import load_dataset, sha
from experiment import (SEED, evaluation_splits, predict, train_bags, threshold_for_budget,
                        metrics, write_csv, cluster_interval)
from conditional import goal_passed
from target import BUDGETS


def grouped_rows(groups):
    order = np.argsort(groups, kind='stable')
    return order, np.unique(groups[order], return_counts=True)[1]


def balanced_observation_weights(y):
    """Balance observed labels, not physical crash/clean classes."""
    positives = np.sum(y)
    if not 0 < positives < len(y):
        raise ValueError('Both positive and unlabelled observations are required')
    return np.where(y, .5 / positives, .5 / (len(y)-positives)) * len(y)


def calibration_partition(outer, years, meta, y, policy):
    if policy == 'latest':
        latest = max(years[outer])
        return outer[years[outer] != latest], outer[years[outer] == latest]
    groups = np.array([meta[i]['group'] for i in outer])
    splitter = StratifiedGroupKFold(n_splits=4, shuffle=True, random_state=SEED)
    fit, cal = next(splitter.split(np.zeros(len(outer)), y[outer], groups))
    return outer[fit], outer[cal]


def session_context(meta, fit):
    names = sorted({str(meta[i]['session']) for i in fit})
    return np.array([[float(str(row['session']) == name) for name in names] for row in meta]), names


def train(kind, parameter, x, y, years):
    weights = balanced_observation_weights(y)
    if kind == 'extra-trees':
        model = ExtraTreesClassifier(n_estimators=300, min_samples_leaf=parameter,
                                     max_features=.8, n_jobs=2, random_state=SEED)
        model.fit(x, y.astype(int), sample_weight=weights)
    elif kind == 'xgboost':
        model = XGBClassifier(n_estimators=300, max_depth=parameter, learning_rate=.035,
            min_child_weight=8, reg_lambda=5., tree_method='hist', n_jobs=2, random_state=SEED)
        model.fit(x, y.astype(int), sample_weight=weights)
    elif kind == 'all-unlabelled':
        model = LGBMClassifier(n_estimators=300, num_leaves=parameter, learning_rate=.025,
                               min_child_samples=35, reg_lambda=5., n_jobs=2,
                               deterministic=True, force_col_wise=True, verbosity=-1,
                               random_state=SEED)
        model.fit(x, y.astype(int), sample_weight=weights)
    else:
        leaves, truncation = parameter
        order, sizes = grouped_rows(years)
        model = LGBMRanker(objective='lambdarank', n_estimators=300,
                           num_leaves=leaves, learning_rate=.025, min_child_samples=35,
                           lambdarank_truncation_level=truncation, label_gain=[0, 1],
                           reg_lambda=5., n_jobs=2, deterministic=True,
                           force_col_wise=True, verbosity=-1, random_state=SEED)
        # Whole training seasons are queries: no row is discarded for belonging
        # to a session with no reported incident. Year is not a predictor.
        model.fit(x[order], y[order].astype(int), group=sizes)
    return model


def score(model, x):
    return (model.booster_.predict(x) if hasattr(model, 'booster_')
            else model.predict_proba(x)[:, 1])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--representation', choices=['original','trajectory','residual','dynamics','paired','environment'], default='original')
    parser.add_argument('--calibration', choices=['latest', 'grouped'], default='latest')
    parser.add_argument('--stage-context', action='store_true')
    parser.add_argument('--paired-enhanced', action='store_true')
    parser.add_argument('--paired-placebo', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    study = args.representation if args.representation != 'original' else 'objectives'
    out = (args.output or Path(f'artifacts/incident-ml-{study}')).resolve()
    out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    x, meta, sectors, hashes = load_dataset(root, args.documents,
        root/'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    y = np.array([m['positive'] for m in meta])
    years = np.array([m['year'] for m in meta])
    baseline = np.array([m['production'] for m in meta])
    rows, predictions, tuning, subgroups = [], [], [], []
    original = x
    if args.representation == 'trajectory':
        from trajectory import prepare, historical_features
        shape, records, diagnostics = prepare(root, meta)
        write_csv(out/'shape-diagnostics.csv', diagnostics)
    elif args.representation == 'environment':
        from environment import prepare
        from paired_sessions import prepare as prepare_pairs
        speed,conditions=prepare(root,meta)
        paired,availability=prepare_pairs(root,meta,enhanced=True)
    elif args.representation == 'paired':
        from paired_sessions import prepare
        from focus import relative_columns
        from data import FEATURE_NAMES
        paired, availability = prepare(root,meta,enhanced=args.paired_enhanced,placebo=args.paired_placebo)
    elif args.representation in ['residual','dynamics']:
        if args.representation == 'residual':
            from field_residual import prepare
        else:
            from checkpoint_dynamics import prepare
        from focus import relative_columns
        from data import FEATURE_NAMES
        residual = prepare(root, meta)
    variants = {'all-unlabelled': [7, 15], 'extra-trees': [5, 15],
                'season-ranker': [(7, 20), (7, 100), (15, 20), (15, 100)]}
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, args.calibration)
        cal_year = max(years[cal])
        assert not {meta[i]['group'] for i in test} & {meta[i]['group'] for i in outer}
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in cal}
        reference_name = 'original-frozen'
        if args.calibration == 'latest':
            with (root/f'artifacts/incident-ml/{split}-lightgbm.pickle').open('rb') as handle:
                scores = {reference_name: predict(pickle.load(handle)['models'], original)}
        else:
            # Frozen baseline estimators fitted some newly selected calibration
            # rounds. Retrain them rather than calibrating on in-sample scores.
            reference_name = 'reference-grouped'
            reference = None
            for leaves in [7, 15]:
                models = train_bags('lightgbm', leaves, original[fit], y[fit])
                values = predict(models, original[cal])
                objective = float(np.mean(values[y[cal]] > threshold_for_budget(values, 700/20340)))
                if reference is None or objective > reference[0]:
                    reference = objective, models
            scores = {reference_name: predict(reference[1], original)}
        feature_sets = {name: (name, parameters, original) for name, parameters in variants.items()}
        if args.representation == 'trajectory':
            historical = historical_features(records, meta, fit)
            joint = np.hstack([shape, historical])
            feature_sets = {'run-shape': ('all-unlabelled', [7, 15], shape),
                            'shape-history': ('all-unlabelled', [7, 15], joint),
                            'shape-history-trees': ('extra-trees', [5, 15], joint)}
            if args.calibration == 'grouped':
                feature_sets['shape-history-xgboost'] = ('xgboost', [2, 3, 4], joint)
        elif args.representation in ['residual','dynamics']:
            feature_sets = {'field-residual-only': ('all-unlabelled', [7, 15], residual),
                            'field-residual-original': ('all-unlabelled', [7, 15], np.hstack([original, residual])),
                            'field-residual-relative': ('all-unlabelled', [7, 15],
                                np.hstack([original[:, relative_columns(FEATURE_NAMES)], residual]))}
            if args.representation == 'dynamics':
                feature_sets={name.replace('field-residual','checkpoint'):value for name,value in feature_sets.items()}
        elif args.representation == 'paired':
            feature_sets = {'paired-original': ('all-unlabelled',[7,15],np.hstack([original,paired])),
                            'paired-relative': ('all-unlabelled',[7,15],np.hstack([original[:,relative_columns(FEATURE_NAMES)],paired])),
                            'paired-only': ('all-unlabelled',[7,15],paired),
                            'availability-only': ('all-unlabelled',[7,15],availability)}
        elif args.representation == 'environment':
            weather_names=sorted({conditions[i] for i in fit})
            weather=np.array([[float(condition==name) for name in weather_names] for condition in conditions])
            all_features=np.hstack([original,paired,speed,weather])
            feature_sets={'timing-speed':('all-unlabelled',[7,15],np.hstack([original,speed])),
                          'paired-speed':('all-unlabelled',[7,15],np.hstack([original,paired,speed])),
                          'paired-weather':('all-unlabelled',[7,15],np.hstack([original,paired,weather])),
                          'paired-speed-weather':('all-unlabelled',[7,15],all_features),
                          'paired-speed-xgboost':('xgboost',[2,3,4],all_features),
                          'speed-only':('all-unlabelled',[7,15],speed),
                          'availability-weather-only':('all-unlabelled',[7,15],np.hstack([speed[:,[0,1,7]],weather]))}
        stage_features, stage_names = session_context(meta, fit) if args.stage_context else (None, [])
        for name, (kind, parameters, features) in feature_sets.items():
            if args.stage_context:
                features = np.hstack([features, stage_features])
            best = None
            for parameter in parameters:
                model = train(kind, parameter, features[fit], y[fit], years[fit])
                calibration = score(model, features[cal])
                threshold = threshold_for_budget(calibration, 700/20340)
                objective = float(np.mean(calibration[y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, parameter=parameter,
                                   calibration_year=int(cal_year), calibration_recall=objective,
                                   feature_count=features.shape[1]))
                if best is None or objective > best[0]:
                    best = objective, model, parameter
            scores[name] = score(best[1], features)
            with (out/f'{split}-{name}.pickle').open('wb') as handle:
                pickle.dump(dict(model=best[1], parameter=best[2]), handle)
            print(f'{split}: {name} fitted', flush=True)
        for name, values in scores.items():
            for budget in BUDGETS:
                threshold = threshold_for_budget(values[cal], budget)
                flagged = values[test] > threshold
                rows.append(dict(split=split, model=name, budget=budget, threshold=threshold,
                                 **metrics(y[test], flagged, baseline[test]),
                                 recall_interval=cluster_interval(meta, test, flagged)))
                fields = ['category','status','session'] + (['paired_reference'] if args.representation in ['paired','environment'] else [])
                for field in fields:
                    for value in sorted(set(str(meta[i][field]) for i in test)):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field,
                            value=value, **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(values[i])) for i in test)
        write_csv(out/'metrics.csv', rows)
    pooled = []
    for name in [reference_name, *feature_sets]:
        for budget in BUDGETS:
            part = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in part), sum(r['alarms'] for r in part)
            pooled.append(dict(model=name, budget=budget, hits=hits, positives=int(y.sum()), alarms=alarms,
                               recall=hits/int(y.sum()), alarm_rate=alarms/len(y),
                               goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for filename, values in [('pooled.csv', pooled), ('predictions.csv', predictions), ('subgroups.csv', subgroups)]:
        write_csv(out/filename, values)
    protected = json.loads((root/'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root/p) == digest for p, digest in protected.items())
    manifest = dict(seed=SEED, input_hashes=hashes, source_hash=sha(Path(__file__)), tuning=tuning,
                    goal_passed=any(r['goal_passed'] for r in pooled), protected_sources_unchanged=True,
                    elapsed_seconds=time.perf_counter()-started, budgets=BUDGETS,
                    representation=args.representation,
                    calibration_policy=args.calibration,
                    stage_context=args.stage_context,
                    paired_enhanced=args.paired_enhanced,paired_placebo=args.paired_placebo,
                    exploratory_reuse_of_previously_inspected_years=True)
    if args.representation == 'trajectory':
        manifest['trajectory_source_hash'] = sha(Path(__file__).with_name('trajectory.py'))
    elif args.representation == 'residual':
        manifest['residual_source_hash'] = sha(Path(__file__).with_name('field_residual.py'))
    elif args.representation == 'dynamics':
        manifest['dynamics_source_hash'] = sha(Path(__file__).with_name('checkpoint_dynamics.py'))
    elif args.representation == 'paired':
        manifest['paired_source_hash'] = sha(Path(__file__).with_name('paired_sessions.py'))
        manifest['paired_coverage'] = dict(runs=sum(m['paired_reference'] for m in meta),
            known_positives=sum(m['positive'] and m['paired_reference'] for m in meta))
    elif args.representation == 'environment':
        manifest['environment_source_hash']=sha(Path(__file__).with_name('environment.py'))
    (out/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Full-mixture and learning-to-rank experiment', '',
        'Same 71 timing features and the same outer-year / latest-training-year calibration partitions. '
        'Only the training objective, estimator and control sampling change. All candidates retain every '
        'unlabelled fit run. `all-unlabelled` balances observed-positive and unlabelled total weight; '
        '`extra-trees` uses the same contaminated observation classes. `season-ranker` uses whole fit '
        'seasons as LambdaRank queries, never using year as a predictor. Reported incidents have relevance '
        '1 and unlabelled observations relevance 0: these are observed-label preferences, not verified '
        'crash/clean truth. Ranking loss can still suppress unreported crashes.', '',
        'The full parameter grid is in the manifest. Selection uses only calibration recall at 700/20,340. '
        'Five budgets were fixed before fitting, with strict score > calibration threshold; no test quota '
        'or test-top-700 selection is used. These years have already influenced research: this is exploratory, '
        'not an independent final validation.', '',
        table(['Model', 'Calibration target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
              [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    if args.representation == 'trajectory':
        lines = ['## Run-shape and historical-tempo experiment', '',
            'The feature representation is replaced, not just the estimator. Each sector retains its position '
            'in the sequence, loss versus other-sector / pre-sector / post-sector pace anchors, jump size, '
            'recovery, pre/post disagreement, best measured sector gap, robust spread and missingness. '
            'References are clamped to the fastest measured sector to avoid impossible extrapolated pace. '
            'Paired position contrasts use an identical measured-rider cohort in both sectors. '
            '`run-shape` has no rider history. The other two variants add up to eight strictly earlier '
            'different-round observations in the same category, estimated from the 35th percentile of '
            'within-run log relative sector times. IDs only join history, never enter predictors. '
            'History sources are restricted to the outer fit partition: calibration/test measurements '
            'never build training history, and later dates are always excluded. This deliberately discards '
            'some history that could be available at deployment.', '',
            'Recovery is a feature, not a mandatory detection gate: terminal and persistent incidents '
            'remain eligible. Missing times cannot invent a loss. Shape localization is diagnostic, '
            'not a validated sector correction. The same held-year tests, calibration split, predeclared '
            'five budgets and positive/unlabelled limitations apply. No test-top-700 selection. '
            'Previously inspected years make this exploratory, not a fresh independent validation.', '',
            table(['Model', 'Target', 'Hits / 341', 'Actual alarms', 'Recall', 'Goal passed'],
                [(r['model'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall']), r['goal_passed']) for r in pooled]), '']
    if args.representation == 'residual':
        lines = ['## Within-field residual standardization and leave-one-out sector prediction', '',
            'New measurements describe abnormal within-run sector contrasts relative to the distribution '
            'of those contrasts in the same session, rather than raw field timing dispersion. '
            'Median and 25th-percentile other-sector pace anchors produce residuals, second losses, '
            'and leave-row-out median/MAD standardization with a 0.004-log-unit scale floor. '
            'A ridge model predicts each sector log time from other measured log times and missingness '
            'indicators. Exact ridge leave-one-out leverage correction removes the target measurement '
            'from its own prediction; this is tested against explicit refits and a target-time mutation. '
            'Rows from the same finalized session provide timing context only, never incident labels.', '',
            'Three representations are compared with weighted full-mixture LightGBM: residual-only, '
            'original plus residual, and relative-only plus residual. Leaves 7/15 use calibration selection. '
            'Same 8 season tests, 5 predeclared budgets, no test quota. Context is retrospective, not '
            'an online forecast. Positive-only and previously-inspected-year limitations still apply.', '',
            table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
                [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    if args.representation == 'dynamics':
        lines = ['## Checkpoint rank and gap dynamics', '',
            'Intermediate checkpoint movement uses complete cumulative measurements from identical '
            'rider cohorts at both checkpoints. Features include raw/relative position loss, gap change, '
            'robust movement/gap deviation, prior checkpoint position and sample quality. Missing early '
            'sectors invalidate later cumulative measurements rather than being filled with zero time. '
            'Cumulative final position, final time and final gap are explicitly excluded, including '
            'four-sector layouts; final-sector rank remains a sector measurement. The within-session '
            'context is retrospective, uses no incident labels and is not an online forecast.', '',
            'Three full-mixture LightGBM representations (checkpoint only, plus original, plus relative) '
            'select leaves 7/15 on calibration. Same eight season tests and five predeclared budgets; '
            'no test quota. Positive-only and exploratory inspected-year limitations remain.', '',
            table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
                [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    if args.representation == 'paired':
        lines = ['## Earlier same-round rider profiles with matched-peer adjustment', '',
            'References use the same rider, course/category, round and sector count, with a strictly '
            'earlier date. Same-day chronology is unknown and never guessed from IDs/stage labels. '
            'Common riders across sessions estimate per-sector timing multipliers, excluding the target '
            'rider. This adjusts track/weather/session speed changes without mixing field strengths. '
            'The target rider global pace change is estimated from other current/prior sector ratios, '
            'excluding the candidate sector. Tests mutate that target time and verify its own reference '
            'is unchanged. Uniform pace change is not a local loss. No prior incident label or final '
            'outcome enters predictors. Missing references remain explicitly missing.', '',
            'Median/minimum earlier references generate local second/log losses and reference quality. '
            'Three timing variants compare paired-only, plus original, plus relative features. '
            '`availability-only` uses reference counts/coverage/peer counts but no timing losses: it '
            'tests whether history availability alone captures reporting/session-selection bias. '
            'Same eight outer-season tests and five predeclared budgets; no test quota. Context uses '
            'earlier test-session measurements only as input to later test runs, never as training '
            'labels; this is retrospective session context, not an online forecast.', '',
            f"Coverage: {manifest['paired_coverage']['runs']} runs / "
            f"{manifest['paired_coverage']['known_positives']} known positives. "
            'Per-stage, paired/unpaired, category and status breakdowns are exported. '
            'Positive-only and inspected-year limitations still apply.', '',
            table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
                [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    if args.representation == 'environment':
        lines=['## Speed-trap and round-condition context', '',
            'Only already frozen sporting inputs are used: measured top speed and round-condition name. '
            'Speed features are within-session log ratio, MAD deviation, percentile, and leave-one-out '
            'speed-vs-sector-tempo residual. They are invariant to a common speed-unit conversion; '
            'missing speed is not measured zero. There are no rider IDs, session labels or final places '
            'in predictors. Weather labels are dynamic fit-only one-hot values; unknown labels are ignored. '
            'Conditions describe a round, not verified weather at every individual session.', '',
            'Models compare timing+speed, paired+speed, paired+weather, paired+both, and XGBoost on '
            'all context. Speed-only and availability/weather-only controls diagnose selection and '
            'reporting confounding. Paired references use strictly earlier dates and common peers. '
            'LightGBM leaves 7/15 or XGBoost depths 2/3/4 use calibration only. Same eight outer season '
            'tests and five predeclared budgets, no test quota. Positive-only, prior-stage availability '
            'and previously inspected-year limitations still prevent a physical-crash/precision claim.', '',
            table(['Model','Target','Hits / 341','Actual alarms','Recall','Goal passed'],
                [(r['model'],pct(r['budget']),r['hits'],r['alarms'],pct(r['recall']),r['goal_passed']) for r in pooled]), '']
    if args.paired_enhanced:
        lines[2:2] = ['Enhanced paired features compare each rider sector change with their own '
            'other-sector change and with the same change profiles of common peers. They add local '
            'second/log excess, peer-MAD deviation, paired sector-rank movement and empirical peer tail '
            'rank. All ranks concern individual sectors, not final overall place. This is not a '
            'calibrated crash probability.', '']
    if args.paired_placebo:
        lines[2:2] = ['PLACEBO CONTROL: the target prior profile is replaced by another complete common '
            'rider profile, preserving the target prior missingness mask. Peer multipliers exclude the '
            'target row, and reference availability/counts remain the same. This deliberately removes '
            'correct personal profile alignment without changing current measurements or labels. '
            'It is an ablation, not a proposed detector or synthetic benchmark incident.', '']
    if args.stage_context:
        lines[2:2] = ['This study adds one-hot session-stage context fitted on fit rows only; unknown '
            'stages get all-zero context. The stage is known before a run, not a final outcome or rider '
            'identity. The reference retains its timing-only predictors. IMPORTANT: the reference labels '
            'are heavily concentrated in finals (305/341); a stage-aware improvement can learn reporting '
            'propensity, not better physical incident detection. This is a bias diagnostic and exploratory '
            'candidate, not validation that unlabeled qualifying alarms are false or safe to suppress.', '']
    if args.calibration == 'grouped':
        lines[2:2] = ['Calibration uses the first fixed-seed four-fold stratified group partition of '
            'outer-training rounds (three folds fit, one calibration), spanning available training seasons. '
            'Whole rounds remain disjoint. Stratification uses outer-training labels only; the outer '
            'test year never enters model/threshold selection. The original baseline is retrained on '
            'the new fit partition: frozen estimators are not reused on overlapping calibration rows. '
            'History still uses fit rows only. This policy differs from earlier latest-year calibration; '
            'comparisons are exploratory, not proof of independent improvement.', '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Objective experiment: {split}', '',
            table(['Model', 'Target', 'Hits', 'Positives', 'Alarms', 'Recall'],
                [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms'], pct(r['recall']))
                 for r in rows if r['split'] == split]), '']
    lines += ['Goal met: **'+str(manifest['goal_passed'])+'**. No model is promoted. '
              'No production source changed. No precision, FPR or physical-crash-probability claim is possible '
              'from this positive-only reference. Scores, per-year/category/status counts and event-cluster '
              f'recall intervals are in `artifacts/incident-ml-{study}/`.', '']
    (out/f'{study}.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(pooled, indent=2), flush=True)


if __name__ == '__main__':
    main()
