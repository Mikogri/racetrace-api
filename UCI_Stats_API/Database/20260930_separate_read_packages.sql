-- Additive read projections; source results, points and cache refresh logic remain unchanged.
begin;

create or replace function public.get_ranking_metric_packages(p_session_ids bigint[])
returns table(session_id bigint, payload jsonb)
language sql stable security invoker set search_path = ''
as $fn$
  select a.session_id, a.payload - 'x'
  from public.ranking_session_preaggregates a
  where a.session_id = any(coalesce(p_session_ids, '{}'::bigint[]))
  order by a.session_id;
$fn$;

-- Complete=false prevents presenting a partial points table when a cache entry is missing.
create or replace function public.get_ranking_points_summary(p_session_ids bigint[])
returns jsonb language sql stable security invoker set search_path = ''
as $fn$
with requested as (select distinct unnest(coalesce(p_session_ids, '{}'::bigint[])) id),
packages as materialized (
  select a.session_id, a.payload from public.ranking_session_preaggregates a
  join requested req on req.id = a.session_id
),
totals as (
  select r->>1 rider_id, sum(coalesce((r->>8)::numeric, 0)) points
  from packages p cross join lateral jsonb_array_elements(p.payload->'r') r
  group by r->>1
),
identities as (
  select distinct on (d.row->>0) d.row->>0 rider_id, d.row
  from packages p cross join lateral jsonb_array_elements(p.payload->'d') with ordinality d(row, ordinal)
  order by d.row->>0, p.session_id desc, d.ordinal desc
)
select jsonb_build_object(
  'complete', not exists(select 1 from requested req where not exists(select 1 from packages p where p.session_id=req.id)),
  'rows', coalesce((select jsonb_agg(jsonb_build_object(
    'rider_id', t.rider_id, 'points', t.points,
    'rider', case when i.row is null then null else jsonb_build_object(
      'id', i.row->0, 'uci_id', i.row->1, 'first_name', i.row->2, 'last_name', i.row->3) end
  ) order by t.rider_id) from totals t left join identities i using(rider_id)), '[]'::jsonb)
);
$fn$;

-- The detailed package keeps its original result/split rows and field populations.
-- Only the repeated field summaries are stored once per session/sector in the wire payload.
create or replace function public.compact_rider_detail_payload(p_payload jsonb)
returns jsonb language sql immutable security invoker set search_path = ''
as $fn$
with a as materialized (
  select row, ordinal from jsonb_array_elements(coalesce(p_payload->'a','[]'::jsonb)) with ordinality e(row,ordinal)
), q as materialized (
  select row, ordinal from jsonb_array_elements(coalesce(p_payload->'q','[]'::jsonb)) with ordinality e(row,ordinal)
)
select case when coalesce((p_payload->>'v')::integer, 1) >= 2 then p_payload else
p_payload || jsonb_build_object(
  'v', 2,
  'a', coalesce((select jsonb_agg(jsonb_build_array(row->0,row->1,row->2,row->3) order by ordinal) from a),'[]'::jsonb),
  'q', coalesce((select jsonb_agg(jsonb_build_array(row->0,row->1,row->2,row->3) order by ordinal) from q),'[]'::jsonb),
  'h', coalesce((select jsonb_agg(jsonb_build_array(
    row->1, row->4, row->5, row->6, row->7, row->8, row->9, row->10, row->11, row->12, row->13, row->14, row->15, row->16, row->17
  ) order by row->>1) from (select distinct on (row->>1) row from a order by row->>1,ordinal) s),'[]'::jsonb),
  'g', coalesce((select jsonb_agg(jsonb_build_array(
    session_id, row->1, row->4, row->5, row->6, row->7, row->8, row->9, row->10, row->11, row->12, row->13, row->14, row->15, row->16, row->17
  ) order by session_id, (row->>1)::integer)
  from (select distinct on (a.row->>1,q.row->>1) a.row->1 session_id, q.row
        from q join a on a.row->>0=q.row->>0 order by a.row->>1,q.row->>1,q.ordinal) s),'[]'::jsonb)
) end;
$fn$;

create or replace function public.get_rider_detail_compact_data(
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
returns jsonb language sql stable security invoker set search_path = '' set statement_timeout = '60s'
as $fn$
  select public.compact_rider_detail_payload(public.get_rider_detail_data(
    p_season_years, p_round_id, p_track_ids, p_country_codes, p_venue_names, p_track_name, p_condition_ids, p_condition_names, p_competition_class_ids, p_competition_class_codes, p_category_codes, p_session_types, p_date_from, p_date_to, p_status, p_rider_id, p_session_ids
  ));
$fn$;

revoke all on function public.get_ranking_metric_packages(bigint[]) from public;
revoke all on function public.get_ranking_points_summary(bigint[]) from public;
revoke all on function public.compact_rider_detail_payload(jsonb) from public;
revoke all on function public.get_rider_detail_compact_data(integer[], bigint, bigint[], text[], text[], text, integer[], text[], integer[], text[], text[], text[], date, date, text, bigint, bigint[]) from public;
grant execute on function public.get_ranking_metric_packages(bigint[]) to anon, authenticated, service_role;
grant execute on function public.get_ranking_points_summary(bigint[]) to anon, authenticated, service_role;
grant execute on function public.compact_rider_detail_payload(jsonb) to anon, authenticated, service_role;
grant execute on function public.get_rider_detail_compact_data(integer[], bigint, bigint[], text[], text[], text, integer[], text[], integer[], text[], text[], text[], date, date, text, bigint, bigint[]) to anon, authenticated, service_role;

commit;
notify pgrst, 'reload schema';
