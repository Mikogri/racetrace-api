begin;

-- The 2022 World Cup season used the normal qualification/final tables at
-- rounds 1-7. At the Val di Sole season finale Elite qualification awarded no
-- points and the Elite final used a bonus table. Junior seeding never awarded
-- points and Junior final points were unchanged at the season finale.
alter table public.point_rules
  add column if not exists season_final_points integer;

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conrelid = 'public.point_rules'::regclass
      and conname = 'point_rules_season_final_points_check'
  ) then
    alter table public.point_rules
      add constraint point_rules_season_final_points_check
      check (season_final_points is null or season_final_points >= 0);
  end if;
end $$;

comment on column public.point_rules.season_final_points is
  'Optional replacement for points on the round marked is_season_final_round.';

-- Resolve the final World Cup round independently of imported round numbers.
with world_cup_rounds as (
  select
    r.id,
    row_number() over (order by r.start_date desc nulls last, r.id desc) as season_order
  from public.rounds r
  join public.competition_ranks cr on cr.id = r.competition_rank_id
  where r.season_year = 2022
    and cr.code = 'world_cup'
)
update public.rounds r
set is_season_final_round = (wcr.season_order = 1)
from world_cup_rounds wcr
where r.id = wcr.id;

create temporary table tmp_2022_point_systems (
  category_code text primary key,
  system_code text not null,
  system_name text not null,
  source_url text not null,
  notes text not null
) on commit drop;

insert into tmp_2022_point_systems values
  ('EM', 'uci_dhi_wc_2022_men_elite', 'UCI DHI World Cup 2022 - Elite Men',
   'https://www.pinkbike.com/news/final-results-from-the-val-di-sole-dh-world-cup-2022.html',
   'Normal rounds: qualification and final points. Season finale: no qualification points and Elite final bonus points.'),
  ('EW', 'uci_dhi_wc_2022_women_elite', 'UCI DHI World Cup 2022 - Elite Women',
   'https://www.pinkbike.com/news/final-results-from-the-val-di-sole-dh-world-cup-2022.html',
   'Normal rounds: qualification and final points. Season finale: no qualification points and Elite final bonus points.'),
  ('JM', 'uci_dhi_wc_2022_men_junior', 'UCI DHI World Cup 2022 - Junior Men',
   'https://misspentsummers.com/wp-content/uploads/2022/09/Junior-men-qualifying-results-Val-di-Sole-2022.pdf',
   'Seeding awards no points. Final points are unchanged at the season finale.'),
  ('JW', 'uci_dhi_wc_2022_women_junior', 'UCI DHI World Cup 2022 - Junior Women',
   'https://misspentsummers.com/wp-content/uploads/2022/09/Junior-women-qualifying-results-Val-di-Sole-2022.pdf',
   'Seeding awards no points. Final points are unchanged at the season finale.');

insert into public.points_systems (
  code,
  name,
  competition_level_id,
  competition_rank_id,
  discipline,
  category_id,
  valid_from_season,
  valid_to_season,
  source_url,
  notes
)
select
  t.system_code,
  t.system_name,
  cl.id,
  cr.id,
  'DHI',
  c.id,
  2022,
  2022,
  t.source_url,
  t.notes
from tmp_2022_point_systems t
join public.categories c on c.code = t.category_code
join public.competition_levels cl on cl.code = 'world_cup'
join public.competition_ranks cr on cr.code = 'world_cup'
on conflict (code) do update
set name = excluded.name,
    competition_level_id = excluded.competition_level_id,
    competition_rank_id = excluded.competition_rank_id,
    discipline = excluded.discipline,
    category_id = excluded.category_id,
    valid_from_season = excluded.valid_from_season,
    valid_to_season = excluded.valid_to_season,
    source_url = excluded.source_url,
    notes = excluded.notes;

create temporary table tmp_2022_stages (
  category_code text not null,
  session_type text not null,
  awards_points boolean not null,
  awards_on_final_round boolean not null,
  primary key (category_code, session_type)
) on commit drop;

