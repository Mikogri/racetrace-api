begin;

-- Narrow indexes used by the rider-detail aggregation path. They contain only
-- rows that can contribute to field-size, finish-time, speed or split metrics.
create index if not exists results_session_classified_metrics_idx
  on public.results (session_id, position, rider_id)
  include (id, race_time_ms, top_speed, team_id)
  where (is_classified = true or position > 0)
    and coalesce(upper(status), '') not in ('DNF', 'DNS', 'DSQ');

create index if not exists time_splits_result_valid_timing_idx
  on public.time_splits (result_id, split_index, id desc)
  include (race_time, speed)
  where race_time > 0;

create index if not exists results_session_rider_detail_idx
  on public.results (session_id, rider_id, id)
  include (race_time_ms, top_speed, position, is_classified, status, team_id);

create index if not exists time_splits_result_index_latest_idx
  on public.time_splits (result_id, split_index, id desc)
  include (race_time, speed)
  where split_index > 0;

create or replace function public.metrics_sorted_median(p_values double precision[])
returns double precision
language plpgsql
immutable
strict
parallel safe
set search_path = ''
as $median$
declare
  value_count integer := pg_catalog.cardinality(p_values);
begin
  if value_count = 0 then
    return null;
  end if;

  if value_count % 2 = 1 then
    return p_values[(value_count + 1) / 2];
  end if;

  return (p_values[value_count / 2] + p_values[value_count / 2 + 1]) / 2.0;
end;
$median$;

drop function if exists public.get_rider_detail_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, bigint, bigint[]
);

