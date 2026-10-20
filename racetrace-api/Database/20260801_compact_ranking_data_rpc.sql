begin;

-- Returns the complete, filtered ranking input as one compact JSON value.
-- Array positions are decoded by DomainQueryService.ParseRankingDataBundle.
drop function if exists public.get_ranking_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, text, text, bigint[]
);

create or replace function public.get_ranking_data(
  p_season_years integer[] default '{}',
  p_round_id bigint default null,
  p_track_ids bigint[] default '{}',
  p_country_codes text[] default '{}',
  p_venue_names text[] default '{}',
  p_track_name text default null,
  p_condition_ids integer[] default '{}',
  p_condition_names text[] default '{}',
  p_competition_class_ids integer[] default '{}',
  p_competition_class_codes text[] default '{}',
  p_category_codes text[] default '{}',
  p_session_types text[] default '{}',
  p_date_from date default null,
  p_date_to date default null,
  p_status text default null,
  p_rider_query text default null,
  p_nation text default null,
  p_session_ids bigint[] default '{}',
  p_rider_id bigint default null
)
returns jsonb
language sql
stable
security invoker
set search_path = ''
as $function$
with filtered_rounds as (
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
      or upper(tr.country_code) = any(
        select upper(value) from unnest(p_country_codes) as value
      )
    )
    and (
      coalesce(cardinality(p_venue_names), 0) = 0
      or lower(tr.venue) = any(
        select lower(value) from unnest(p_venue_names) as value
      )
    )
    and (p_track_name is null or lower(tr.name) = lower(p_track_name))
    and (coalesce(cardinality(p_condition_ids), 0) = 0 or ro.condition_id = any(p_condition_ids))
    and (
      coalesce(cardinality(p_condition_names), 0) = 0
      or lower(co.name) = any(
        select lower(value) from unnest(p_condition_names) as value
      )
    )
    and (
      coalesce(cardinality(p_competition_class_ids), 0) = 0
      or ro.competition_level_id = any(p_competition_class_ids)
    )
    and (
      coalesce(cardinality(p_competition_class_codes), 0) = 0
      or lower(cl.code) = any(
        select lower(value) from unnest(p_competition_class_codes) as value
      )
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
      or upper(ca.code) = any(
        select upper(value) from unnest(p_category_codes) as value
      )
    )
    and (
      coalesce(cardinality(p_session_types), 0) = 0
      or upper(se.session_type) = any(
        select upper(value) from unnest(p_session_types) as value
      )
    )
    and (coalesce(cardinality(p_session_ids), 0) = 0 or se.id = any(p_session_ids))
),
filtered_results as materialized (
  select
    re.id,
    re.rider_id,
    re.session_id,
    re.race_time_ms,
    re.top_speed,
    re.position,
    re.is_classified,
    re.status,
    re.team_id
  from public.results re
  join filtered_sessions fs on fs.id = re.session_id
  where (p_status is null or re.status = p_status)
),
matching_team_riders as materialized (
  select distinct team_result.rider_id
  from public.teams searched_team
  join public.results team_result on team_result.team_id = searched_team.id
  where p_rider_id is null
    and p_rider_query is not null
    and btrim(p_rider_query) <> ''
    and (
      searched_team.name ilike ('%' || p_rider_query || '%')
      or searched_team.code ilike ('%' || p_rider_query || '%')
    )
),
matching_riders as materialized (
  select ri.id
  from public.riders ri
  where (p_rider_id is null or ri.id = p_rider_id)
    and (p_nation is null or upper(ri.nation) = upper(p_nation))
    and (
      p_rider_id is not null
      or
      p_rider_query is null
      or btrim(p_rider_query) = ''
      or ri.first_name ilike ('%' || p_rider_query || '%')
      or ri.last_name ilike ('%' || p_rider_query || '%')
      or ri.uci_code ilike ('%' || p_rider_query || '%')
      or ri.nation ilike ('%' || p_rider_query || '%')
      or concat_ws(' ', ri.first_name, ri.last_name) ilike ('%' || p_rider_query || '%')
      or ri.id::text = p_rider_query
      or ri.uci_id::text = p_rider_query
      or ri.id in (select rider_id from matching_team_riders)
    )
),
selected_results as materialized (
  select re.*
  from filtered_results re
  join matching_riders ri on ri.id = re.rider_id
),
field_results as materialized (
  select re.*
  from filtered_results re
  where (
    (p_nation is null and (p_rider_query is null or btrim(p_rider_query) = ''))
    or exists (
      select 1
      from selected_results selected
      where selected.session_id = re.session_id
    )
  )
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
missing_team_riders as materialized (
  select rd.rider_id, rd.last_result_date
  from rider_last_dates rd
  left join selected_team_data selected_team on selected_team.rider_id = rd.rider_id
  where selected_team.team_id is null
),
historical_team_data as materialized (
  select distinct on (missing.rider_id)
    missing.rider_id,
    te.id as team_id,
    te.name as team_name,
    te.code as team_code
  from missing_team_riders missing
  join public.results historical_result on historical_result.rider_id = missing.rider_id
  join public.sessions historical_session on historical_session.id = historical_result.session_id
  join public.rounds historical_round on historical_round.id = historical_session.round_id
  join public.teams te on te.id = historical_result.team_id
  order by
    missing.rider_id,
    case
      when missing.last_result_date is null
        or coalesce(historical_session.session_date::date, historical_round.start_date::date) <= missing.last_result_date
      then 0
      else 1
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
    null::boolean as retired_slot,
    coalesce(selected_team.team_id, historical_team.team_id) as team_id,
    coalesce(selected_team.team_name, historical_team.team_name) as team_name,
    coalesce(selected_team.team_code, historical_team.team_code) as team_code
  from rider_last_dates rd
  join public.riders ri on ri.id = rd.rider_id
  left join selected_team_data selected_team on selected_team.rider_id = rd.rider_id
  left join historical_team_data historical_team on historical_team.rider_id = rd.rider_id
),
team_data as materialized (
  select distinct te.id, te.name, te.code
  from selected_results sr
  join public.teams te on te.id = sr.team_id
)
select jsonb_build_object(
  'v', 1,
  's', coalesce((
    select jsonb_agg(
      jsonb_build_array(
        fs.id,
        fs.round_id,
        fs.category_id,
        fs.name,
        fs.session_type,
        fs.session_date,
        fs.track_id,
        fs.season_year,
        fs.start_date,
        fs.competition_class_priority,
        fs.category_code,
        fs.round_number,
        fs.competition_level_id,
        fs.competition_class_code,
        fs.competition_class_name,
        fs.condition_id,
        fs.condition_name,
        fs.track_name,
        fs.venue,
        fs.country_code,
        fs.length_m,
        fs.avg_slope
      )
    )
    from filtered_sessions fs
  ), '[]'::jsonb),
  'r', coalesce((
    select jsonb_agg(
      jsonb_build_array(
        sr.id,
        sr.rider_id,
        sr.session_id,
        sr.race_time_ms,
        sr.top_speed,
        sr.position,
        sr.is_classified,
        sr.status,
        coalesce(pr.points, 0),
        sr.team_id
      )
    )
    from selected_results sr
    left join points_by_result pr on pr.result_id = sr.id
  ), '[]'::jsonb),
  'f', case
    when p_rider_id is null
      and p_nation is null
      and (p_rider_query is null or btrim(p_rider_query) = '')
      then '[]'::jsonb
    else coalesce((
      select jsonb_agg(
        jsonb_build_array(
          fr.id,
          fr.rider_id,
          fr.session_id,
          fr.race_time_ms,
          fr.top_speed,
          fr.position,
          fr.is_classified,
          fr.status
        )
      )
      from field_results fr
    ), '[]'::jsonb)
  end,
  'x', coalesce((
    select jsonb_agg(
      jsonb_build_array(ts.result_id, ts.split_index, ts.race_time, ts.speed)
    )
    from public.time_splits ts
    join field_results fr on fr.id = ts.result_id
  ), '[]'::jsonb),
  'd', coalesce((
    select jsonb_agg(
      jsonb_build_array(
        rd.id,
        rd.uci_id,
        rd.first_name,
        rd.last_name,
        rd.nation,
        rd.profile_image_url,
        rd.team_id,
        rd.team_name,
        rd.uci_code,
        rd.birth_date,
        null::boolean,
        rd.team_code
      )
    )
    from rider_data rd
  ), '[]'::jsonb),
  't', coalesce((
    select jsonb_agg(
      jsonb_build_array(td.id, td.name, td.code)
    )
    from team_data td
  ), '[]'::jsonb)
);
$function$;

revoke all on function public.get_ranking_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, text, text, bigint[], bigint
) from public;

grant execute on function public.get_ranking_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, text, text, bigint[], bigint
) to anon, authenticated, service_role;

commit;
