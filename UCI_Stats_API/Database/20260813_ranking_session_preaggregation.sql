begin;

create table if not exists public.ranking_session_preaggregates (
  session_id bigint primary key references public.sessions(id)
    on update cascade on delete cascade,
  payload jsonb not null,
  result_count integer not null,
  split_count integer not null,
  refreshed_at timestamptz not null default now(),
  constraint ranking_session_preaggregates_payload_object
    check (jsonb_typeof(payload) = 'object'),
  constraint ranking_session_preaggregates_counts
    check (result_count >= 0 and split_count >= 0)
);

create index if not exists ranking_session_preaggregates_refreshed_idx
  on public.ranking_session_preaggregates (refreshed_at desc);

alter table public.ranking_session_preaggregates enable row level security;
grant select on public.ranking_session_preaggregates to anon, authenticated;
revoke insert, update, delete, truncate, references, trigger
  on public.ranking_session_preaggregates from anon, authenticated;

drop policy if exists public_read_ranking_session_preaggregates
  on public.ranking_session_preaggregates;
create policy public_read_ranking_session_preaggregates
  on public.ranking_session_preaggregates
  for select to anon, authenticated using (true);

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

revoke all on function public.refresh_ranking_session_preaggregate(bigint) from public;
grant execute on function public.refresh_ranking_session_preaggregate(bigint)
  to service_role;

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

create or replace function public.invalidate_ranking_preaggregate_from_result()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  affected_result_id bigint;
  affected_session_id bigint;
begin
  affected_result_id := case when tg_op = 'DELETE' then old.result_id else new.result_id end;
  select session_id into affected_session_id from public.results where id = affected_result_id;
  if affected_session_id is not null then
    delete from public.ranking_session_preaggregates where session_id = affected_session_id;
  end if;
  return case when tg_op = 'DELETE' then old else new end;
end;
$function$;

create or replace function public.invalidate_ranking_preaggregates_from_round()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  delete from public.ranking_session_preaggregates aggregate
  using public.sessions session
  where aggregate.session_id = session.id
    and session.round_id = case when tg_op = 'DELETE' then old.id else new.id end;
  return case when tg_op = 'DELETE' then old else new end;
end;
$function$;

drop trigger if exists invalidate_ranking_preaggregate_results on public.results;
create trigger invalidate_ranking_preaggregate_results
after insert or update or delete on public.results
for each row execute function public.invalidate_ranking_session_preaggregate();

drop trigger if exists invalidate_ranking_preaggregate_splits on public.time_splits;
create trigger invalidate_ranking_preaggregate_splits
after insert or update or delete on public.time_splits
for each row execute function public.invalidate_ranking_preaggregate_from_result();

drop trigger if exists invalidate_ranking_preaggregate_points on public.result_points;
create trigger invalidate_ranking_preaggregate_points
after insert or update or delete on public.result_points
for each row execute function public.invalidate_ranking_preaggregate_from_result();

drop trigger if exists invalidate_ranking_preaggregate_sessions on public.sessions;
create trigger invalidate_ranking_preaggregate_sessions
after update on public.sessions
for each row execute function public.invalidate_ranking_session_preaggregate();

drop trigger if exists invalidate_ranking_preaggregate_rounds on public.rounds;
create trigger invalidate_ranking_preaggregate_rounds
after update on public.rounds
for each row execute function public.invalidate_ranking_preaggregates_from_round();

-- Initial backfill. SQL Editor runs as postgres, so this may exceed API role timeouts.
set local statement_timeout = '0';
do $backfill$
declare
  target_session record;
begin
  for target_session in select id from public.sessions order by id loop
    perform public.refresh_ranking_session_preaggregate(target_session.id);
  end loop;
end;
$backfill$;

commit;
