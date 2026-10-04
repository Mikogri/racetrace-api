begin;

create or replace function public.compact_ranking_preaggregate_payload(
  p_payload jsonb,
  p_session_id bigint
)
returns jsonb
language sql
immutable
set search_path = ''
as $function$
  select coalesce(p_payload, '{}'::jsonb) || jsonb_build_object(
    'v', 2,
    'a', coalesce((
      select jsonb_agg(jsonb_build_array(
        aggregate_row->0,
        aggregate_row->1,
        aggregate_row->2,
        aggregate_row->3
      ))
      from jsonb_array_elements(coalesce(p_payload->'a', '[]'::jsonb)) aggregate_row
    ), '[]'::jsonb),
    'q', coalesce((
      select jsonb_agg(jsonb_build_array(
        sector_row->0,
        sector_row->1,
        sector_row->2,
        sector_row->3
      ))
      from jsonb_array_elements(coalesce(p_payload->'q', '[]'::jsonb)) sector_row
    ), '[]'::jsonb),
    'h', coalesce((
      select jsonb_agg(jsonb_build_array(
        p_session_id,
        aggregate_row->4,
        aggregate_row->5,
        aggregate_row->6,
        aggregate_row->7,
        aggregate_row->8,
        aggregate_row->9,
        aggregate_row->10,
        aggregate_row->11,
        aggregate_row->12,
        aggregate_row->13,
        aggregate_row->14,
        aggregate_row->15,
        aggregate_row->16,
        aggregate_row->17
      ))
      from (
        select value as aggregate_row
        from jsonb_array_elements(coalesce(p_payload->'a', '[]'::jsonb))
        limit 1
      ) source
      where jsonb_array_length(aggregate_row) > 4
    ), '[]'::jsonb),
    'g', coalesce((
      select jsonb_agg(jsonb_build_array(
        p_session_id,
        sector_row->1,
        sector_row->4,
        sector_row->5,
        sector_row->6,
        sector_row->7,
        sector_row->8,
        sector_row->9,
        sector_row->10,
        sector_row->11,
        sector_row->12,
        sector_row->13,
        sector_row->14,
        sector_row->15,
        sector_row->16,
        sector_row->17
      ) order by (sector_row->>1)::integer)
      from (
        select distinct on ((value->>1)::integer) value as sector_row
        from jsonb_array_elements(coalesce(p_payload->'q', '[]'::jsonb))
        where jsonb_array_length(value) > 4
        order by (value->>1)::integer
      ) source
    ), '[]'::jsonb)
  );
$function$;

create or replace function public.refresh_ranking_session_preaggregate(
  p_session_id bigint
)
returns void
language plpgsql
security definer
set search_path = ''
set statement_timeout = '60s'
as $function$
declare
  aggregate_payload jsonb;
begin
  if not exists (select 1 from public.sessions where id = p_session_id) then
    delete from public.ranking_session_preaggregates where session_id = p_session_id;
    return;
  end if;

  aggregate_payload := public.get_rider_detail_data(
    p_rider_id => null,
    p_session_ids => array[p_session_id]
  );

  aggregate_payload := jsonb_set(
    aggregate_payload,
    '{d}',
    coalesce((
      select jsonb_agg(rider_row)
      from jsonb_array_elements(aggregate_payload->'d') rider_row
      where exists (
        select 1
        from jsonb_array_elements(aggregate_payload->'r') result_row
        where result_row->>1 = rider_row->>0
      )
    ), '[]'::jsonb)
  );
  aggregate_payload := public.compact_ranking_preaggregate_payload(
    aggregate_payload,
    p_session_id
  );

  insert into public.ranking_session_preaggregates (
    session_id,
    payload,
    result_count,
    split_count,
    refreshed_at
  )
  values (
    p_session_id,
    aggregate_payload,
    jsonb_array_length(aggregate_payload->'r'),
    jsonb_array_length(aggregate_payload->'x'),
    now()
  )
  on conflict (session_id) do update set
    payload = excluded.payload,
    result_count = excluded.result_count,
    split_count = excluded.split_count,
    refreshed_at = excluded.refreshed_at;
end;
$function$;

-- Existing packages can be compacted without recalculating points or metrics.
update public.ranking_session_preaggregates aggregate
set payload = public.compact_ranking_preaggregate_payload(
  aggregate.payload,
  aggregate.session_id
)
where coalesce((aggregate.payload->>'v')::integer, 1) < 2;

drop function if exists public.get_ranking_preaggregate_bundle(bigint[]);

create or replace function public.get_ranking_preaggregate_packages(
  p_session_ids bigint[]
)
returns table (
  session_id bigint,
  payload jsonb
)
language sql
stable
security invoker
set search_path = ''
set statement_timeout = '60s'
as $function$
  select aggregate.session_id, aggregate.payload
  from public.ranking_session_preaggregates aggregate
  where aggregate.session_id = any(coalesce(p_session_ids, array[]::bigint[]))
  order by aggregate.session_id;
$function$;

revoke all on function public.compact_ranking_preaggregate_payload(jsonb, bigint)
  from public;
revoke all on function public.get_ranking_preaggregate_packages(bigint[])
  from public;
grant execute on function public.get_ranking_preaggregate_packages(bigint[])
  to anon, authenticated, service_role;

commit;

notify pgrst, 'reload schema';
