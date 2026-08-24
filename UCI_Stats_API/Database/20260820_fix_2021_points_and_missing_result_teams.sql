begin;

-- The 2021 Elite season finale used the same bonus table as 2022:
-- qualification awarded no points and final positions used season_final_points.
with matching_rules as (
  select
    target_rule.id as target_rule_id,
    source_rule.season_final_points
  from public.points_systems target_system
  join public.categories category
    on category.id = target_system.category_id
  join public.point_system_stages target_stage
    on target_stage.point_system_id = target_system.id
   and target_stage.session_type = 'FINAL'
  join public.point_rules target_rule
    on target_rule.point_system_stage_id = target_stage.id
  join public.points_systems source_system
    on source_system.category_id = target_system.category_id
   and source_system.competition_level_id = target_system.competition_level_id
   and source_system.discipline = target_system.discipline
   and source_system.valid_from_season <= 2022
   and source_system.valid_to_season >= 2022
  join public.point_system_stages source_stage
    on source_stage.point_system_id = source_system.id
   and source_stage.session_type = 'FINAL'
  join public.point_rules source_rule
    on source_rule.point_system_stage_id = source_stage.id
   and source_rule.position_from = target_rule.position_from
   and source_rule.position_to = target_rule.position_to
  where target_system.valid_from_season <= 2021
    and target_system.valid_to_season >= 2021
    and target_system.competition_level_id = 2
    and target_system.discipline = 'DHI'
    and category.code in ('EM', 'EW')
)
update public.point_rules target
set season_final_points = source.season_final_points
from matching_rules source
where target.id = source.target_rule_id;

-- Historical PDFs do not contain trade-team data. For the already imported
-- first two 2021 rounds, use the closest known team-history period only when
-- the result itself has no team.
with candidates as (
  select
    result.id as result_id,
    history.team_id,
    row_number() over (
      partition by result.id
      order by
        case
          when session.session_date >= history.valid_from
           and (history.valid_to is null or session.session_date <= history.valid_to)
            then 0
          else 1
        end,
        least(
          abs(session.session_date - history.valid_from),
          abs(session.session_date - coalesce(history.valid_to, history.valid_from))
        ),
        history.valid_from desc
    ) as preference
  from public.results result
  join public.sessions session
    on session.id = result.session_id
  join public.rounds round
    on round.id = session.round_id
  join public.rider_team_history history
    on history.rider_id = result.rider_id
  where result.team_id is null
    and round.season_year = 2021
    and round.round_number in (1, 2)
)
update public.results result
set team_id = candidate.team_id,
    updated_at = now()
from candidates candidate
where result.id = candidate.result_id
  and candidate.preference = 1
  and result.team_id is null;

commit;
