-- Input measurements are independent of calculator versions. Legacy payloads remain
-- readable during deployment; the new API ignores their derived statistics.
alter table public.ranking_session_preaggregates add column if not exists input_splits jsonb;

create or replace function public.capture_ranking_input_splits()
returns trigger language plpgsql security invoker set search_path='' as $$
begin
  select coalesce(jsonb_agg(jsonb_build_array(s.result_id,s.split_index,s.race_time)
    order by s.result_id,s.split_index),'[]'::jsonb)
  into new.input_splits
  from (
    select distinct on (ts.result_id,ts.split_index) ts.result_id,ts.split_index,ts.race_time
    from public.time_splits ts join public.results r on r.id=ts.result_id
    where r.session_id=new.session_id and ts.split_index>0
    order by ts.result_id,ts.split_index,ts.id desc
  ) s;
  return new;
end;
$$;
revoke all on function public.capture_ranking_input_splits() from public;
create trigger capture_ranking_input_splits before insert or update of payload
on public.ranking_session_preaggregates for each row execute function public.capture_ranking_input_splits();

-- Only cache rows are touched; source results and timings are unchanged.
update public.ranking_session_preaggregates set payload=payload where input_splits is null;

create or replace function public.get_ranking_input_packages(p_session_ids bigint[],p_include_splits boolean default true)
returns table(session_id bigint,payload jsonb)
language sql stable security invoker set search_path='' as $$
select c.session_id,jsonb_build_object(
  'v',3,'s',c.payload->'s','r',c.payload->'r','d',c.payload->'d','t',c.payload->'t',
  'ix',c.input_splits,'x',case when p_include_splits then c.payload->'x' else '[]'::jsonb end)
from public.ranking_session_preaggregates c
where c.session_id=any(coalesce(p_session_ids,'{}'::bigint[])) and c.input_splits is not null
order by c.session_id;
$$;
revoke all on function public.get_ranking_input_packages(bigint[],boolean) from public;
grant execute on function public.get_ranking_input_packages(bigint[],boolean) to anon,authenticated,service_role;

CREATE OR REPLACE FUNCTION public.get_rider_input_data(p_use_preaggregation boolean DEFAULT true, p_season_years integer[] DEFAULT '{}'::integer[], p_round_id bigint DEFAULT NULL::bigint, p_track_ids bigint[] DEFAULT '{}'::bigint[], p_country_codes text[] DEFAULT '{}'::text[], p_venue_names text[] DEFAULT '{}'::text[], p_track_name text DEFAULT NULL::text, p_condition_ids integer[] DEFAULT '{}'::integer[], p_condition_names text[] DEFAULT '{}'::text[], p_competition_class_ids integer[] DEFAULT '{}'::integer[], p_competition_class_codes text[] DEFAULT '{}'::text[], p_category_codes text[] DEFAULT '{}'::text[], p_session_types text[] DEFAULT '{}'::text[], p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_status text DEFAULT NULL::text, p_rider_id bigint DEFAULT NULL::bigint, p_session_ids bigint[] DEFAULT '{}'::bigint[])

 RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER SET search_path = '' SET work_mem='64MB' AS $function$
