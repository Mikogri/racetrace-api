"""Render measured artifacts, never invented benchmark rows."""
import csv
import json
from pathlib import Path
import hashlib
from collections import defaultdict

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'artifacts/incident-ml'


def read_csv(name):
    return list(csv.DictReader((OUT/name).open(encoding='utf-8')))


def pct(value):
    return f'{100*float(value):.2f}%'


def table(headers, rows):
    return '\n'.join(['| '+' | '.join(headers)+' |', '| '+' | '.join(['---']*len(headers))+' |']+
                     ['| '+' | '.join(str(v).replace('|','/') for v in row)+' |' for row in rows])


def main():
    manifest = json.loads((OUT/'manifest.json').read_text(encoding='utf-8'))
    metrics, pooled, groups, loc = map(read_csv, ['metrics.csv','pooled.csv','subgroups.csv','localization.csv'])
    audit = json.loads((OUT/'input-audit.json').read_text(encoding='utf-8'))
    lines = ['# RaceTrace incident ML research — measured offline results', '',
             '## Decision', '',
             '**Continue ML research; do not deploy yet.** The learned PU ranking improves known-incident recall at comparable alarm rates. '
             'This does not establish a lower false-positive rate: a representative negative test set is absent. '
             'Forward 2026 performance is weaker than 2025; the ONNX conversion also fails native-score parity. '
             'No production calculator, database, API contract or frontend was changed by this research.', '',
             '## Data and audit', '',
             f"Actual corpus: **{manifest['run_count']:,} runs**, **{manifest['positive_count']} distinct matched positive runs**, "
             f"411 source observations; **{manifest['explicit_sector_runs']} runs with explicit sector labels**. Repeated observations count once. "
             'Training/ambiguous sessions and unavailable DNS/DSQ results follow the existing C# matching exclusions. '
             'The lists are incomplete and opportunistically reported; unlabelled runs are unknown, never verified clean examples.', '',
             'Source documents are external; descriptions/health narratives/URLs are not copied into the dataset. '
             'Only explicit sector numbers are extracted for localization evaluation, not all sectors of a positive run. '
             'No new database/provider requests were made. Input hashes and protected C# source hashes are in the manifest.', '',
             table(['Input check','Observed'], sorted(audit.items())), '',
             'The 690 representation differences are trailing missing-checkpoint padding, not different measured times. '
             'The corpus includes 50 runs without sector entries. Complete measured-sector sums agree with official finish times '
             'within 10 ms for all 19,554 checked runs: these are sector durations, not cumulative splits. '
             '13,250 runs have different reference-field counts across sectors; this uncertainty is represented in features, '
             'not silently treated as an unchanged cohort. Eight runs have the research compensating-checkpoint quality feature. '
             'A missing timing is not evidence of a specific crash location.', '',
             '## Models, features and validation', '',
             'Seventy-one features summarize sector timing relative to its field, leave-one-sector-out median expectations, '
             'absolute/relative/log losses, robust deviation, rank contrast, neighbour contrast, recovery, field size/spread, '
             'sample-size changes, missing data and checkpoint quality. Expected times are diagnostic references, '
             'not validated counterfactual corrected race times. Identities, season, venue, team, category, final rank/time '
             'and DNF status are not in the feature matrix.', '',
             'Three deterministic case-control PU bags are fitted per logistic/LightGBM/XGBoost model. Each bag contains '
             'observed positives and a subsample of the contaminated unlabelled population. This is a genuine learned '
             'observed-label ranking model, not a calibrated crash classifier or an unbiased PU-risk estimator. '
             'Scores must not be presented as crash probabilities. Reporting is not assumed random (SCAR). '
             'The hybrid is 0.85 × LightGBM score + 0.15 × original C# flag; its weights were fixed before evaluation.', '',
             'Six leave-whole-season-out tests and two forward tests were actually run. In each training partition, the '
             'latest available year is reserved for calibration and selection between two hyperparameter settings; estimator '
             'fitting excludes that calibration year. Forward 2025 therefore fits 2021–2023, calibrates on 2024 and tests 2025. '
             'Forward 2026 fits 2021–2024, calibrates on 2025 and tests 2026. All categories/sessions of a round stay together. '
             'Leave-season-out is transfer testing, not always chronological forecasting: later years can train an earlier-year fold.', '',
             'Thresholds target each requested budget on calibration data only; strict > handles ties conservatively. '
             'Actual held-out alarm rates are shown and are not retrospectively forced to the target. '
             'Field summaries and later sectors are available after the session; this is retrospective assessment, not live prediction. '
             'All these years influenced earlier detector/feature development; none is a pristine untouched external benchmark. '
             'The same rider can recur between years, though no rider identifier is a predictor.', '',
             '## Fixed C# classifiers at their original operating points', '',
             table(['Classifier','Known hits / 341','Recall','Alarms / 20,340','Alarm rate'],
                   [(name, f"{r['hits']}/341",pct(r['recall']),r['alarms'],pct(r['alarm_rate']))
                    for name,r in manifest['fixed_baselines'].items()]), '',
             '`production` is the original frozen 205/341 baseline. `current` is the local conservative 216/341 variant '
             'that existed before this ML task, not a claim about the deployed server. `high_recall` is the archived 298/341 experiment. '
             'These C# implementations were not changed or retrained by this experiment.', '',
             '## All budgets — pooled leave-season-out predictions', '',
             'Every run contributes exactly once to this pooled table. C# budget curves prioritize their original flag, '
             'then diagnostic deviation; they are explicitly **reranked diagnostic baselines**, not the fixed classifiers above. '
             'Archived high-recall diagnostics omit normal sectors, so some ties/budgets cannot be attained exactly.', '',
             table(['Model','Calibration target','Known hits / 341','Recall','Actual alarms','Actual alarm rate'],
                   [(r['model'],pct(r['budget']),f"{r['hits']}/341",pct(r['recall']),r['alarms'],pct(r['alarm_rate'])) for r in pooled]), '',
             '![Recall versus actual alarm rate](recall-alarm.svg)', '',
             'LightGBM reaches 80.94% known-positive recall at 10.38% actual alarms and 86.51% at 12.83%. '
             'At target 15%, it reaches 89.44% at 14.95% actual alarms. This satisfies the exploratory aggregate goal, '
             'but is not proof that the same trade-off will hold in a new season or that the extra alarms are correct.', '']
    target_manifest=ROOT/'artifacts/incident-ml-target/manifest.json'
    if target_manifest.exists():
        accepted=json.loads(target_manifest.read_text(encoding='utf-8'))
        candidates=list(csv.DictReader((target_manifest.parent/'pooled.csv').open(encoding='utf-8')))
        eligible=[r for r in candidates if int(r['alarms'])<=700]
        best=max(eligible,key=lambda r:int(r['hits'])) if eligible else None
        position=lines.index('## Data and audit')
        lines[position:position]=['## Initial strict-target experiment (historical)', '',
            '**In the initial conditional-timing experiment, the target of at least 307/341 known hits with at most 700 actual alarms was '+
            ('met numerically in this exploratory run.' if accepted['goal_passed'] else 'NOT achieved.')+'** '
            +(f"Within that experiment, the highest observed recall among its tested operating points under the cap was {best['hits']}/341 "
              f"with {best['alarms']} alarms ({best['model']}). " if best else 'No evaluated point was under the alarm cap. ')+
            'Matched-peer references, learned conditional sector-time reconstruction and their combination were actually '
            'trained and tested. These do not establish a solution to the stricter requirement. The original results below '
            'are retained for transparency; they must not be presented as meeting the newly agreed goal. '
            'The full acceptance experiment is appended at the end of this report.', '']
    for split in ['chronological-2025','chronological-2026']:
        selected = [r for r in metrics if r['split']==split]
        lines += [f'## Forward test: {split}', '',
                  table(['Model','Target','Hits / labelled','Recall','Alarms','Actual rate','Added / lost known hits vs fixed original C#','New alarms / added known hit'],
                        [(r['model'],pct(r['budget']),f"{r['hits']}/{r['positives']}",pct(r['recall']),r['alarms'],pct(r['alarm_rate']),
                          f"+{r['additional_hits']} / −{r['lost_hits']}",r['new_alarms_per_added_hit'] or 'undefined') for r in selected]), '',
                  'Added-hit counts exclude known hits already found by fixed original C#. New alarms include unverified runs '
                  'and recovered known positives; lost hits are shown separately. The ratio is not a precision estimate.', '']
    lines += ['## Seasonal stability: LightGBM', '',
              table(['Season','Labelled','Hits at target 10%','Actual alarm rate','Hits at target 15%','Actual alarm rate'],
                    [(year, (a:=next(r for r in metrics if r['split']==f'leave-{year}-out' and r['model']=='lightgbm' and r['budget']=='0.1'))['positives'],
                      a['hits'],pct(a['alarm_rate']),
                      (b:=next(r for r in metrics if r['split']==f'leave-{year}-out' and r['model']=='lightgbm' and r['budget']=='0.15'))['hits'],pct(b['alarm_rate']))
                     for year in range(2021,2027)]), '',
              'For forward 2026 at target 15%, observed-positive recall has event-cluster bootstrap 95% interval '
              + '–'.join(pct(next(r for r in metrics if r['split']=='chronological-2026' and r['model']=='lightgbm' and r['budget']=='0.15')[key])
                         for key in ['recall_ci_low','recall_ci_high'])+'. '
              'Intervals condition on fitted models and observed labels; they do not include label-selection or training uncertainty. '
              'For perfect recall, the bootstrap may degenerate to [100%,100%]; that is not certainty about future events.', '',
              '## Categories and DNF: LightGBM at target 15%', '']
    grouped = defaultdict(lambda:[0,0,0,0])
    for r in groups:
        if r['split'].startswith('leave-') and r['model']=='lightgbm' and r['budget']=='0.15':
            values=grouped[(r['field'],r['value'])]
            for j,k in enumerate(['hits','positives','alarms','runs']): values[j]+=int(r[k])
    lines += [table(['Group','Value','Known hits / labelled','Recall','Alarms / runs','Alarm rate'],
                    [(k[0],k[1],f'{v[0]}/{v[1]}',pct(v[0]/v[1]) if v[1] else 'unknown',f'{v[2]}/{v[3]}',pct(v[2]/v[3])) for k,v in sorted(grouped.items())]), '',
              'All models/budgets/category/status breakdowns are in `subgroups.csv`; full per-season results are in `metrics.csv`. '
              'DNF is an evaluation subgroup only, never a model input. Missing-only runs cannot reliably localize an incident.', '',
              '## Sector localization — separate diagnostic task', '',
              'The run classifier does not invent sector training labels. A separate robust residual ranking orders candidate '
              'sectors; missing-only or compensating-checkpoint cases can remain unknown. This is not a trained sector classifier. '
              'The top candidate is evaluated only where a source explicitly identifies a sector. Correct run detection '
              'does not prove correct localization or a correct removable time estimate.', '',
              table(['Model, target 15%','Explicit-sector runs','Detected','Exact end-to-end','Adjacent end-to-end','Exact given detection'],
                    [(model, (n:=sum(int(r['sector_labelled_runs']) for r in loc if r['split'].startswith('leave-') and r['model']==model and r['budget']=='0.15')),
                      (d:=sum(int(r['detected_with_sector_label']) for r in loc if r['split'].startswith('leave-') and r['model']==model and r['budget']=='0.15')),
                      f"{(e:=sum(int(r['exact_hits']) for r in loc if r['split'].startswith('leave-') and r['model']==model and r['budget']=='0.15'))}/{n}",
                      f"{sum(int(r['adjacent_hits']) for r in loc if r['split'].startswith('leave-') and r['model']==model and r['budget']=='0.15')}/{n}",
                      pct(e/d) if d else 'unknown') for model in ['logistic','lightgbm','xgboost','hybrid']]), '',
              'The conditional sample is very small and selected; these numbers cannot validate general sector accuracy.', '',
              '## Error analysis', '',
              'The full forward-2026 review is in `error-analysis.json`: actual and model-reference times, all sector features, '
              'scores, C# flags, candidate sectors and native LightGBM feature contributions. Contributions are mean bag '
              'logit contributions, not causes or calibrated probability explanations. New unlabelled alarms are **not false positives**.', '']
    errors=json.loads((OUT/'error-analysis.json').read_text(encoding='utf-8'))
    for kind in ['missed-labelled','new-alarm-unverified']:
        lines += [f'### {kind}', '', table(['Rider / venue / session','Actual sector seconds','Reference seconds','Score','Top sector','Largest model contribution'],
            [(f"{r['rider']} / {r['venue']} / {r['session']}",
              ', '.join('—' if v is None else f'{v:.3f}' for v in r['times']),
              ', '.join('—' if v is None else f'{v:.3f}' for v in r['predicted_times']),
              f"{r['score']:.4f}",r['predicted_sector'],r['top_contributions'][0]['feature'])
             for r in sorted([v for v in errors if v['kind']==kind],key=lambda v:-v['score'])[:10]]), '']
    lines += ['These rows include local time spikes, broadly slow runs, terminal losses and incomplete DNF data. '
              'Checkpoint features and small reference fields are explicitly available for review; the model is not authorized '
              'to repair those data or infer a physical crash from them. Neither names nor these reviewed examples are used '
              'as special runtime rules. No post-test retuning was performed to rescue individual examples.', '',
              '### Previously discussed cases — held-out-season predictions', '',
              'These identities select review rows only; they are never model inputs or exceptions in training/inference. '
              'Both Pierron examples are detected, but Goldstone remains missed through the 15% calibration target. '
              'Aggregate improvement does not mean all previously identified weaknesses are solved.', '']
    predictions={(r['split'],r['model'],r['key']):float(r['score']) for r in read_csv('predictions.csv')}
    cases=[('Amaury Pierron / Les Gets 2025 Final','leave-2025-out','103/15'),
           ('Amaury Pierron / Lake Placid 2025 Final','leave-2025-out','75/15'),
           ('Jackson Goldstone / Loudenvielle 2023 Quali','leave-2023-out','305/17')]
    case_rows=[]
    for label,split,key in cases:
        score=predictions[split,'lightgbm',key]
        decisions=[]
        for budget in ['0.1','0.125','0.15']:
            threshold=float(next(r['threshold'] for r in metrics if r['split']==split and r['model']=='lightgbm' and r['budget']==budget))
            decisions.append('detected' if score>threshold else 'missed')
        case_rows.append([label,f'{score:.4f}']+decisions)
    lines += [table(['Case','Score (not probability)','Target 10%','Target 12.5%','Target 15%'],case_rows), '',
              '## Cost, portability and reproducibility', '',
              f"One full experiment run took {manifest['elapsed_seconds']:.2f} seconds including input loading, "
              'feature extraction, model selection, eight outer tests and artifact export on '
              f"{manifest['cpu']} / {manifest['platform']}, Python {manifest['python']}. CPU only, two model threads. "
              'This is not an API/database latency measurement.', '',
              table(['Forward-2026 ensemble','Runs','Python prediction seconds (excludes features/I/O)'],
                    [(r['model'],r['predict_runs'],f"{r['seconds']:.6f}") for r in manifest['timing'] if r['split']=='chronological-2026']), '']
    if (OUT/'onnx-parity.json').exists():
        onnx=json.loads((OUT/'onnx-parity.json').read_text())
        cs=json.loads((OUT/'csharp-onnx-parity.json').read_text()) if (OUT/'csharp-onnx-parity.json').exists() else {}
        lines += [f"ONNX conversion (onnxmltools 1.14.0, ONNX Runtime 1.23.1 CPU) was actually attempted: "
                  f"native/ONNX maximum score difference **{onnx['max_absolute_score_difference']:.8f}** over {onnx['runs']} runs; "
                  f"{onnx['changed_runs_over_tolerance']} runs exceed the 2e-5 tolerance. **Native parity failed.** "
                  'The converter also emits fixed-label-output shape warnings on batches. The export is diagnostic only; '
                  'do not treat it as production-compatible or increase tolerance just to make the check pass.', '',
                  f"The separate C# console tested {cs.get('Runs','not run')} feature rows including the worst conversion case. "
                  f"Python ONNX vs C# ONNX maximum difference: {cs.get('PythonOnnxVsCSharpOnnxDifference','unknown')}. "
                  f"Native Python vs C# ONNX difference: {cs.get('PythonNativeVsCSharpOnnxDifference','unknown')}. "
                  'The runtime boundary works, but conversion fidelity does not meet the chosen requirement. '
                  'C# feature construction has not been ported: this demo consumes the identical Python feature matrix. '
                  'A later alternative is a corrected converter / native LightGBM C API, with feature and decision parity tests; '
                  'none was added to production.', '']
    lines += ['See `README.md` for exact commands, dependency pins, files and availability assumptions; '
              '`LICENSES.md` and the generated installed-license inventory for library and native runtime obligations. '
              'Raw data/source rights are separate from permissive library licenses.', '',
              '## Limitations and recommendation', '',
              '- Observed positives are incomplete, non-random and likely biased toward prominent riders/events. '
              'The small purposefully selected human-reviewed negative sample is not a representative clean test set.',
              '- No precision, specificity, FPR or calibrated crash probability can be inferred from this experiment.',
              '- Run-level labels do not establish exact incident sectors; only 30 runs support explicit-sector validation.',
              '- Retrospective finalized-field evaluation is not a live-timing or pre-race deployment claim.',
              '- Seasonal distribution shift is substantial: forward 2025 is much easier than 2026, and calibration targets can be exceeded.',
              '- Models trained on overlapping years are not statistically independent; clustered intervals are conditional and exploratory.',
              '- ONNX native-score parity failed. The runtime demonstration does not make the model deployment-ready.',
              '',
              '**Recommendation: develop further, not deploy.** Prioritize blind adjudication of stratified model alarms and '
              'matched unlabelled controls (including uniform slow, terminal, small-field and checkpoint cases), independently '
              'label incident sectors, and validate a genuinely new event/season. Then lock a model/threshold, verify '
              'feature/inference parity in C#, and only separately evaluate effects of any time correction on Pace/Consistency/Rating.', '']
    path=Path(__file__).parent/'report.md'
    (path.parent/'recall-alarm.svg').write_bytes((OUT/'recall-alarm.svg').read_bytes())
    repeat=ROOT/'artifacts/incident-ml-repeat'
    checks={}
    for filename in ['predictions.csv','metrics.csv','pooled.csv','subgroups.csv','localization.csv','error-analysis.json']:
        if (repeat/filename).exists():
            a=hashlib.sha256((OUT/filename).read_bytes()).hexdigest()
            b=hashlib.sha256((repeat/filename).read_bytes()).hexdigest()
            checks[filename]=dict(identical=a==b,sha256=a)
    source_unchanged=all(hashlib.sha256((ROOT/p).read_bytes()).hexdigest()==digest
                         for p,digest in manifest['protected_source_hashes'].items())
    if not source_unchanged or any(not v['identical'] for v in checks.values()):
        raise ValueError('Production source integrity or deterministic-repeat check failed')
    (OUT/'verification.json').write_text(json.dumps(dict(protected_sources_unchanged=source_unchanged,repeat_checks=checks),indent=2),encoding='utf-8')
    if checks:
        lines += ['## Verification', '',
                  'A second independent full training/evaluation run produced byte-identical predictions.csv, metrics.csv, '
                  'pooled.csv, subgroups.csv, localization.csv and error-analysis.json. Protected API/test C# sources match '
                  'the start-of-experiment hashes. Existing dirty edits from earlier work were retained, not reverted. '
                  'No commit, push, database mutation or deployment was performed.', '']
    followup=ROOT/'artifacts/incident-ml-focus/followup.md'
    if followup.exists():
        lines += [followup.read_text(encoding='utf-8')]
    target_report=ROOT/'artifacts/incident-ml-target/target.md'
    if target_report.exists():
        lines += [target_report.read_text(encoding='utf-8')]
    objective_report=ROOT/'artifacts/incident-ml-objectives/objectives.md'
    if objective_report.exists():
        lines += [objective_report.read_text(encoding='utf-8')]
    trajectory_report=ROOT/'artifacts/incident-ml-trajectory/trajectory.md'
    if trajectory_report.exists():
        lines += [trajectory_report.read_text(encoding='utf-8')]
        trajectory=trajectory_report.parent
        trajectory_repeat=ROOT/'artifacts/incident-ml-trajectory-repeat'
        if (trajectory_repeat/'manifest.json').exists():
            repeat_checks={name: hashlib.sha256((trajectory/name).read_bytes()).hexdigest() ==
                                hashlib.sha256((trajectory_repeat/name).read_bytes()).hexdigest()
                           for name in ['predictions.csv','metrics.csv','pooled.csv','subgroups.csv','shape-diagnostics.csv']}
            def prediction_map(folder):
                result=defaultdict(dict)
                for row in csv.DictReader((folder/'predictions.csv').open(encoding='utf-8')):
                    result[(row['split'],row['model'])][row['key']]=float(row['score'])
                return result
            first,second=prediction_map(trajectory),prediction_map(trajectory_repeat)
            if first.keys()!=second.keys() or any(first[k].keys()!=second[k].keys() for k in first):
                raise ValueError('Trajectory repeat changed evaluated run identities')
            maximum_difference=max(abs(first[k][key]-second[k][key]) for k in first for key in first[k])
            first_metrics=list(csv.DictReader((trajectory/'metrics.csv').open(encoding='utf-8')))
            second_metrics=list(csv.DictReader((trajectory_repeat/'metrics.csv').open(encoding='utf-8')))
            unchanged_metrics=len(first_metrics)==len(second_metrics) and all(
                {k:v for k,v in a.items() if k!='threshold'}=={k:v for k,v in b.items() if k!='threshold'}
                for a,b in zip(first_metrics,second_metrics))
            changed_decisions=0
            changed_thresholds=sum(a['threshold']!=b['threshold'] for a,b in zip(first_metrics,second_metrics))
            for a,b in zip(first_metrics,second_metrics):
                group=(a['split'],a['model'])
                changed_decisions+=sum((first[group][key]>float(a['threshold'])) !=
                                       (second[group][key]>float(b['threshold'])) for key in first[group])
            if changed_decisions or not unchanged_metrics or not all(repeat_checks[k] for k in
                    ['pooled.csv','subgroups.csv','shape-diagnostics.csv']):
                raise ValueError('Trajectory repeat changed alarm decisions or evaluation counts')
            repeat_checks.update(maximum_score_difference=maximum_difference,
                                 changed_alarm_decisions=changed_decisions,
                                 nonthreshold_metrics_identical=unchanged_metrics)
            (trajectory/'verification.json').write_text(json.dumps(repeat_checks,indent=2),encoding='utf-8')
            lines += ['### Run-shape reproducibility', '',
                      'An independent second full training/evaluation run produced identical alarm decisions at '
                      'all 160 evaluated operating points, with byte-identical pooled counts, subgroup counts and '
                      'shape diagnostics. Prediction CSV bytes are not identical: maximum score difference '
                      f'is {maximum_difference:.17g}; {changed_thresholds} threshold values also differ. '
                      'This verifies decision/count repeatability on frozen inputs, not bitwise score identity, '
                      'external validity or correctness of incident labels.', '']
    for folder,filename in [('incident-ml-multiple-instance','multiple-instance.md'),('incident-ml-kernels','kernels.md'),
                            ('incident-ml-residual','residual.md'),('incident-ml-grouped-history','trajectory.md'),
                            ('incident-ml-grouped-residual','residual.md'),('incident-ml-stage-history','trajectory.md'),
                            ('incident-ml-stage-residual','residual.md'),('incident-ml-nnpu','nnpu.md'),
                            ('incident-ml-stacking','stacking.md'),('incident-ml-checkpoints','dynamics.md'),
                            ('incident-ml-paired','paired.md'),('incident-ml-paired-enhanced','paired.md'),
                            ('incident-ml-paired-placebo','paired.md'),('incident-ml-hybrid-grouped','hybrid.md'),
                            ('incident-ml-hybrid-latest','hybrid.md'),('incident-ml-environment-latest','environment.md'),
                            ('incident-ml-environment-grouped','environment.md'),('incident-ml-wide','wide.md'),
                            ('incident-ml-sparse-delay','sparse-delay.md'),
                            ('incident-ml-field-objectives','field-objectives.md'),
                            ('incident-ml-field-objectives-enhanced','field-objectives.md'),
                            ('incident-ml-aligned','aligned.md'),
                            ('incident-ml-unsupervised','unsupervised.md'),
                            ('incident-ml-local-density','local-density.md'),
                            ('incident-ml-nested-selection','nested-selection.md'),
                            ('incident-ml-reviewed','reviewed.md'),
                            ('incident-ml-reviewed-sectors','reviewed-sectors.md'),
                            ('incident-ml-field-factor','field-factor.md'),
                            ('incident-ml-factor-blend','factor-blend.md'),
                            ('incident-ml-siblings','siblings.md'),
                            ('incident-ml-finish-change','finish-change.md'),
                            ('incident-ml-rolling-history','rolling-history.md'),
                            ('incident-ml-rank-history','rank-history.md'),
                            ('incident-ml-density-ratio','density-ratio.md'),
                            ('incident-ml-sector-sequence','sector-sequence.md'),
                            ('incident-ml-field-noise','field-noise.md'),
                            ('incident-ml-tail-ranking','tail-ranking.md'),
                            ('incident-ml-tail-ranking-pruned','tail-ranking-pruned.md'),
                            ('incident-ml-monotone','monotone.md'),
                            ('incident-ml-reviewed-history','reviewed-history.md'),
                            ('incident-ml-specialists','specialists.md'),
                            ('incident-ml-gated-specialists','gated-specialists.md'),
                            ('incident-ml-label-efficiency','label-efficiency.md'),
                            ('incident-ml-event-robust','event-robust.md'),
                            ('incident-ml-subbagging','subbagging.md'),
                            ('incident-ml-field-score','field-score.md'),
                            ('incident-ml-additive','additive.md'),
                            ('incident-ml-bounded','bounded.md'),
                            ('incident-deployment','deployment.md')]:
        search_report=ROOT/'artifacts'/folder/filename
        if search_report.exists():
            lines += [search_report.read_text(encoding='utf-8')]
            if folder=='incident-ml-rank-history':
                operating=list(csv.DictReader((search_report.parent/'pooled.csv').open(encoding='utf-8')))
                point={r['model']:r for r in operating if abs(float(r['budget'])-.03)<1e-12}
                joint,placebo=point['time-rank-joint'],point['rank-placebo']
                lines += ['### Added rank-history alignment control', '',
                    f"At the predeclared 3% calibration budget, joint time/rank history detects "
                    f"{joint['hits']}/341 at {joint['alarms']} alarms; the rank-only reference swap "
                    f"detects {placebo['hits']}/341 at {placebo['alarms']}. This does not establish "
                    'an extra personal-rank alignment benefit. Past-ability-only and support-only '
                    'controls are reported separately; neither is promoted as an incident detector. '
                    'Within this rank-history comparison, neither ablation improves the earlier '
                    'time-history operating point under the cap.', '']
            if folder=='incident-ml-reviewed-history':
                auxiliary_path=search_report.parent/'held-review-audit.json'
                if auxiliary_path.exists():
                    auxiliary=json.loads(auxiliary_path.read_text(encoding='utf-8'))
                    assert auxiliary['original_primary_positive_count']==341
                    assert auxiliary['primary_benchmark_unchanged']
                    assert auxiliary['source_hash']==hashlib.sha256(
                        (ROOT/'tools/incident-ml-research/held_review_audit.py').read_bytes()).hexdigest()
                    assert all(hashlib.sha256((search_report.parent/name).read_bytes()).hexdigest()==digest
                               for name,digest in auxiliary['primary_file_hashes'].items())
                    lines += ['### Held human reviews: auxiliary outcomes only', '',
                        'Each review contributes only in its held year; these answers did not supervise '
                        'that fold or select its model/threshold. Added human-positive cases outside the '
                        'original 341 are shown separately, not inserted into the primary denominator. '
                        'Disputed original positives are also kept separate. Purposeful case selection '
                        'does not identify population precision or FPR.', '',
                        table(['Model','Target','Additional human-positive alerts / reviews',
                               'Alerts on human No / reviews','Alerts on disputed original P / reviews'],
                            [(r['model'],pct(r['budget']),
                              f"{r['added_positive_alerts']}/{r['added_positive_reviews']}",
                              f"{r['negative_alerts']}/{r['negative_reviews']}",
                              f"{r['disputed_alerts']}/{r['disputed_original_positives']}")
                             for r in auxiliary['pooled']]), '',
                        'At the predeclared 3% point, the label-quarantine control adds one original '
                        'benchmark hit but loses one additional human-positive hit versus the frozen '
                        'reference. This is not evidence of a general detection breakthrough. The '
                        'chronological 2026 check also loses two original hits while reducing alarms.', '']
            if folder=='incident-ml-label-efficiency':
                curves=list(csv.DictReader((search_report.parent/'metrics.csv').open(encoding='utf-8')))
                full=[r for r in curves if r['split'].startswith('leave-') and r['model']=='full-reference'
                      and abs(float(r['budget'])-.03)<1e-12]
                fit_values=[float(r['fit_positive_recall']) for r in full]
                held_values=[float(r['recall']) for r in full]
                points=list(csv.DictReader((search_report.parent/'pooled.csv').open(encoding='utf-8')))
                primary=next(r for r in points if r['model']=='full-reference' and abs(float(r['budget'])-.03)<1e-12)
                reduced=[r for r in points if r['model']!='full-reference' and abs(float(r['budget'])-.03)<1e-12]
                lines += ['### Label-efficiency observations, not a new detector selection', '',
                    f"At the fixed 3% calibration policy, full-label fitting observed-positive recall spans "
                    f"{pct(min(fit_values))}–{pct(max(fit_values))} across leave-year folds; held-year "
                    f"recall spans {pct(min(held_values))}–{pct(max(held_values))}. This is consistent with "
                    'limited transfer/overfitting, but does not isolate its cause from reporting or season shift.', '',
                    f"The full reference detects {primary['hits']}/341 at {primary['alarms']} alarms. "
                    f"Reduced-label arms detect {min(int(r['hits']) for r in reduced)}–"
                    f"{max(int(r['hits']) for r in reduced)}/341 at "
                    f"{min(int(r['alarms']) for r in reduced)}–{max(int(r['alarms']) for r in reduced)} alarms. "
                    'Actual alarm counts differ, so this is not a matched-alarm-rate superiority test. '
                    'These observations do not justify extrapolating that more labels will attain 307 hits.', '',
                    'Fit counts are operations, not independent replications. With only 2021–2026 in the '
                    'frozen corpus, leave-2026-out and chronological-2026 share the same outer train/test '
                    'split. Forward checks are shown separately and are not added into pooled coverage.', '']
            if folder=='incident-ml-field-factor':
                factor_metrics=list(csv.DictReader((search_report.parent/'metrics.csv').open(encoding='utf-8')))
                for split in ['chronological-2025','chronological-2026']:
                    lines += [f'### Field-factor forward test: {split}', '',
                        table(['Model','Target','Hits','Positives','Alarms'],
                            [(r['model'],pct(r['budget']),r['hits'],r['positives'],r['alarms'])
                             for r in factor_metrics if r['split']==split]), '']
            if folder=='incident-ml-factor-blend':
                differences=list(csv.DictReader((search_report.parent/'component-disagreement.csv').open(encoding='utf-8')))
                selected=[r for r in differences if r['split'].startswith('leave-') and
                          abs(float(r['budget'])-700/20340)<1e-12]
                totals={field:sum(int(r[field]) for r in selected) for field in
                        ['both','factor_only','rank_only','known_both','known_factor_only','known_rank_only','known_neither']}
                union_hits=totals['known_both']+totals['known_factor_only']+totals['known_rank_only']
                union_alarms=totals['both']+totals['factor_only']+totals['rank_only']
                lines += ['### Shared misses at fixed component thresholds', '',
                    f"At the existing 700/20,340 calibration budget, the two components share "
                    f"{totals['known_both']} known hits; factor alone adds {totals['known_factor_only']} "
                    f"and rank alone adds {totals['known_rank_only']}. Both miss {totals['known_neither']}. "
                    f"Their plain union would flag {union_alarms} runs for {union_hits}/341 known hits, "
                    'so it exceeds the alarm cap and is not an accepted detector. This is a diagnostic '
                    'at those fixed thresholds, not an information-theoretic bound on all combinations. '
                    'No thresholds are adjusted using these test-region counts.', '']
            if folder=='incident-ml-reviewed-sectors':
                localization=list(csv.DictReader((search_report.parent/'localization.csv').open(encoding='utf-8')))
                summaries=defaultdict(lambda:[0,0])
                for row in localization:
                    if row['split'].startswith('leave-'):
                        total=summaries[row['model'],row['label_source']]
                        total[0]+=int(row['labelled_runs'])
                        total[1]+=int(row['exact'])
                lines += ['### Sector selection is not incident detection', '',
                    table(['Model','Label source','Labelled runs','Exact top-sector choices'],
                          [(model,source,values[0],values[1]) for (model,source),values in sorted(summaries.items())]), '',
                    'These are forced top-sector choices across every labelled run with usable measurements, '
                    'without an incident-detection threshold. They must not be counted as detected incidents. '
                    'The human subset was purposefully sampled, not representative. Explicit fit anchors '
                    'did not improve these held-season localization counts; the tested blend also remains '
                    'below the earlier 228/341 at 699 alarms. Additional localization supervision has not '
                    'resolved separation of ordinary loss from actionable anomalies.', '']
    placebo=ROOT/'artifacts/incident-ml-paired/placebo-comparison.json'
    if placebo.exists():
        comparison=json.loads(placebo.read_text(encoding='utf-8'))
        low,high=comparison['event_bootstrap_recall_difference_interval']
        lines += ['### Personal-profile alignment control', '',
            f"Aligned paired-original: {comparison['aligned_hits']}/341 at {comparison['aligned_alarms']} alarms. "
            f"Wrong-person placebo: {comparison['placebo_hits']}/341 at {comparison['placebo_alarms']} alarms. "
            f"Conditional event-bootstrap recall-difference interval (1,000 replicates): [{pct(low)}, {pct(high)}]. "
            'This does not establish a benefit from correct personal alignment. Availability-only results '
            'are identical across original, enhanced and placebo studies. The placebo is not a proposed '
            'production model. A gain over the original timing model must not be attributed to learned '
            'personal style without stronger evidence.', '']
    scoreboard=[]
    nonstage=[]
    for folder in ['incident-ml-target','incident-ml-objectives','incident-ml-trajectory',
                   'incident-ml-multiple-instance','incident-ml-kernels','incident-ml-residual',
                   'incident-ml-grouped-history','incident-ml-grouped-residual',
                   'incident-ml-stage-history','incident-ml-stage-residual','incident-ml-nnpu',
                   'incident-ml-stacking','incident-ml-checkpoints','incident-ml-paired','incident-ml-paired-enhanced',
                   'incident-ml-hybrid-grouped','incident-ml-hybrid-latest',
                   'incident-ml-environment-latest','incident-ml-environment-grouped','incident-ml-wide',
                   'incident-ml-sparse-delay','incident-ml-field-objectives',
                   'incident-ml-field-objectives-enhanced','incident-ml-aligned','incident-ml-unsupervised',
                   'incident-ml-local-density','incident-ml-nested-selection','incident-ml-reviewed',
                   'incident-ml-reviewed-sectors','incident-ml-field-factor','incident-ml-factor-blend','incident-ml-siblings',
                   'incident-ml-finish-change','incident-ml-rolling-history','incident-ml-rank-history',
                   'incident-ml-density-ratio','incident-ml-sector-sequence','incident-ml-field-noise',
                   'incident-ml-tail-ranking','incident-ml-monotone','incident-ml-reviewed-history','incident-ml-specialists',
                   'incident-ml-gated-specialists','incident-ml-event-robust','incident-ml-subbagging',
                   'incident-ml-field-score','incident-ml-additive','incident-ml-bounded']:
        result=ROOT/'artifacts'/folder/'pooled.csv'
        study_manifest=result.parent/'manifest.json'
        if result.exists() and study_manifest.exists():
            metadata=json.loads(study_manifest.read_text(encoding='utf-8'))
            eligible=[r for r in csv.DictReader(result.open(encoding='utf-8'))
                      if int(r['alarms'])<=700 and
                      (r.get('control','False')!='True' or r['model']=='quarantine-only')]
            if eligible:
                best=max(eligible,key=lambda r:(int(r['hits']),-int(r['alarms'])))
                availability='post-round only' if metadata.get('post_round_only') else 'original availability'
                if metadata.get('derived_finish_changes'):
                    availability='derived finish changes'
                if metadata.get('causal_unlabelled_stream_history'):
                    availability='causal raw-history updates'
                if metadata.get('causal_unlabelled_rank_history'):
                    availability='causal rank/time-history updates'
                scoreboard.append((folder,best['model'],best['hits'],best['alarms'],pct(best['recall']),availability))
                if not metadata.get('stage_context',False) and not metadata.get('post_round_only',False):
                    controls={'original-frozen','reference-grouped','availability-only','availability-weather-only','speed-only','flags-only'}
                    nonstage += [(folder,r['model'],r['hits'],r['alarms'],pct(r['recall'])) for r in eligible
                                 if not r['model'].startswith('stage-') and r['model'] not in controls]
    if scoreboard:
        lines += ['## Exploratory search scoreboard', '',
                  'Highest measured known-positive hit count among the predeclared operating points with '
                  'at most 700 actual alarms in each completed study. This is a descriptive comparison of '
                  'already inspected tests, not an unbiased final-model selection or deployment validation. '
                  'Stage-context studies specifically risk learning reporting propensity: their results '
                  'are not equivalent evidence of physical incident detection.', '',
                  'The label-quarantine control is a usable full prediction pipeline and remains '
                  'eligible for this descriptive scoreboard. It removes disputed labels from fit '
                  'only, preserving all held targets. Diagnostic placebo/support-only controls remain '
                  'excluded; a higher descriptive hit count is not statistical proof of improvement.', '',
                  'Post-round-only studies can use later/unordered sibling measurements and must not be '
                  'represented as session-completion or live detection results.', '',
                  table(['Study','Candidate','Hits / 341','Actual alarms','Recall','Measurement availability'],scoreboard), '']
        best=max(nonstage,key=lambda r:(int(r[2]),-int(r[3]))) if nonstage else None
        achieved=any(int(r[2])>=307 for r in scoreboard)
        status=('A measured point meets the numerical target; deployment/generalization is not established.' if achieved else
                'The tighter target of at least 307/341 at no more than 700 actual alarms is not achieved.')
        summary='**Current search status: '+status+'**'
        if best:
            summary += (f' Best completed measured point without an explicit stage input: **{best[2]}/341 '
                        f'({best[4]}) at {best[3]} alarms**, `{best[0]}/{best[1]}`. '
                        'The absence of an explicit stage column does not imply absence of reporting or availability bias.')
            if best[0]=='incident-ml-finish-change':
                summary += ' This experiment uses derived finish-checkpoint changes after finalized session measurements.'
            if best[1]=='quarantine-only':
                summary += (' This is the label-quarantine control, not a new scoring formula. Its '
                            'one-hit gain over the previous 234/657 point is small; forward checks '
                            'and auxiliary reviewed outcomes do not establish a general improvement.')
        lines[4:4]=[summary,'']
    retraining=ROOT/'artifacts/incident-ml-environment-grouped/selected-retraining-verification.json'
    if retraining.exists():
        checked=json.loads(retraining.read_text(encoding='utf-8'))
        if checked['changed_decisions'] or not checked['input_hashes_match'] or checked['maximum_score_difference']>1e-12:
            raise ValueError('Selected environment-model retraining failed')
        lines += ['## Selected-candidate retraining verification', '',
                  f"All {checked['selected_models_retrained']} selected paired-weather models were retrained "
                  'from their saved parameter choices and frozen inputs. '
                  f"Maximum score difference: {checked['maximum_score_difference']:.17g}; "
                  f"changed alarm decisions across {checked['operating_points']} operating points: "
                  f"{checked['changed_decisions']}. Input hashes match. This verifies selected-model "
                  'training/inference repeatability, not a second independent hyperparameter-search repeat '
                  'or external validity.', '']
    sparse_folder=ROOT/'artifacts/incident-ml-sparse-delay'
    sparse_checks=sparse_folder/'verification.json'
    sparse_retraining=sparse_folder/'selected-retraining-verification.json'
    if sparse_checks.exists() and sparse_retraining.exists():
        counts=json.loads(sparse_checks.read_text(encoding='utf-8'))
        repeated=json.loads(sparse_retraining.read_text(encoding='utf-8'))
        if (not counts['calibration_thresholds_recomputed'] or not counts['production_sources_unchanged']
                or repeated['changed_decisions'] or repeated['maximum_score_difference']>1e-12
                or not repeated['input_hashes_match']):
            raise ValueError('Sparse-delay score/count/retraining verification failed')
        lines += ['## Sparse-delay verification', '',
            f"All {counts['verified_operating_points']} operating points were independently recomputed "
            'from saved test scores and calibration-only thresholds; '
            f"{counts['verified_pooled_points']} pooled points match their six held-year components. "
            f"All {repeated['models_retrained']} selected delay-original models were retrained from "
            'their frozen inputs and chosen recipes, including covariance fitting. Maximum score '
            f"difference: {repeated['maximum_score_difference']:.17g}; changed decisions across "
            f"{repeated['operating_points']} operating points: {repeated['changed_decisions']}. "
            'Input hashes match and protected production source hashes remain unchanged. This verifies '
            'repeatability, not independent generalization or a complete second search.', '']
    for study in ['incident-ml-field-objectives', 'incident-ml-field-objectives-enhanced',
                  'incident-ml-aligned', 'incident-ml-unsupervised', 'incident-ml-local-density',
                  'incident-ml-nested-selection', 'incident-ml-reviewed', 'incident-ml-reviewed-sectors',
                  'incident-ml-field-factor', 'incident-ml-factor-blend', 'incident-ml-siblings',
                  'incident-ml-finish-change', 'incident-ml-rolling-history', 'incident-ml-rank-history',
                  'incident-ml-density-ratio','incident-ml-sector-sequence','incident-ml-field-noise',
                  'incident-ml-tail-ranking','incident-ml-monotone','incident-ml-reviewed-history','incident-ml-specialists',
                  'incident-ml-gated-specialists','incident-ml-label-efficiency','incident-ml-event-robust',
                  'incident-ml-subbagging','incident-ml-field-score','incident-ml-additive',
                  'incident-ml-bounded']:
        folder=ROOT/'artifacts'/study
        verification=folder/'verification.json'
        if verification.exists():
            checked=json.loads(verification.read_text(encoding='utf-8'))
            for current, previous in [('calibration_thresholds_recomputed','score_counts_match'),
                    ('split_identities_recomputed','split_keys_match'),
                    ('production_sources_unchanged','protected_source_hashes_match'),
                    ('verified_operating_points','metric_points_verified'),
                    ('verified_pooled_points','pooled_points_verified')]:
                if current not in checked and previous in checked:
                    checked[current]=checked[previous]
            if not all(checked[k] for k in ['calibration_thresholds_recomputed',
                    'split_identities_recomputed','input_hashes_match','production_sources_unchanged']):
                raise ValueError(f'{study} score/count/split verification failed')
            if 'research_source_hashes_match' in checked and not checked['research_source_hashes_match']:
                raise ValueError(f'{study} research source changed after training')
            lines += [f'### Verification: {study}', '',
                f"Recomputed {checked['verified_operating_points']} operating points and "
                f"{checked['verified_pooled_points']} pooled points from saved scores, "
                'calibration-only thresholds and observed-positive keys. Exact test/calibration identities '
                'match the grouped season protocol. Frozen input hashes and protected production sources match.', '']
            if study=='incident-ml-reviewed-sectors':
                lines += [f"Verified {checked['reviewed_fit_usages_verified']} fit-only annotation usages, "
                    f"{checked['sector_bag_scores_verified']} pooled sector-to-run scores, "
                    f"{checked['localization_rows_verified']} localization rows and "
                    f"{checked['calibration_selections_verified']} calibration-only selections. "
                    f"The first split's {checked['first_fold_repeated_models']} selected models were independently "
                    'retrained after fixing CSV serialization: maximum full-corpus sector-score difference '
                    f"{checked['maximum_first_fold_repeat_difference']:.17g}. This is a first-split repeat, "
                    'not a second full eight-split search. The incomplete CSV-failed attempt remains archived.', '']
            if study=='incident-ml-field-factor':
                lines += [f"Recomputed {checked['field_configurations_recomputed']} complete field decompositions; "
                    'missingness masks match and maximum finite-value difference is '
                    f"{checked['maximum_field_recompute_difference']:.17g}. The unchanged classifier control "
                    f"matches {checked['reference_scores_compared']} saved enhanced paired/weather scores, "
                    f"maximum difference {checked['maximum_reference_difference']:.17g}. "
                    'This repeats field preparation, not a second full supervised search.', '']
            if study=='incident-ml-factor-blend':
                lines += [f"Compared {checked['component_scores_verified']} source-component scores and "
                    f"recomputed {checked['combination_selections_verified']} calibration-only recipe selections. "
                    'Selected combination scores match replay from the original frozen components; component '
                    'hashes also match. No model fitting or test-quantile selection occurs here.', '']
            if study=='incident-ml-siblings':
                lines += ['Sibling profiles, exact course/layout groups and support-matched placebo features '
                    'were rebuilt from frozen raw sector measurements. The original factor/context control '
                    f"matches {checked['reference_scores_compared']} previous scores (maximum difference "
                    f"{checked['maximum_reference_difference']:.17g}). Later/unordered measurements are "
                    'explicitly permitted only for this post-round research variant.', '']
            if study=='incident-ml-finish-change':
                lines += ['Checkpoint tensors were regenerated from validated raw sector durations and '
                    'session/category layout, without reading official outcome fields. The intermediate '
                    'control and finish-change branch were checked against their saved arrays. '
                    f"All {checked['calibration_selections_verified']} recipe choices use calibration. "
                    f"The unchanged context control matches {checked['reference_scores_compared']} previous "
                    f"scores (maximum difference {checked['maximum_reference_difference']:.17g}).", '']
            if study=='incident-ml-rolling-history':
                lines += [f"Rebuilt all history arrays and placebo profiles across {checked['history_scopes_verified']} "
                    'splits. Fit history arrays stay identical between frozen/rolling modes; calibration '
                    'never reads a test row. Saved source scopes match the recomputed partitions. '
                    f"All {checked['calibration_selections_verified']} calibration choices were checked. "
                    f"The control matches {checked['reference_scores_compared']} previous scores, maximum "
                    f"difference {checked['maximum_reference_difference']:.17g}. Independently audited "
                    f"{checked['independent_role_date_targets_audited']} target histories against allowed "
                    'source roles, strictly earlier dates, round and category. '
                    f"All {checked['repeated_loyo_history_arrays']} leave-season history arrays and "
                    f"{checked['identical_selected_model_representations']} selected model representations "
                    'match the preserved partial run before the forward-scope guard fix. These are '
                    'selected representations (some share fitted weights), not 36 unique model fits. '
                    'Forward 2025/2026 completed after fixing the guard; the failed attempt remains archived.', '',
                    'Causal here describes input-history availability, not the entire leave-year-out '
                    'training protocol: leave-year-out can still train on later seasons. Only the separate '
                    'chronological checks train on earlier years. All inputs remain finalized snapshots.', '']
            if study=='incident-ml-rank-history':
                lines += ['Raw sector ranks, quality masks, scoped historical percentiles and support-matched '
                    'placebo references were reconstructed from frozen sources. Independently checked '
                    f"{checked['independent_rank_targets_audited']} histories against role/date/round/category "
                    f"restrictions and {checked['calibration_selections_verified']} calibration choices. "
                    f"The time-only control reproduces {checked['reference_scores_compared']} earlier scores, "
                    f"maximum difference {checked['maximum_reference_difference']:.17g}. Shared history "
                    'source hashes remain unchanged.', '']
            if study=='incident-ml-density-ratio':
                lines += [f"Rebuilt and refitted {checked['ratio_selected_fits_retrained']} selected kernel-ratio "
                    'models from fit-only preprocessing and positive-only landmarks. Maximum replay score '
                    f"difference {checked['maximum_ratio_retrain_difference']:.17g}; changed alarm decisions "
                    f"{checked['changed_ratio_decisions']}. All candidate KKT checks passed. The numerical "
                    'retrain tolerance is 1e-10 with zero decision changes; saved-score replay uses 1e-12. '
                    'This verifies solver/pipeline repeatability, not physical crash probabilities.', '']
            if study=='incident-ml-tail-ranking':
                lines += [f"Refitted {checked['tail_models_retrained']} selected native tail/full-pair models: "
                    f"maximum score difference {checked['maximum_tail_retrain_difference']:.17g}, "
                    f"{checked['changed_tail_decisions']} changed decisions. Checked fit-only gradient signs, "
                    'curvature and translation invariance at actual fitted margins; '
                    f"{checked['reference_scores_compared']} cached causal-tree scores match exactly. "
                    'Calibration-only recipe selection was replayed. These checks establish numerical '
                    'repeatability, not physical incident identification.', '']
            if study=='incident-ml-monotone':
                lines += [f"Refitted {checked['monotone_models_retrained']} selected monotone models: "
                    f"maximum score difference {checked['maximum_monotone_retrain_difference']:.17g}, "
                    f"{checked['changed_monotone_decisions']} changed decisions. Rebuilt "
                    f"{checked['coordinate_comparisons_verified']} coordinate-property comparisons "
                    'with no decreasing scores; replayed calibration-only selection. '
                    f"All {checked['reference_scores_compared']} cached unconstrained-control scores "
                    'match exactly. Coordinate probes do not prove monotonic response when correlated '
                    'real-run features change together.', '']
            if study=='incident-ml-reviewed-history':
                lines += [f"Replayed {checked['reviewed_fit_usages_verified']} exact fit-only review-use records "
                    'against the frozen original benchmark. '
                    f"Refitted {checked['reviewed_history_models_retrained']} selected reviewed-context models: "
                    f"maximum score difference {checked['maximum_reviewed_history_retrain_difference']:.17g}, "
                    f"{checked['changed_reviewed_history_decisions']} changed alarm decisions. Held targets and "
                    'calibration-only selection remain unchanged; weighted observed-class mass is balanced. '
                    f"All {checked['reference_scores_compared']} cached reference scores match exactly. "
                    'This verifies annotation boundaries/repeatability, not representativeness of the '
                    'selected human review sample.', '']
            if study=='incident-ml-specialists':
                lines += [f"Independently reconstructed {checked['independently_rebuilt_profiles']} masked sector profiles; "
                    f"verified {checked['fitting_partitions_verified']} fit-positive specialist partitions. "
                    f"Refitted {checked['specialist_components_retrained']} native components for "
                    f"{checked['specialist_ensembles_rebuilt']} ensembles. Maximum component score difference "
                    f"{checked['maximum_specialist_component_difference']:.17g}, pooled score difference "
                    f"{checked['maximum_specialist_ensemble_difference']:.17g}, "
                    f"{checked['changed_specialist_decisions']} changed decisions. Calibration-only component "
                    'CDFs and cached label-quarantine controls were replayed; '
                    f"{checked['reference_scores_compared']} reference scores match exactly. These checks "
                    'do not establish that unsupervised groups are physical incident mechanisms.', '']
            if study=='incident-ml-gated-specialists':
                lines += [f"Replayed {checked['routing_views_replayed']} routed/max views using "
                    f"{checked['frozen_components_reused']} frozen components; fitted zero new models. "
                    f"Independently checked {checked['independent_nearest_profile_checks']} nearest-centroid "
                    f"routes ({checked['numerical_near_tie_queries']} numerical near-ties) against explicit "
                    'distances and low-support fallback. '
                    f"All {checked['reference_scores_compared']} inherited control scores match exactly. "
                    'Parent fit-only provenance and calibration-only CDFs were checked. This compares '
                    'pooling/routing only, with inherited max-selected leaves.', '']
            if study=='incident-ml-label-efficiency':
                lines += [f"Independently reconstructed {checked['label_samples_independently_rebuilt']} fit samples; "
                    f"refitted {checked['learning_curve_models_retrained']} reduced-label models and replayed "
                    f"{checked['full_reference_models_replayed']} inherited full-label references. "
                    f"Maximum score difference {checked['maximum_learning_curve_retrain_difference']:.17g}, "
                    f"{checked['changed_learning_curve_decisions']} changed decisions. Fixed U, original held "
                    'targets, inherited leaves and same-calibration-threshold fitting diagnostics were checked. '
                    'This verifies a data-efficiency diagnostic, not selection of a new detector.', '']
            if study in ['incident-ml-sector-sequence','incident-ml-field-noise']:
                lines += [f"Independently reconstructed {checked['independently_rebuilt_observations']} "
                    'run observation vectors, including reference clocks and missing masks. '
                    f"Refitted {checked['sequence_selected_fits_retrained']} selected sequence models: "
                    f"maximum score difference {checked['maximum_sequence_retrain_difference']:.17g}, "
                    f"{checked['changed_sequence_decisions']} changed alarm decisions. Fit-only bag/sector "
                    'anchors and unknown all-missing runs were verified. This is numerical reproducibility, '
                    'not validation of the latent state as a physical crash.', '']
                if study=='incident-ml-field-noise':
                    lines += [f"Independently rebuilt {checked['independently_rebuilt_field_scaled_observations']} "
                        'field-scaled observations and checked preserved diagnostic seconds. '
                        f"Compared {checked['reference_scores_compared']} cached control scores, maximum difference "
                        f"{checked['maximum_reference_difference']:.17g}. Recorded optimizer failures across "
                        f"candidate EM steps: {checked['optimizer_failures']}.", '']
            if study=='incident-ml-aligned':
                lines += [f"The aggregate-tree reference reproduces {checked['reference_scores_compared']} "
                    'earlier paired/weather scores, with maximum difference '
                    f"{checked['maximum_reference_difference']:.17g}. "
                    f"All {checked['neural_trials']} neural trials retained iteration/warning diagnostics: "
                    f"iterations {checked['neural_iteration_range'][0]}–{checked['neural_iteration_range'][1]}, "
                    f"{checked['neural_warnings']} warnings. This records optimizer behavior; it does not "
                    'prove a global optimum or identified crash probabilities.', '']
        if study=='incident-ml-event-robust' and verification.exists():
            lines += [f"Refitted {checked['event_risk_models_retrained']} selected event-risk models; "
                f"maximum score difference {checked['maximum_event_risk_retrain_difference']:.17g}, "
                f"{checked['changed_event_risk_decisions']} changed decisions; "
                f"{checked['event_risk_reference_scores_verified']} frozen reference scores replayed.", '']
        if study=='incident-ml-subbagging' and verification.exists():
            lines += [f"Independently recomputed {checked['subbagging_recipes_verified']} fixed ensemble recipes "
                f"and {checked['subbagging_scores_verified']} scores, maximum difference "
                f"{checked['maximum_subbagging_difference']:.17g}; "
                f"{checked['subbagging_reference_scores_verified']} reference scores matched. "
                'No new fits: the hashed parent proof covers the component refits.', '']
        if study=='incident-ml-field-score' and verification.exists():
            lines += [f"Independently checked {checked['field_score_choices_verified']} choices and "
                f"{checked['field_score_values_verified']} leave-one-out transformed scores, maximum difference "
                f"{checked['maximum_field_score_difference']:.17g}; "
                f"{checked['field_score_reference_values_verified']} reference scores matched.", '']
        if study=='incident-ml-additive' and verification.exists():
            lines += [f"Refitted {checked['additive_models_retrained']} selected additive/linear models "
                f"and independently checked {checked['additive_basis_columns_verified']} fit-basis columns: "
                f"maximum score difference {checked['maximum_additive_retrain_difference']:.17g}, "
                f"{checked['changed_additive_decisions']} changed decisions; "
                f"{checked['additive_reference_scores_verified']} reference scores matched.", '']
        if study=='incident-ml-bounded' and verification.exists():
            lines += [f"Refitted {checked['bounded_models_retrained']} selected bounded/logistic models: "
                f"maximum score difference {checked['maximum_bounded_retrain_difference']:.17g}, "
                f"{checked['changed_bounded_decisions']} changed decisions; "
                f"{checked['bounded_reference_scores_verified']} reference scores matched.", '']
        retraining=folder/'selected-retraining-verification.json'
        if retraining.exists():
            repeated=json.loads(retraining.read_text(encoding='utf-8'))
            if (repeated['changed_decisions'] or repeated['maximum_score_difference']>1e-12
                    or not repeated['input_hashes_match']):
                raise ValueError(f'{study} selected-model retraining failed')
            recipes=repeated['model'] if 'model' in repeated else ', '.join(repeated['models'])
            fits=repeated.get('models_retrained',repeated.get('model_fits'))
            if not fits:
                raise ValueError(f'{study} missing retrained-model count')
            lines += [f"Selected recipe(s) `{recipes}` were retrained in "
                f"{fits} model fits: maximum score difference "
                f"{repeated['maximum_score_difference']:.17g}, "
                f"{repeated['changed_decisions']} changed decisions across "
                f"{repeated['operating_points']} operating points. This is repeatability, not external validity.", '']
            if repeated.get('saved_models_reload_verified'):
                lines += ['Saved model pipelines were also reloaded and scored outside their CLI process.', '']
    field_folder=ROOT/'artifacts/incident-ml-field-objectives-enhanced'
    field_control=field_folder/'reference-ablation-verification.json'
    if field_control.exists():
        control=json.loads(field_control.read_text(encoding='utf-8'))
        if control['maximum_timing_difference']>1e-12 or control['maximum_reference_difference']>1e-12:
            raise ValueError('Field-objective reference/feature ablation failed')
        lines += ['### Within-field reference and ablation controls', '',
            f"All {control['timing_scores_compared']} timing-only scores match between the plain and "
            f"enhanced paired studies (maximum difference {control['maximum_timing_difference']:.17g}). "
            f"All {control['reference_scores_compared']} enhanced pointwise control scores reproduce the "
            'previous environment-grouped paired-weather model '
            f"(maximum difference {control['maximum_reference_difference']:.17g}). "
            'Thus the comparison changes the training objective, not the frozen data or reference protocol.', '']
        field_points=list(csv.DictReader((field_folder/'metrics.csv').open(encoding='utf-8')))
        forward=[r for r in field_points if r['split'].startswith('chronological-')
                 and r['model'] in ['paired-weather-pointwise','paired-weather-round-category-rank']
                 and abs(float(r['budget'])-700/20340)<1e-12]
        lines += ['A higher pooled hit count under the cap is not dominance at comparable alarm rates. '
            'The forward checks must remain visible, including failures to improve the reference.', '',
            table(['Forward test','Recipe','Known hits','Positives','Actual alarms'],
                  [(r['split'],r['model'],r['hits'],r['positives'],r['alarms']) for r in forward]), '']
    rematched=ROOT/'artifacts/incident-label-rematch/matching.csv'
    if rematched.exists():
        original_path=ROOT/'artifacts/incident-conservative/labels.csv'
        labels=list(csv.DictReader(original_path.open(encoding='utf-8-sig')))
        older=[r for r in labels if int(r['year'])<2026]
        repeated=list(csv.DictReader(rematched.open(encoding='utf-8-sig')))
        def assignment(row):
            return (row['session_id'],row['rider_id']) if row['state']=='matched' else None
        a,b=({r['record']:assignment(r) for r in rows} for rows in [older,repeated])
        assert len(a)==len(older) and len(b)==len(repeated) and set(a)==set(b)
        differences=sum(a[k]!=b[k] for k in a)
        sessions,phases={},defaultdict(set)
        for year in range(2021,2027):
            frozen=ROOT/f'artifacts/incident-multiseason/{year}'
            bundle=json.loads((frozen/f'inputs-{year}.json').read_text(encoding='utf-8-sig'))
            metadata={str(s['id']):s for s in bundle['Data']['Sessions']}
            sessions.update(metadata)
            scored={(str(r['SessionId']),str(r['RiderId'])) for r in
                    json.loads((frozen/'baseline.json').read_text(encoding='utf-8-sig'))}
            for run in bundle['PaceSessions']:
                sid,rid=str(run['SessionId']),str(run['Run']['RiderId'])
                s=metadata[sid]
                if (sid,rid) in scored and s['session_type'] in ['Q1','Q2','QUALI']:
                    phases[year,str(s['round_id']),s['categories']['code'],rid].add(s['session_type'])
        ambiguous=0
        for row in labels:
            if row['state']=='matched' and row['session'].strip().upper() in ['QUALIFYING','QUALI']:
                s=sessions[row['session_id']]
                ambiguous+=len(phases[int(row['year']),str(s['round_id']),s['categories']['code'],row['rider_id']])>1
        lines += ['## Frozen label-to-run association audit', '',
            f"Independent Python rematching of {len(a)} original 2021–2025 source observations gives "
            f"{differences} run-assignment disagreements with the frozen C# mapping. "
            f"There are {ambiguous} matched generic Qualifying/Quali observations with multiple scored "
            'qualifying-phase alternatives for the same rider/event/category in the frozen inputs. '
            'Training/ambiguous and unscored observations are not silently assigned to a scored run.', '',
            'This audits structural association only. It does NOT independently verify that an incident '
            'occurred, resolve source/human-review conflicts, locate its sector or replace the frozen '
            '341-positive benchmark. No network, new database data or ground-truth edits were used.', '',
            f"Frozen labels SHA-256: `{hashlib.sha256(original_path.read_bytes()).hexdigest()}`. "
            f"Rematched CSV SHA-256: `{hashlib.sha256(rematched.read_bytes()).hexdigest()}`.", '']
    path.write_text('\n'.join(lines),encoding='utf-8')
    print(path)


if __name__ == '__main__':
    main()
