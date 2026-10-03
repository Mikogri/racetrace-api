CREATE OR REPLACE FUNCTION public.get_prepared_rider_detail_data(p_algorithm_version text DEFAULT 'pace-field-v1', p_season_years integer[] DEFAULT '{}'::integer[], p_round_id bigint DEFAULT NULL::bigint, p_track_ids bigint[] DEFAULT '{}'::bigint[], p_country_codes text[] DEFAULT '{}'::text[], p_venue_names text[] DEFAULT '{}'::text[], p_track_name text DEFAULT NULL::text, p_condition_ids integer[] DEFAULT '{}'::integer[], p_condition_names text[] DEFAULT '{}'::text[], p_competition_class_ids integer[] DEFAULT '{}'::integer[], p_competition_class_codes text[] DEFAULT '{}'::text[], p_category_codes text[] DEFAULT '{}'::text[], p_session_types text[] DEFAULT '{}'::text[], p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_status text DEFAULT NULL::text, p_rider_id bigint DEFAULT NULL::bigint, p_session_ids bigint[] DEFAULT '{}'::bigint[])

 RETURNS jsonb LANGUAGE sql STABLE SET search_path = '' SET work_mem='64MB' AS $function$
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
 where c.session_id in (select distinct session_id from selected_results)
),
validity as (
 select p_status is null and p_rider_id is not null
 and not exists (
  select 1 from selected_results sr left join cache c on c.session_id=sr.session_id
  where c.session_id is null or c.prepared_version is distinct from p_algorithm_version
     or c.prepared_payload is null
 ) as complete
)
select case when not (select complete from validity) then jsonb_build_object('complete',false)
else jsonb_build_object('complete',true,'payload',jsonb_build_object(
 'v',2,
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

 'f','[]'::jsonb,
 'i','[]'::jsonb,
 'h',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'h') v),'[]'),
 'g',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'g') v),'[]'),
 'a',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'a') v join selected_results r on r.id=(v->>0)::bigint),'[]'),
 'q',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'q') v join selected_results r on r.id=(v->>0)::bigint),'[]'),
 'x',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.payload->'x') v join selected_results r on r.id=(v->>0)::bigint),'[]'),
 'pf',coalesce((select jsonb_agg(v) from cache c cross join lateral jsonb_array_elements(c.prepared_payload) v where (v->>1)::bigint=p_rider_id),'[]')
)) end;
$function$;
revoke all on function public.get_prepared_rider_detail_data(text,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint,bigint[]) from public;
grant execute on function public.get_prepared_rider_detail_data(text,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint,bigint[]) to anon, authenticated, service_role;