with filtered_rounds as materialized (
  select
    ro.id,
    ro.track_id,
    ro.season_year,
    ro.start_date,
    ro.round_number,
    ro.competition_level_id,
    cl.code as competition_class_code,
    cl.name as competition_class_name,
    cl.priority as competition_class_priority,
    co.id as condition_id,
    co.name as condition_name,
    tr.name as track_name,
    tr.venue,
    tr.country_code,
    tr.length_m,
    tr.avg_slope
  from public.rounds ro
  join public.tracks tr on tr.id = ro.track_id
  left join public.conditions co on co.id = ro.condition_id
  left join public.competition_levels cl on cl.id = ro.competition_level_id
  where (p_round_id is null or ro.id = p_round_id)
    and (coalesce(cardinality(p_season_years), 0) = 0 or ro.season_year = any(p_season_years))
    and (coalesce(cardinality(p_track_ids), 0) = 0 or ro.track_id = any(p_track_ids))
    and (
      coalesce(cardinality(p_country_codes), 0) = 0
      or upper(tr.country_code) = any(select upper(value) from unnest(p_country_codes) as value)
    )
    and (
      coalesce(cardinality(p_venue_names), 0) = 0
      or lower(tr.venue) = any(select lower(value) from unnest(p_venue_names) as value)
    )
    and (p_track_name is null or lower(tr.name) = lower(p_track_name))
    and (coalesce(cardinality(p_condition_ids), 0) = 0 or ro.condition_id = any(p_condition_ids))
    and (
      coalesce(cardinality(p_condition_names), 0) = 0
      or lower(co.name) = any(select lower(value) from unnest(p_condition_names) as value)
    )
    and (
      coalesce(cardinality(p_competition_class_ids), 0) = 0
      or ro.competition_level_id = any(p_competition_class_ids)
    )
    and (
      coalesce(cardinality(p_competition_class_codes), 0) = 0
      or lower(cl.code) = any(select lower(value) from unnest(p_competition_class_codes) as value)
    )
    and (p_date_from is null or ro.start_date::date >= p_date_from)
    and (p_date_to is null or ro.start_date::date <= p_date_to)
),
filtered_sessions as materialized (
  select
    se.id,
    se.round_id,
    se.category_id,
    se.name,
    se.session_type,
    se.session_date,
    fr.track_id,
    fr.season_year,
    fr.start_date,
    fr.competition_class_priority,
    ca.code as category_code,
    fr.round_number,
    fr.competition_level_id,
    fr.competition_class_code,
    fr.competition_class_name,
    fr.condition_id,
    fr.condition_name,
    fr.track_name,
    fr.venue,
    fr.country_code,
    fr.length_m,
    fr.avg_slope
  from public.sessions se
  join filtered_rounds fr on fr.id = se.round_id
  join public.categories ca on ca.id = se.category_id
  where (
      coalesce(cardinality(p_category_codes), 0) = 0
      or upper(ca.code) = any(select upper(value) from unnest(p_category_codes) as value)
    )
    and (
      coalesce(cardinality(p_session_types), 0) = 0
      or upper(se.session_type) = any(select upper(value) from unnest(p_session_types) as value)
    )
    and (coalesce(cardinality(p_session_ids), 0) = 0 or se.id = any(p_session_ids))
),
selected_results as materialized (
  select
    re.id,
    re.rider_id,
    re.session_id,
    re.race_time_ms,
    re.top_speed,
    re.position,
    re.is_classified,
    re.status,
    re.team_id,
    (
      (re.is_classified = true or re.position > 0)
      and coalesce(upper(re.status), '') not in ('DNF', 'DNS', 'DSQ')
    ) as is_valid_classification
  from public.results re
  join filtered_sessions fs on fs.id = re.session_id
  where (p_rider_id is null or re.rider_id = p_rider_id)
    and (p_status is null or re.status = p_status)
),
points_by_result as materialized (
  select rp.result_id, sum(rp.points_awarded) as points
  from public.result_points rp
  join selected_results sr on sr.id = rp.result_id
  group by rp.result_id
),
rider_last_dates as materialized (
  select
    sr.rider_id,
    max(coalesce(fs.session_date::date, fs.start_date::date)) as last_result_date
  from selected_results sr
  join filtered_sessions fs on fs.id = sr.session_id
  group by sr.rider_id
),
selected_team_data as materialized (
  select distinct on (sr.rider_id)
    sr.rider_id,
    te.id as team_id,
    te.name as team_name,
    te.code as team_code
  from selected_results sr
  join filtered_sessions fs on fs.id = sr.session_id
  join public.teams te on te.id = sr.team_id
  order by
    sr.rider_id,
    coalesce(fs.session_date::date, fs.start_date::date) desc,
    sr.id desc
),
historical_team_data as materialized (
  select distinct on (missing.rider_id)
    missing.rider_id,
    te.id as team_id,
    te.name as team_name,
    te.code as team_code
  from rider_last_dates missing
  left join selected_team_data selected_team on selected_team.rider_id = missing.rider_id
  join public.results historical_result on historical_result.rider_id = missing.rider_id
  join public.sessions historical_session on historical_session.id = historical_result.session_id
  join public.rounds historical_round on historical_round.id = historical_session.round_id
  join public.teams te on te.id = historical_result.team_id
  where selected_team.team_id is null
  order by
    missing.rider_id,
    case
      when coalesce(historical_session.session_date::date, historical_round.start_date::date) <= missing.last_result_date
      then 0 else 1
    end,
    coalesce(historical_session.session_date::date, historical_round.start_date::date) desc,
    historical_result.id desc
),
rider_data as materialized (
  select
    ri.id,
    ri.uci_id,
    ri.uci_code,
    ri.first_name,
    ri.last_name,
    ri.nation,
    ri.birth_date,
    ri.profile_image_url,
    ri.has_injury,
    ri.gender,
    coalesce(selected_team.team_id, historical_team.team_id) as team_id,
    coalesce(selected_team.team_name, historical_team.team_name) as team_name,
    coalesce(selected_team.team_code, historical_team.team_code) as team_code
  from public.riders ri
  left join selected_team_data selected_team on selected_team.rider_id = ri.id
  left join historical_team_data historical_team on historical_team.rider_id = ri.id
  where p_rider_id is null or ri.id = p_rider_id
),
team_data as materialized (
  select distinct te.id, te.name, te.code
  from selected_results sr
  join public.teams te on te.id = sr.team_id
),

