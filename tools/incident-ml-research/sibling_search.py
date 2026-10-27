"""Post-round sibling-run context, with phase/support-matched identity placebo."""
import argparse
import json
import pickle
from pathlib import Path
import numpy as np
from sibling_profiles import references, placebo, features as sibling_features
from data import load_dataset, sha
from objectives import calibration_partition, train, score
from paired_sessions import prepare as prepare_pairs
from environment import prepare as prepare_environment
from experiment import evaluation_splits, threshold_for_budget, metrics, write_csv, cluster_interval
from conditional import goal_passed
from target import BUDGETS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--documents', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/incident-ml-siblings'))
    args = parser.parse_args()
    root, out = Path(__file__).resolve().parents[2], args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    x, meta, sector_data, hashes = load_dataset(root, args.documents,
        root / 'tools/racetrace-api.PodiumBacktest/Fixtures/incidents-2026.csv')
    course_by_key = {}
    for year in range(2021, 2027):
        bundle = json.loads((root / f'artifacts/incident-multiseason/{year}/inputs-{year}.json').read_text(encoding='utf-8-sig'))
        for run in bundle['PaceSessions']:
            assert run['CourseKey']
            course_by_key[f"{run['SessionId']}/{run['Run']['RiderId']}"] = str(run['CourseKey'])
    layouts = {}
    for row, sectors in zip(meta, sector_data):
        sid = row['key'].split('/')[0]
        layouts[sid] = max(layouts.get(sid, 0), len(sectors))
    courses = [course_by_key[r['key']] + '/layout-' + str(layouts[r['key'].split('/')[0]]) for r in meta]
    profiles, durations = np.full((len(meta), 5), np.nan), np.zeros((len(meta), 5))
    for i, sectors in enumerate(sector_data):
        for j, sector in enumerate(sectors):
            if sector[12] == 0 and sector[13] == 0:
                profiles[i, j], durations[i, j] = sector[4], sector[11]
    reference, counts = references(profiles, meta, courses)
    swapped, changed = placebo(reference, counts, meta, courses)
    aligned = sibling_features(profiles, reference, counts, durations)
    wrong = sibling_features(profiles, swapped, counts, durations)
    support = np.hstack([counts, np.isfinite(reference).astype(float)])
    np.savez_compressed(out / 'sibling-features.npz', keys=np.array([r['key'] for r in meta]),
        courses=np.array(courses), profiles=profiles, reference=reference, counts=counts,
        placebo_reference=swapped, placebo_changed=changed, aligned=aligned, placebo=wrong, support=support)
    paired, _ = prepare_pairs(root, meta, enhanced=True)
    _, conditions = prepare_environment(root, meta)
    factor_folder = root / 'artifacts/incident-ml-field-factor'
    assert json.loads((factor_folder / 'manifest.json').read_text())['input_hashes'] == hashes
    factor_hash = sha(factor_folder / 'field-factors.npz')
    with np.load(factor_folder / 'field-factors.npz') as pack:
        assert pack['keys'].tolist() == [r['key'] for r in meta]
        factors = [pack[f'configuration-{j}'] for j in range(4)]
    local = np.hstack([np.hstack([np.nan_to_num(f).reshape(len(meta), -1),
                                  (~np.isfinite(f[:, :, 0])).astype(float)]) for f in factors])
    y, years = np.array([r['positive'] for r in meta]), np.array([r['year'] for r in meta])
    baseline = np.array([r['production'] for r in meta])
    rows, pooled, tuning, predictions, calibration_scores, subgroups, selections = ([] for _ in range(7))
    for split, outer, test in evaluation_splits(years):
        fit, cal = calibration_partition(outer, years, meta, y, 'grouped')
        assert not {meta[i]['group'] for i in fit} & {meta[i]['group'] for i in np.concatenate([cal, test])}
        names = sorted({conditions[i] for i in fit})
        weather = np.array([[float(c == name) for name in names] for c in conditions])
        context = np.hstack([x, paired, weather, local])
        selected, models = {}, {}
        views = {'sibling-only': aligned, 'sibling-augmented': np.hstack([context, aligned]),
                 'placebo-augmented': np.hstack([context, wrong]), 'support-only': support,
                 'context-reference': context}
        for name, values in views.items():
            best = None
            for leaves in [7, 15]:
                model = train('all-unlabelled', leaves, values[fit], y[fit], years[fit])
                scores = score(model, values)
                threshold = threshold_for_budget(scores[cal], 700 / 20340)
                objective = float(np.mean(scores[cal][y[cal]] > threshold))
                tuning.append(dict(split=split, model=name, leaves=leaves, calibration_recall=objective))
                if best is None or objective > best[0]:
                    best = objective, scores, leaves
                    models[name] = model
            selected[name] = best
        with (out / f'{split}.pickle').open('wb') as handle:
            pickle.dump(dict(models=models, weather_names=names), handle)
        for name, (objective, scores, leaves) in selected.items():
            control = name in ['placebo-augmented', 'support-only', 'context-reference']
            selections.append(dict(split=split, model=name, leaves=leaves, calibration_objective=objective))
            for budget in BUDGETS:
                threshold = threshold_for_budget(scores[cal], budget)
                flagged = scores[test] > threshold
                rows.append(dict(split=split, model=name, control=control, budget=budget, threshold=threshold,
                    **metrics(y[test], flagged, baseline[test]), recall_interval=cluster_interval(meta, test, flagged)))
                for field in ['category', 'session', 'status']:
                    for value in sorted({str(meta[i][field]) for i in test}):
                        take = np.array([str(meta[i][field]) == value for i in test])
                        subgroups.append(dict(split=split, model=name, budget=budget, field=field, value=value,
                            **metrics(y[test][take], flagged[take], baseline[test][take])))
            predictions.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(scores[i])) for i in test)
            calibration_scores.extend(dict(split=split, model=name, key=meta[i]['key'], score=float(scores[i])) for i in cal)
        write_csv(out / 'metrics.csv', rows)
        write_csv(out / 'tuning.csv', tuning)
        print(f'{split}: post-round sibling models complete', flush=True)
    for name in selected:
        for budget in BUDGETS:
            take = [r for r in rows if r['split'].startswith('leave-') and r['model'] == name and r['budget'] == budget]
            hits, alarms = sum(r['hits'] for r in take), sum(r['alarms'] for r in take)
            pooled.append(dict(model=name, control=take[0]['control'], budget=budget, hits=hits,
                positives=int(y.sum()), alarms=alarms, recall=hits/int(y.sum()),
                goal_passed=goal_passed(hits, int(y.sum()), alarms)))
    for name, content in [('pooled', pooled), ('predictions', predictions), ('calibration-scores', calibration_scores), ('subgroups', subgroups)]:
        write_csv(out / f'{name}.csv', content)
    protected = json.loads((root / 'artifacts/incident-ml/manifest.json').read_text())['protected_source_hashes']
    assert all(sha(root / p) == digest for p, digest in protected.items())
    assert sha(factor_folder / 'field-factors.npz') == factor_hash
    coverage = np.isfinite(reference).any(axis=1)
    manifest = dict(input_hashes=hashes, source_hashes={str(p.relative_to(root)): sha(p) for p in
        [Path(__file__), Path(__file__).with_name('sibling_profiles.py')]}, selections=selections,
        component_source_hashes={str((factor_folder / 'field-factors.npz').relative_to(root)): factor_hash},
        post_round_only=True, uses_later_or_unordered_sessions=True, protected_sources_unchanged=True,
        covered_runs=int(coverage.sum()), covered_positives=int(np.sum(coverage & y)),
        placebo_permutable_runs=int(changed.sum()), goal_passed=any(r['goal_passed'] for r in pooled if not r['control']),
        exploratory_reuse_of_previously_inspected_years=True)
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    from report import table, pct
    lines = ['## Post-round sibling-run profiles', '',
        'This is deliberately post-round retrospective analysis, NOT live detection or a causal pre-session '
        'forecast. All other scored runs of the same rider/round/course/category/sector layout may supply '
        'a median normalized sector profile, including later or unordered sessions. No same-day ordering '
        'is invented. Same-session/self references are excluded. Whole rounds remain inside their held '
        'season: labels never enter profile construction or cross fit/calibration/test round boundaries. '
        'No official finish positions/status, identities or notes become prediction columns.', '',
        'Raw and centered profile changes, diagnostic seconds and support are used together, retaining '
        'multi-sector slowdown instead of subtracting it from every feature. Missing references stay '
        'unknown with explicit support. The placebo substitutes a different rider within the current '
        'session, same round/course/category and exact reference-count/missingness pattern. References '
        'that cannot be swapped stay unchanged; permutation coverage is exported. Support-only and '
        'unchanged factor/context are separate controls. Models compare leaves 7/15 on calibration only, '
        'then all five budgets/eight held-year/forward tests. No test quota. This purpose is more permissive '
        'about measurement availability than earlier-session models; gains cannot be sold as live gains.', '',
        f"Reference coverage: {manifest['covered_runs']} runs / {manifest['covered_positives']} original positives; "
        f"placebo can swap {manifest['placebo_permutable_runs']} rows.", '',
        table(['Model', 'Control', 'Target', 'Hits / 341', 'Alarms', 'Recall'],
            [(r['model'], r['control'], pct(r['budget']), r['hits'], r['alarms'], pct(r['recall'])) for r in pooled]), '']
    for split in ['chronological-2025', 'chronological-2026']:
        lines += [f'### Post-round sibling model: {split}', '', table(['Model', 'Target', 'Hits', 'Positives', 'Alarms'],
            [(r['model'], pct(r['budget']), r['hits'], r['positives'], r['alarms']) for r in rows if r['split'] == split]), '']
    lines += ['U remains unverified; these retrospective tests cannot identify crash probabilities or '
              'precision. All artifacts remain in `artifacts/incident-ml-siblings/`; production is unchanged.', '']
    (out / 'siblings.md').write_text('\n'.join(lines), encoding='utf-8')
    eligible = [r for r in pooled if not r['control'] and r['alarms'] <= 700]
    print(json.dumps(dict(best_candidate=max(eligible, key=lambda r: r['hits']) if eligible else None)), flush=True)


if __name__ == '__main__':
    main()