-- Returns one rider and SQL-preaggregated field distributions. Array positions
-- are decoded by DomainQueryService.ParseRiderDetailDataBundle.
create or replace function public.get_rider_detail_data(
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
  p_rider_id bigint default null,
  p_session_ids bigint[] default '{}'
)
returns jsonb
language sql
stable
security invoker
set search_path = ''
set work_mem = '64MB'
set statement_timeout = '60s'
as $function$
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
relevant_sessions as materialized (
  select distinct session_id from selected_results
),
field_results as materialized (
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
  join relevant_sessions rs on rs.session_id = re.session_id
  where (p_status is null or re.status = p_status)
    and upper(btrim(coalesce(re.status, ''))) <> 'DSQ'
),
deduplicated_splits as materialized (
  select distinct on (ts.result_id, ts.split_index)
    ts.id,
    ts.result_id,
    fr.session_id,
    fr.rider_id,
    ts.split_index,
    ts.race_time,
    ts.speed
  from public.time_splits ts
  join field_results fr on fr.id = ts.result_id
  where ts.split_index > 0
  order by ts.result_id, ts.split_index, ts.id desc
),
latest_splits as materialized (
  select
    selected.id,
    selected.result_id,
    selected.session_id,
    selected.rider_id,
    selected.split_index,
    selected.race_time,
    selected.speed,
    selected.split_index::integer - 1 as sector_index,
    case
      when selected.split_index = 1 then 0
      else previous.race_time
    end as previous_race_time
  from deduplicated_splits selected
  left join deduplicated_splits previous
    on previous.result_id = selected.result_id
   and previous.split_index = selected.split_index - 1
),
session_split_layout as materialized (
  select
    fr.session_id,
    coalesce(max(ds.split_index), 0)::integer as maximum_split_index,
    coalesce(bool_or(
      fr.race_time_ms > 0 and ds.race_time = fr.race_time_ms
    ), false) as finish_is_timing_split
  from field_results fr
  left join deduplicated_splits ds on ds.result_id = fr.id
  group by fr.session_id
),
split_tails as materialized (
  select
    fr.id as result_id,
    layout.maximum_split_index as finish_sector_index,
    case
      when layout.maximum_split_index = 0 then 0
      else tail.race_time
    end as last_race_time
  from field_results fr
  join session_split_layout layout on layout.session_id = fr.session_id
  left join deduplicated_splits tail
    on tail.result_id = fr.id
   and tail.split_index = layout.maximum_split_index
),
raw_sector_rows as materialized (
  select
    ls.result_id,
    ls.rider_id,
    ls.session_id,
    ls.sector_index,
    case
      when ls.race_time > 0
        and ls.previous_race_time is not null
        and ls.race_time > ls.previous_race_time
      then (ls.race_time - ls.previous_race_time)::double precision / 1000.0
    end as time_seconds,
    case when ls.race_time > 0 then ls.race_time::double precision / 1000.0 end as cumulative_seconds,
    false as is_finish_sector
  from latest_splits ls

  union all

  select
    fr.id,
    fr.rider_id,
    fr.session_id,
    st.finish_sector_index,
    case
      when st.last_race_time is not null
        and fr.race_time_ms > st.last_race_time
      then (fr.race_time_ms - st.last_race_time)::double precision / 1000.0
    end,
    case
      when st.last_race_time is not null
        and fr.race_time_ms > st.last_race_time
      then fr.race_time_ms::double precision / 1000.0
    end,
    true
  from field_results fr
  join split_tails st on st.result_id = fr.id
),
sector_values as materialized (
  select *
  from raw_sector_rows
  where time_seconds > 0
),
sector_ranked as materialized (
  select
    sv.*,
    rank() over (
      partition by sv.session_id, sv.sector_index
      order by sv.time_seconds
    )::integer as sector_rank
  from sector_values sv
),
target_sector_ranked as materialized (
  select ranked.*
  from sector_ranked ranked
  where p_rider_id is null or ranked.rider_id = p_rider_id
),
timing_sector_ranked as materialized (
  select
    raw.*,
    rank() over (
      partition by raw.session_id, raw.sector_index
      order by raw.time_seconds
    )::integer as sector_rank
  from raw_sector_rows raw
  where not raw.is_finish_sector and raw.time_seconds > 0
),
target_timing_sector_ranked as materialized (
  select ranked.*
  from timing_sector_ranked ranked
  where p_rider_id is null or ranked.rider_id = p_rider_id
),
sector_groups as materialized (
  select
    session_id,
    sector_index,
    array_agg(time_seconds order by time_seconds) as values
  from sector_values
  group by session_id, sector_index
),
sector_medians as materialized (
  select
    session_id,
    sector_index,
    values,
    cardinality(values)::integer as field_count,
    public.metrics_sorted_median(values) as median_seconds,
    public.metrics_sorted_median(array(
      select ln(value) from unnest(values) as value order by value
    )) as log_median,
    values[1] as best_seconds,
    values[2] as second_best_seconds
  from sector_groups
),
sector_statistics as materialized (
  select
    sm.session_id,
    sm.sector_index,
    sm.field_count,
    sm.median_seconds,
    sm.log_median,
    sm.best_seconds,
    sm.second_best_seconds,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(ln(value) - sm.log_median)
        from unnest(sm.values) as value
        order by abs(ln(value) - sm.log_median)
      )),
      0.003
    ) as log_mad_scale,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(value - sm.median_seconds)
        from unnest(sm.values) as value
        order by abs(value - sm.median_seconds)
      )),
      greatest(sm.median_seconds * 0.003, 0.01)
    ) as linear_mad_scale
  from sector_medians sm
),
cumulative_values as materialized (
  select
    session_id,
    sector_index,
    cumulative_seconds
  from raw_sector_rows
  where cumulative_seconds > 0
),
cumulative_groups as materialized (
  select
    session_id,
    sector_index,
    array_agg(cumulative_seconds order by cumulative_seconds) as values
  from cumulative_values
  group by session_id, sector_index
),
cumulative_medians as materialized (
  select
    session_id,
    sector_index,
    values,
    cardinality(values)::integer as field_count,
    public.metrics_sorted_median(values) as median_seconds,
    public.metrics_sorted_median(array(
      select ln(value) from unnest(values) as value order by value
    )) as log_median,
    values[1] as best_seconds,
    values[2] as second_best_seconds
  from cumulative_groups
),
cumulative_statistics as materialized (
  select
    cm.session_id,
    cm.sector_index,
    cm.field_count,
    cm.median_seconds,
    cm.log_median,
    cm.best_seconds,
    cm.second_best_seconds,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(ln(value) - cm.log_median)
        from unnest(cm.values) as value
        order by abs(ln(value) - cm.log_median)
      )),
      0.003
    ) as log_mad_scale,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(value - cm.median_seconds)
        from unnest(cm.values) as value
        order by abs(value - cm.median_seconds)
      )),
      greatest(cm.median_seconds * 0.003, 0.01)
    ) as linear_mad_scale
  from cumulative_medians cm
),
classified_field_sizes as materialized (
  select session_id, count(distinct rider_id)::integer as field_count
  from field_results
  where is_valid_classification
  group by session_id
),
finish_values as materialized (
  select session_id, race_time_ms::double precision / 1000.0 as time_seconds
  from field_results
  where is_valid_classification and race_time_ms > 0
),
finish_groups as materialized (
  select
    session_id,
    array_agg(time_seconds order by time_seconds) as values
  from finish_values
  group by session_id
),
finish_medians as materialized (
  select
    session_id,
    values,
    cardinality(values)::integer as field_count,
    public.metrics_sorted_median(values) as median_seconds,
    public.metrics_sorted_median(array(
      select ln(value) from unnest(values) as value order by value
    )) as log_median,
    values[1] as best_seconds,
    values[2] as second_best_seconds
  from finish_groups
),
finish_statistics as materialized (
  select
    fm.session_id,
    fm.field_count,
    fm.median_seconds,
    fm.log_median,
    fm.best_seconds,
    fm.second_best_seconds,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(ln(value) - fm.log_median)
        from unnest(fm.values) as value
        order by abs(ln(value) - fm.log_median)
      )),
      0.003
    ) as log_mad_scale,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(value - fm.median_seconds)
        from unnest(fm.values) as value
        order by abs(value - fm.median_seconds)
      )),
      greatest(fm.median_seconds * 0.003, 0.01)
    ) as linear_mad_scale
  from finish_medians fm
),
speed_values as materialized (
  select id as result_id, session_id, rider_id, top_speed::double precision as speed
  from field_results
  where top_speed > 0
),
speed_ranked as materialized (
  select
    sv.*,
    rank() over (partition by sv.session_id order by sv.speed desc)::integer as speed_rank
  from speed_values sv
),
target_speed_ranked as materialized (
  select ranked.*
  from speed_ranked ranked
  where p_rider_id is null or ranked.rider_id = p_rider_id
),
speed_groups as materialized (
  select
    session_id,
    array_agg(speed order by speed) as values
  from speed_values
  group by session_id
),
speed_medians as materialized (
  select
    session_id,
    values,
    cardinality(values)::integer as field_count,
    public.metrics_sorted_median(values) as median_speed,
    public.metrics_sorted_median(array(
      select ln(value) from unnest(values) as value order by value
    )) as log_median,
    values[cardinality(values)] as best_speed,
    values[cardinality(values) - 1] as second_best_speed
  from speed_groups
),
speed_statistics as materialized (
  select
    sm.session_id,
    sm.field_count,
    sm.median_speed,
    sm.log_median,
    sm.best_speed,
    sm.second_best_speed,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(ln(value) - sm.log_median)
        from unnest(sm.values) as value
        order by abs(ln(value) - sm.log_median)
      )),
      0.003
    ) as log_mad_scale,
    greatest(
      1.4826 * public.metrics_sorted_median(array(
        select abs(value - sm.median_speed)
        from unnest(sm.values) as value
        order by abs(value - sm.median_speed)
      )),
      greatest(sm.median_speed * 0.003, 0.01)
    ) as linear_mad_scale
  from speed_medians sm
),
split_ranked as materialized (
  select
    ls.*,
    rank() over (
      partition by ls.session_id, ls.split_index
      order by ls.race_time
    )::integer as split_rank
  from latest_splits ls
  where ls.race_time > 0
),
target_split_ranked as materialized (
  select ranked.*
  from split_ranked ranked
  where p_rider_id is null or ranked.rider_id = p_rider_id
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
    null::boolean as retired_slot,
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
)
select jsonb_build_object(
  'v', 1,
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
  'f', '[]'::jsonb,
  'x', coalesce((
    select jsonb_agg(jsonb_build_array(
      sr.result_id, sr.split_index, sr.race_time, sr.speed,
      sr.split_rank, target_timing_sector_ranked.sector_rank
    ))
    from target_split_ranked sr
    left join target_timing_sector_ranked
      on target_timing_sector_ranked.result_id = sr.result_id
     and target_timing_sector_ranked.sector_index = sr.sector_index
  ), '[]'::jsonb),
  'd', coalesce((
    select jsonb_agg(jsonb_build_array(
      rd.id, rd.uci_id, rd.first_name, rd.last_name, rd.nation,
      rd.profile_image_url, rd.team_id, rd.team_name, rd.uci_code,
      rd.birth_date, null::boolean, rd.team_code, rd.gender
    ))
    from rider_data rd
  ), '[]'::jsonb),
  't', coalesce((
    select jsonb_agg(jsonb_build_array(td.id, td.name, td.code))
    from team_data td
  ), '[]'::jsonb),
  'a', coalesce((
    select jsonb_agg(jsonb_build_array(
      sr.id,
      sr.session_id,
      coalesce(cfs.field_count, 0),
      target_speed_ranked.speed_rank,
      speed_statistics.field_count,
      speed_statistics.median_speed,
      speed_statistics.log_median,
      speed_statistics.log_mad_scale,
      speed_statistics.linear_mad_scale,
      speed_statistics.best_speed,
      speed_statistics.second_best_speed,
      finish_statistics.field_count,
      finish_statistics.median_seconds,
      finish_statistics.log_median,
      finish_statistics.log_mad_scale,
      finish_statistics.linear_mad_scale,
      finish_statistics.best_seconds,
      finish_statistics.second_best_seconds
    ))
    from selected_results sr
    left join classified_field_sizes cfs on cfs.session_id = sr.session_id
    left join target_speed_ranked on target_speed_ranked.result_id = sr.id
    left join speed_statistics on speed_statistics.session_id = sr.session_id
    left join finish_statistics on finish_statistics.session_id = sr.session_id
  ), '[]'::jsonb),
  'q', coalesce((
    select jsonb_agg(jsonb_build_array(
      sr.id,
      ss.sector_index,
      target_sector.time_seconds,
      target_sector.sector_rank,
      ss.field_count,
      ss.median_seconds,
      ss.log_median,
      ss.log_mad_scale,
      ss.linear_mad_scale,
      ss.best_seconds,
      ss.second_best_seconds,
      cs.field_count,
      cs.median_seconds,
      cs.log_median,
      cs.log_mad_scale,
      cs.linear_mad_scale,
      cs.best_seconds,
      cs.second_best_seconds
    ))
    from selected_results sr
    join sector_statistics ss on ss.session_id = sr.session_id
    left join target_sector_ranked target_sector
      on target_sector.result_id = sr.id
     and target_sector.sector_index = ss.sector_index
    left join cumulative_statistics cs
      on cs.session_id = sr.session_id
     and cs.sector_index = ss.sector_index
  ), '[]'::jsonb)
);
$function$;

revoke all on function public.get_rider_detail_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, bigint, bigint[]
) from public;

grant execute on function public.get_rider_detail_data(
  integer[], bigint, bigint[], text[], text[], text, integer[], text[],
  integer[], text[], text[], text[], date, date, text, bigint, bigint[]
) to anon, authenticated, service_role;

revoke all on function public.metrics_sorted_median(double precision[]) from public;
grant execute on function public.metrics_sorted_median(double precision[])
  to anon, authenticated, service_role;

commit;

begin;

do $refresh$
declare
  target_session record;
begin
  for target_session in select id from public.sessions order by id
  loop
    perform public.refresh_ranking_session_preaggregate(target_session.id);
  end loop;
end;
$refresh$;

commit;