insert into tmp_2022_stages values
  ('EM', 'QUALI', true, false),
  ('EM', 'FINAL', true, true),
  ('EW', 'QUALI', true, false),
  ('EW', 'FINAL', true, true),
  ('JM', 'SEEDING', false, false),
  ('JM', 'QUALI', false, false),
  ('JM', 'FINAL', true, true),
  ('JW', 'SEEDING', false, false),
  ('JW', 'QUALI', false, false),
  ('JW', 'FINAL', true, true);

insert into public.point_system_stages (
  point_system_id,
  session_type,
  awards_points,
  awards_points_on_season_final_round
)
select
  ps.id,
  t.session_type,
  t.awards_points,
  t.awards_on_final_round
from tmp_2022_stages t
join tmp_2022_point_systems ts on ts.category_code = t.category_code
join public.points_systems ps on ps.code = ts.system_code
on conflict (point_system_id, session_type) do update
set awards_points = excluded.awards_points,
    awards_points_on_season_final_round = excluded.awards_points_on_season_final_round;

-- Replacing all rules makes rerunning this migration deterministic.
delete from public.point_rules pr
using public.point_system_stages pss, public.points_systems ps
where pr.point_system_stage_id = pss.id
  and pss.point_system_id = ps.id
  and ps.code in (select system_code from tmp_2022_point_systems);

create temporary table tmp_2022_rules (
  category_code text not null,
  session_type text not null,
  position integer not null,
  points integer not null,
  season_final_points integer,
  primary key (category_code, session_type, position)
) on commit drop;

-- Elite Men qualification: positions 1-20.
insert into tmp_2022_rules
select 'EM', 'QUALI', ordinality::integer, points, null
from unnest(array[50,40,30,25,22,20,18,17,16,15,14,13,12,11,10,9,8,7,6,5])
  with ordinality as values_table(points, ordinality);

-- Elite Women qualification: positions 1-10.
insert into tmp_2022_rules
select 'EW', 'QUALI', ordinality::integer, points, null
from unnest(array[50,40,30,25,20,16,14,12,10,5])
  with ordinality as values_table(points, ordinality);

-- Elite Men final. season_final_points is the Val di Sole bonus table.
insert into tmp_2022_rules
select 'EM', 'FINAL', ordinality::integer, normal_points, final_points
from unnest(
  array[200,160,140,125,110,95,90,85,80,75,70,65,60,55,50,45,44,43,42,41,40,39,38,37,36,35,34,33,32,31,30,29,28,27,26,25,24,23,22,21,20,19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1],
  array[250,200,170,150,132,115,108,102,96,90,84,78,72,66,60,54,52,50,48,46,40,39,38,37,36,35,34,33,32,31,30,29,28,27,26,25,24,23,22,21,20,19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1]
) with ordinality as values_table(normal_points, final_points, ordinality);

-- Elite Women final.
insert into tmp_2022_rules
select 'EW', 'FINAL', ordinality::integer, normal_points, final_points
from unnest(
  array[200,160,140,125,110,95,80,70,60,55,45,35,25,15,5],
  array[250,200,170,150,130,111,94,82,70,60,45,35,25,15,5]
) with ordinality as values_table(normal_points, final_points, ordinality);

-- Junior Men final: positions 1-25, unchanged at the season finale.
insert into tmp_2022_rules
select 'JM', 'FINAL', ordinality::integer, points, points
from unnest(array[60,50,45,40,35,30,28,26,24,22,20,18,16,14,12,10,9,8,7,6,5,4,3,2,1])
  with ordinality as values_table(points, ordinality);

-- Junior Women final: positions 1-10, unchanged at the season finale.
insert into tmp_2022_rules
select 'JW', 'FINAL', ordinality::integer, points, points
from unnest(array[60,50,45,40,35,30,25,15,10,5])
  with ordinality as values_table(points, ordinality);

insert into public.point_rules (
  point_system_stage_id,
  position_from,
  position_to,
  points,
  season_final_points
)
select
  pss.id,
  r.position,
  r.position,
  r.points,
  r.season_final_points
