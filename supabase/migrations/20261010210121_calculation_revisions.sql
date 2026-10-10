-- Apply explicitly after existing RaceTrace schema/preaggregation migrations.
-- No source data import, no production backfill. All revision changes share the source transaction.
begin;
create schema if not exists racetrace_private;
revoke all on schema racetrace_private from public, anon, authenticated;
grant usage on schema racetrace_private to service_role;

create table if not exists racetrace_private.calculation_revisions (
  scope text primary key,
  revision bigint not null default 1,
  transaction_id text not null,
  changed_at timestamptz not null default clock_timestamp()
);
alter table racetrace_private.calculation_revisions enable row level security;
revoke all on racetrace_private.calculation_revisions from public, anon, authenticated;
grant select on racetrace_private.calculation_revisions to service_role;
drop policy if exists service_read_calculation_revisions on racetrace_private.calculation_revisions;
create policy service_read_calculation_revisions on racetrace_private.calculation_revisions
  for select to service_role using (true);
insert into racetrace_private.calculation_revisions(scope, transaction_id)
values ('all', pg_current_xact_id()::text), ('global', pg_current_xact_id()::text)
on conflict do nothing;

create or replace function racetrace_private.bump_calculation_scope(p_scope text) returns void
language sql security definer set search_path = '' as $$
  insert into racetrace_private.calculation_revisions as r(scope, transaction_id)
  values(p_scope, pg_current_xact_id()::text)
  on conflict(scope) do update set revision = r.revision + 1,
    transaction_id = excluded.transaction_id, changed_at = clock_timestamp()
  where r.transaction_id <> excluded.transaction_id;
$$;
revoke all on function racetrace_private.bump_calculation_scope(text) from public, anon, authenticated;

create or replace function public.get_calculation_revisions() returns jsonb
language sql stable security invoker set search_path = '' as $$
  select coalesce(jsonb_object_agg(scope,revision), '{}'::jsonb)
  from racetrace_private.calculation_revisions;
$$;
revoke all on function public.get_calculation_revisions() from public, anon, authenticated;
grant execute on function public.get_calculation_revisions() to service_role;

create or replace function racetrace_private.invalidate_calculation_round(p_round bigint, p_season integer default null)
returns void language plpgsql security definer set search_path = '' as $$
declare season integer; sid bigint;
begin
  select season_year into season from public.rounds where id=p_round;
  perform racetrace_private.bump_calculation_scope('all');
  perform racetrace_private.bump_calculation_scope('round:' || p_round);
  if coalesce(p_season,season) is not null then
    perform racetrace_private.bump_calculation_scope('season:' || coalesce(p_season,season));
  end if;
  for sid in select id from public.sessions where round_id=p_round loop
    perform racetrace_private.bump_calculation_scope('session:' || sid);
  end loop;
  delete from public.ranking_session_preaggregates a using public.sessions s
    where a.session_id=s.id and s.round_id=p_round;
end;
$$;
revoke all on function racetrace_private.invalidate_calculation_round(bigint,integer) from public, anon, authenticated;

create or replace function racetrace_private.calculation_source_changed() returns trigger
language plpgsql security definer set search_path = '' as $$
declare doc jsonb; rid bigint; sid bigint; keyid bigint;
begin
  -- BEFORE triggers can resolve deleted parents before cascading deletes remove relationships.
  for doc in select value from jsonb_array_elements(
    case tg_op when 'INSERT' then jsonb_build_array(to_jsonb(new))
      when 'DELETE' then jsonb_build_array(to_jsonb(old))
      else jsonb_build_array(to_jsonb(old),to_jsonb(new)) end)
  loop
    rid := null; sid := null; keyid := (doc->>'id')::bigint;
    if tg_table_name='rounds' then
      perform racetrace_private.invalidate_calculation_round(keyid,(doc->>'season_year')::integer);
    elsif tg_table_name in ('sessions','round_categories') then
      rid := (doc->>'round_id')::bigint;
      if tg_table_name='sessions' then perform racetrace_private.bump_calculation_scope('session:' || keyid); end if;
    elsif tg_table_name='results' then
      sid := (doc->>'session_id')::bigint;
    elsif tg_table_name in ('time_splits','result_points') then
      select session_id into sid from public.results where id=(doc->>'result_id')::bigint;
    elsif tg_table_name='tracks' then
      for rid in select id from public.rounds where track_id=keyid loop
        perform racetrace_private.invalidate_calculation_round(rid);
      end loop;
      rid := null;
    elsif tg_table_name in ('riders','rider_team_history','teams') then
      for rid in select distinct s.round_id from public.results r join public.sessions s on s.id=r.session_id
        where (tg_table_name='riders' and r.rider_id=keyid)
          or (tg_table_name='rider_team_history' and r.rider_id=(doc->>'rider_id')::bigint)
          or (tg_table_name='teams' and (r.team_id=keyid or r.rider_id in
            (select rider_id from public.rider_team_history where team_id=keyid)))
      loop perform racetrace_private.invalidate_calculation_round(rid); end loop;
      rid := null;
    else
      -- Global dictionaries/rules can affect any season. Raw source results are retained.
      perform racetrace_private.bump_calculation_scope('global');
      delete from public.ranking_session_preaggregates;
    end if;
    if sid is not null then
      perform racetrace_private.bump_calculation_scope('session:' || sid);
      select round_id into rid from public.sessions where id=sid;
    end if;
    if rid is not null then perform racetrace_private.invalidate_calculation_round(rid); end if;
  end loop;
  perform racetrace_private.bump_calculation_scope('all');
  return case when tg_op='DELETE' then old else new end;
end;
$$;
revoke all on function racetrace_private.calculation_source_changed() from public, anon, authenticated;

do $$ declare t text; begin
  foreach t in array array['results','time_splits','result_points','sessions','rounds','round_categories',
    'tracks','riders','teams','rider_team_history','conditions','categories','competition_levels','competition_ranks',
    'session_types','points_systems','point_system_stages','point_rules'] loop
    if to_regclass('public.' || t) is not null then
      execute format('drop trigger if exists calculation_revision_changed on public.%I',t);
      execute format('create trigger calculation_revision_changed before insert or update or delete on public.%I for each row execute function racetrace_private.calculation_source_changed()',t);
    end if;
  end loop;
end $$;
commit;