cache as materialized (
 select c.* from public.ranking_session_preaggregates c
 where p_use_preaggregation and c.session_id in (select distinct session_id from selected_results)
),
validity as materialized (
 select p_use_preaggregation and p_status is null
 and not exists (
  select 1 from selected_results sr left join cache c on c.session_id=sr.session_id
  where c.session_id is null or c.input_splits is null
 ) as cached
),
field_results as materialized (
 select re.* from public.results re
 where not (select cached from validity)
 and re.session_id in (select distinct session_id from selected_results)
 and (p_status is null or re.status=p_status)
),
raw_splits as materialized (
 select distinct on (ts.result_id,ts.split_index)
 ts.result_id,ts.split_index,ts.race_time,ts.speed,re.session_id
 from public.time_splits ts join field_results re on re.id=ts.result_id
 where ts.split_index>0 and upper(btrim(coalesce(re.status,'')))<>'DSQ'
 order by ts.result_id,ts.split_index,ts.id desc
),
timing_values as materialized (
 select ts.*,
 case when ts.split_index=1 then ts.race_time
      when prev.race_time is not null then ts.race_time-prev.race_time end as sector_time
 from raw_splits ts left join raw_splits prev
 on prev.result_id=ts.result_id and prev.split_index=ts.split_index-1
),
split_ranks as (
 select ts.*,rank() over(partition by session_id,split_index order by race_time)::integer as split_rank
 from timing_values ts where race_time>0
),
sector_ranks as (
 select result_id,split_index,
 rank() over(partition by session_id,split_index order by sector_time)::integer as sector_rank
 from timing_values where sector_time>0
)
select jsonb_build_object('complete',true,'cached',(select cached from validity),'payload',jsonb_build_object(
 'v',3,
 's', coalesce((
    select jsonb_agg(jsonb_build_array(
      fs.id, fs.round_id, fs.category_id, fs.name, fs.session_type,
      fs.session_date, fs.track_id, fs.season_year, fs.start_date,
      fs.competition_class_priority, fs.category_code, fs.round_number,
      fs.competition_level_id, fs.competition_class_code,
      fs.competition_class_name, fs.condition_id, fs.condition_name,
      fs.track_name, fs.venue, fs.country_code, fs.length_m, fs.avg_slope
    ))
    from filtered_sessions fs
  ), '[]'::jsonb),
  'r', coalesce((
    select jsonb_agg(jsonb_build_array(
      sr.id, sr.rider_id, sr.session_id, sr.race_time_ms, sr.top_speed,
      sr.position, sr.is_classified, sr.status, coalesce(pr.points, 0), sr.team_id
    ))
    from selected_results sr
    left join points_by_result pr on pr.result_id = sr.id
  ), '[]'::jsonb),
  'd', coalesce((
    select jsonb_agg(jsonb_build_array(
      rd.id, rd.uci_id, rd.first_name, rd.last_name, rd.nation,
      rd.profile_image_url, rd.team_id, rd.team_name, rd.uci_code,
      rd.birth_date, rd.has_injury, rd.team_code, rd.gender
    ))
    from rider_data rd
  ), '[]'::jsonb),
  't', coalesce((
    select jsonb_agg(jsonb_build_array(td.id, td.name, td.code))
    from team_data td
  ), '[]'::jsonb),

 'f',case when (select cached from validity) then
 coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'r') v),'[]')
 else coalesce((select jsonb_agg(jsonb_build_array(id,rider_id,session_id,race_time_ms,top_speed,position,is_classified,status)) from field_results),'[]') end,
 'ix',case when (select cached from validity) then
 coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.input_splits) v),'[]')
 else coalesce((select jsonb_agg(jsonb_build_array(result_id,split_index,race_time)) from raw_splits),'[]') end,
 'x',case when (select cached from validity) then
 coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'x') v join selected_results r on r.id=(v->>0)::bigint),'[]')
 else coalesce((select jsonb_agg(jsonb_build_array(s.result_id,s.split_index,s.race_time,s.speed,s.split_rank,k.sector_rank))
 from split_ranks s join selected_results r on r.id=s.result_id
 left join sector_ranks k on k.result_id=s.result_id and k.split_index=s.split_index),'[]') end
));
$function$;
revoke all on function public.get_rider_input_data(boolean,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint,bigint[]) from public;
grant execute on function public.get_rider_input_data(boolean,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint,bigint[]) to anon, authenticated, service_role;