from tmp_2022_rules r
join tmp_2022_point_systems ts on ts.category_code = r.category_code
join public.points_systems ps on ps.code = ts.system_code
join public.point_system_stages pss
  on pss.point_system_id = ps.id
 and pss.session_type = r.session_type;

-- Remove prior 2022 World Cup calculations for these systems and rebuild them
-- from the authoritative rules, including zero rows for unclassified riders.
delete from public.result_points rp
using public.points_systems ps
where rp.points_system_id = ps.id
  and ps.code in (select system_code from tmp_2022_point_systems);

insert into public.result_points (
  result_id,
  points_system_id,
  point_system_stage_id,
  point_rule_id,
  position_used,
  points_awarded,
  calculated_at
)
select
  res.id,
  ps.id,
  pss.id,
  pr.id,
  case
    when res.position > 0
      and coalesce(res.is_classified, false)
      and upper(coalesce(res.status, '')) not in ('DNF', 'DNS', 'DSQ')
    then res.position
  end,
  case
    when not pss.awards_points then 0
    when r.is_season_final_round and not pss.awards_points_on_season_final_round then 0
    when res.position is null
      or res.position <= 0
      or not coalesce(res.is_classified, false)
      or upper(coalesce(res.status, '')) in ('DNF', 'DNS', 'DSQ') then 0
    when r.is_season_final_round then coalesce(pr.season_final_points, pr.points, 0)
    else coalesce(pr.points, 0)
  end,
  now()
from public.results res
join public.sessions s on s.id = res.session_id
join public.rounds r on r.id = s.round_id
join public.categories c on c.id = s.category_id
join public.competition_ranks cr on cr.id = r.competition_rank_id and cr.code = 'world_cup'
join tmp_2022_point_systems ts on ts.category_code = c.code
join public.points_systems ps on ps.code = ts.system_code
join public.point_system_stages pss
  on pss.point_system_id = ps.id
 and pss.session_type = coalesce(s.points_session_type_override, s.session_type)
left join public.point_rules pr
  on pr.point_system_stage_id = pss.id
 and res.position between pr.position_from and coalesce(pr.position_to, res.position)
where r.season_year = 2022
on conflict (result_id, points_system_id) do update
set point_system_stage_id = excluded.point_system_stage_id,
    point_rule_id = excluded.point_rule_id,
    position_used = excluded.position_used,
    points_awarded = excluded.points_awarded,
    calculated_at = excluded.calculated_at;

-- Enforce the season-final suppression as a postcondition as well. This keeps
-- a rerun correct even when older result_points rows were produced by a prior
-- application version while the migration was executing.
update public.result_points rp
set points_awarded = 0,
    point_rule_id = null,
    calculated_at = now()
from public.results res,
     public.sessions s,
     public.rounds r,
     public.point_system_stages pss,
     public.points_systems ps
where res.id = rp.result_id
  and s.id = res.session_id
  and r.id = s.round_id
  and pss.id = rp.point_system_stage_id
  and ps.id = rp.points_system_id
  and ps.code in (select system_code from tmp_2022_point_systems)
  and r.season_year = 2022
  and r.is_season_final_round
  and not pss.awards_points_on_season_final_round;

do $$
begin
  if exists (
    select 1
    from public.result_points rp
    join public.results res on res.id = rp.result_id
    join public.sessions s on s.id = res.session_id
    join public.rounds r on r.id = s.round_id
    join public.point_system_stages pss on pss.id = rp.point_system_stage_id
    join public.points_systems ps on ps.id = rp.points_system_id
    where ps.code like 'uci_dhi_wc_2022_%'
      and r.season_year = 2022
      and r.is_season_final_round
      and not pss.awards_points_on_season_final_round
      and rp.points_awarded <> 0
  ) then
    raise exception '2022 season-final non-point stages still contain awarded points';
  end if;
end $$;

commit;

notify pgrst, 'reload schema';
