begin;

alter table public.sessions
  add column if not exists points_session_type_override text;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.sessions'::regclass
      and conname = 'sessions_points_session_type_override_check'
  ) then
    alter table public.sessions
      add constraint sessions_points_session_type_override_check
      check (
        points_session_type_override is null
        or points_session_type_override in ('Q1', 'Q2', 'QUALI', 'SEMI', 'FINAL')
      );
  end if;
end $$;

comment on column public.sessions.points_session_type_override is
  'Optional points-only stage override. The session keeps its real type for metrics, while points use this stage, for example when a cancelled junior final is scored from qualifying.';

-- World Cup systems must match World Cup rounds, independently of the
-- competition class used by Rating.
update public.points_systems
set competition_rank_id = (
  select id
  from public.competition_ranks
  where code = 'world_cup'
)
where valid_from_season between 2023 and 2025
  and valid_to_season between 2023 and 2025
  and discipline = 'DHI'
  and code like 'uci_dhi_wc_%';

-- The last World Cup round suppresses qualification/semi-final points where
-- configured by point_system_stages.awards_points_on_season_final_round.
with ranked_rounds as (
  select
    r.id,
    row_number() over (
      partition by r.season_year
      order by r.start_date desc nulls last, r.id desc
    ) as season_order
  from public.rounds r
  join public.competition_ranks cr on cr.id = r.competition_rank_id
  where cr.code = 'world_cup'
    and r.season_year between 2023 and 2025
)
update public.rounds r
set is_season_final_round = (rr.season_order = 1)
from ranked_rounds rr
where r.id = rr.id;

-- In 2025 the final World Cup round awarded no Q1 points for Elite riders.
update public.point_system_stages pss
set awards_points_on_season_final_round = false
from public.points_systems ps
where ps.id = pss.point_system_id
  and ps.valid_from_season = 2025
  and ps.valid_to_season = 2025
  and ps.code in ('uci_dhi_wc_2025_me', 'uci_dhi_wc_2025_we')
  and pss.session_type in ('Q1', 'QUALI');

-- Loudenvielle 2023 and Pal Arinsal 2025 had cancelled Junior finals. Their
-- qualifying classification received the normal FINAL points table.
-- The existing preaggregation trigger on sessions expects NEW.session_id even
-- though sessions identifies itself with id, so bypass it for this narrow edit.
alter table public.sessions
  disable trigger invalidate_ranking_preaggregate_sessions;

update public.sessions s
set points_session_type_override = 'FINAL'
from public.rounds r
where r.id = s.round_id
  and s.category_id in (3, 4)
  and (
    (r.season_year = 2023 and r.round_number = 5 and s.session_type = 'QUALI')
    or
    (r.season_year = 2025 and r.round_number = 6 and s.session_type in ('Q1', 'QUALI'))
  );

alter table public.sessions
  enable trigger invalidate_ranking_preaggregate_sessions;

commit;

notify pgrst, 'reload schema';
