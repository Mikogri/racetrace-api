begin;

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

  -- Rider metadata was previously repeated for every rider in the database in
  -- every session payload. Keep only riders referenced by this session's results.
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

create or replace function public.invalidate_ranking_session_preaggregate()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  affected_session_id bigint;
begin
  affected_session_id := case
    when tg_table_name = 'sessions' and tg_op = 'DELETE' then old.id
    when tg_table_name = 'sessions' then new.id
    when tg_op = 'DELETE' then old.session_id
    else new.session_id
  end;
  delete from public.ranking_session_preaggregates where session_id = affected_session_id;
  return case when tg_op = 'DELETE' then old else new end;
end;
$function$;

-- Compact existing payloads without recalculating any metrics or points.
update public.ranking_session_preaggregates aggregate
set payload = jsonb_set(
  aggregate.payload,
  '{d}',
  coalesce((
    select jsonb_agg(rider_row)
    from jsonb_array_elements(aggregate.payload->'d') rider_row
    where exists (
      select 1
      from jsonb_array_elements(aggregate.payload->'r') result_row
      where result_row->>1 = rider_row->>0
    )
  ), '[]'::jsonb)
);

commit;

notify pgrst, 'reload schema';
