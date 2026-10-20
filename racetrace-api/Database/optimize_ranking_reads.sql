-- Preserve ranking formulas and compact payload columns; only change the read path.
-- Apply after ranking/team-history RPC migrations. Existing function grants are retained.
BEGIN;

CREATE OR REPLACE FUNCTION public.get_ranking_data(p_season_years integer[] DEFAULT '{}'::integer[], p_round_id bigint DEFAULT NULL::bigint, p_track_ids bigint[] DEFAULT '{}'::bigint[], p_country_codes text[] DEFAULT '{}'::text[], p_venue_names text[] DEFAULT '{}'::text[], p_track_name text DEFAULT NULL::text, p_condition_ids integer[] DEFAULT '{}'::integer[], p_condition_names text[] DEFAULT '{}'::text[], p_competition_class_ids integer[] DEFAULT '{}'::integer[], p_competition_class_codes text[] DEFAULT '{}'::text[], p_category_codes text[] DEFAULT '{}'::text[], p_session_types text[] DEFAULT '{}'::text[], p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_status text DEFAULT NULL::text, p_rider_query text DEFAULT NULL::text, p_nation text DEFAULT NULL::text, p_session_ids bigint[] DEFAULT '{}'::bigint[], p_rider_id bigint DEFAULT NULL::bigint)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE
 SET search_path TO ''
AS $function$
DECLARE
  ranking_data jsonb;
BEGIN
  -- Optional filters differ greatly in selectivity. Re-plan this parameterized
  -- statement per request instead of reusing a generic one-row estimate.
  EXECUTE $ranking$
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
  where ($2 is null or ro.id = $2)
    and (coalesce(cardinality($1), 0) = 0 or ro.season_year = any($1))
    and (coalesce(cardinality($3), 0) = 0 or ro.track_id = any($3))
    and (
      coalesce(cardinality($4), 0) = 0
      or upper(tr.country_code) = any(
        select upper(value) from unnest($4) as value
      )
    )
    and (
      coalesce(cardinality($5), 0) = 0
      or lower(tr.venue) = any(
        select lower(value) from unnest($5) as value
      )
    )
    and ($6 is null or lower(tr.name) = lower($6))
    and (coalesce(cardinality($7), 0) = 0 or ro.condition_id = any($7))
    and (
      coalesce(cardinality($8), 0) = 0
      or lower(co.name) = any(
        select lower(value) from unnest($8) as value
      )
    )
    and (
      coalesce(cardinality($9), 0) = 0
      or ro.competition_level_id = any($9)
    )
    and (
      coalesce(cardinality($10), 0) = 0
      or lower(cl.code) = any(
        select lower(value) from unnest($10) as value
      )
    )
    and ($13 is null or ro.start_date::date >= $13)
    and ($14 is null or ro.start_date::date <= $14)
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
      coalesce(cardinality($11), 0) = 0
      or upper(ca.code) = any(
        select upper(value) from unnest($11) as value
      )
    )
    and (
      coalesce(cardinality($12), 0) = 0
      or upper(se.session_type) = any(
        select upper(value) from unnest($12) as value
      )
    )
    and (coalesce(cardinality($18), 0) = 0 or se.id = any($18))
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
  where ($15 is null or re.status = $15)
),
matching_team_riders as materialized (
  select distinct team_result.rider_id
  from public.teams searched_team
  join public.results team_result on team_result.team_id = searched_team.id
  where $19 is null
    and $16 is not null
    and btrim($16) <> ''
    and (
      searched_team.name ilike ('%' || $16 || '%')
      or searched_team.code ilike ('%' || $16 || '%')
    )
),
matching_riders as materialized (
  select ri.id
  from public.riders ri
  where ($19 is null or ri.id = $19)
    and ($17 is null or upper(ri.nation) = upper($17))
    and (
      $19 is not null
      or
      $16 is null
      or btrim($16) = ''
      or ri.first_name ilike ('%' || $16 || '%')
      or ri.last_name ilike ('%' || $16 || '%')
      or ri.uci_code ilike ('%' || $16 || '%')
      or ri.nation ilike ('%' || $16 || '%')
      or concat_ws(' ', ri.first_name, ri.last_name) ilike ('%' || $16 || '%')
      or ri.id::text = $16
      or ri.uci_id::text = $16
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
    ($17 is null and ($16 is null or btrim($16) = ''))
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
    when $19 is null
      and $17 is null
      and ($16 is null or btrim($16) = '')
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
$ranking$
    INTO ranking_data
    USING p_season_years, p_round_id, p_track_ids, p_country_codes, p_venue_names, p_track_name, p_condition_ids, p_condition_names, p_competition_class_ids, p_competition_class_codes, p_category_codes, p_session_types, p_date_from, p_date_to, p_status, p_rider_query, p_nation, p_session_ids, p_rider_id;
  RETURN ranking_data;
END;
$function$;

CREATE OR REPLACE FUNCTION public.get_ranking_points_raw_summary(p_season_years integer[] DEFAULT '{}'::integer[], p_round_id bigint DEFAULT NULL::bigint, p_track_ids bigint[] DEFAULT '{}'::bigint[], p_country_codes text[] DEFAULT '{}'::text[], p_venue_names text[] DEFAULT '{}'::text[], p_track_name text DEFAULT NULL::text, p_condition_ids integer[] DEFAULT '{}'::integer[], p_condition_names text[] DEFAULT '{}'::text[], p_competition_class_ids integer[] DEFAULT '{}'::integer[], p_competition_class_codes text[] DEFAULT '{}'::text[], p_category_codes text[] DEFAULT '{}'::text[], p_session_types text[] DEFAULT '{}'::text[], p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_status text DEFAULT NULL::text, p_session_ids bigint[] DEFAULT '{}'::bigint[])
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY INVOKER
SET search_path = ''
AS $function$
DECLARE
  points_data jsonb;
BEGIN
  -- Reapply the same round/session predicates as get_ranking_data. Resolved IDs
  -- alone are insufficient when the caller combines IDs with names or codes.
  EXECUTE $points$
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
  where ($2 is null or ro.id = $2)
    and (coalesce(cardinality($1), 0) = 0 or ro.season_year = any($1))
    and (coalesce(cardinality($3), 0) = 0 or ro.track_id = any($3))
    and (
      coalesce(cardinality($4), 0) = 0
      or upper(tr.country_code) = any(
        select upper(value) from unnest($4) as value
      )
    )
    and (
      coalesce(cardinality($5), 0) = 0
      or lower(tr.venue) = any(
        select lower(value) from unnest($5) as value
      )
    )
    and ($6 is null or lower(tr.name) = lower($6))
    and (coalesce(cardinality($7), 0) = 0 or ro.condition_id = any($7))
    and (
      coalesce(cardinality($8), 0) = 0
      or lower(co.name) = any(
        select lower(value) from unnest($8) as value
      )
    )
    and (
      coalesce(cardinality($9), 0) = 0
      or ro.competition_level_id = any($9)
    )
    and (
      coalesce(cardinality($10), 0) = 0
      or lower(cl.code) = any(
        select lower(value) from unnest($10) as value
      )
    )
    and ($13 is null or ro.start_date::date >= $13)
    and ($14 is null or ro.start_date::date <= $14)
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
      coalesce(cardinality($11), 0) = 0
      or upper(ca.code) = any(
        select upper(value) from unnest($11) as value
      )
    )
    and (
      coalesce(cardinality($12), 0) = 0
      or upper(se.session_type) = any(
        select upper(value) from unnest($12) as value
      )
    )
    and (coalesce(cardinality($16), 0) = 0 or se.id = any($16))
),
selected_results AS MATERIALIZED (
  SELECT re.id, re.rider_id
  FROM public.results re
  JOIN filtered_sessions fs ON fs.id = re.session_id
  JOIN public.riders ri ON ri.id = re.rider_id
  WHERE $15 IS NULL OR re.status = $15
),
points_by_result AS (
  SELECT rp.result_id, sum(rp.points_awarded) AS points
  FROM public.result_points rp
  JOIN selected_results sr ON sr.id = rp.result_id
  GROUP BY rp.result_id
),
totals AS (
  SELECT sr.rider_id, sum(coalesce(pr.points, 0)) AS points
  FROM selected_results sr
  LEFT JOIN points_by_result pr ON pr.result_id = sr.id
  GROUP BY sr.rider_id
)
SELECT jsonb_build_object(
  'complete', true,
  'rows', coalesce((
    SELECT jsonb_agg(jsonb_build_object(
      'rider_id', t.rider_id::text,
      'points', t.points,
      'rider', jsonb_build_object('first_name', ri.first_name, 'last_name', ri.last_name)
    ) ORDER BY t.rider_id)
    FROM totals t
    JOIN public.riders ri ON ri.id = t.rider_id
  ), '[]'::jsonb)
);$points$
    INTO points_data
    USING p_season_years, p_round_id, p_track_ids, p_country_codes, p_venue_names, p_track_name, p_condition_ids, p_condition_names, p_competition_class_ids, p_competition_class_codes, p_category_codes, p_session_types, p_date_from, p_date_to, p_status, p_session_ids;
  RETURN points_data;
END;
$function$;

REVOKE ALL ON FUNCTION public.get_ranking_points_raw_summary(integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.get_ranking_points_raw_summary(integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint[]) TO anon, authenticated, service_role;

NOTIFY pgrst, 'reload schema';
COMMIT;
