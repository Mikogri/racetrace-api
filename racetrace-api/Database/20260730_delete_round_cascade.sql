-- Atomically deletes a round and data owned exclusively by that round.
-- Shared tracks, riders, teams, categories, conditions and point systems remain intact.

create or replace function public.delete_round_cascade(target_round_id bigint)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  session_ids bigint[] := '{}'::bigint[];
  result_ids bigint[] := '{}'::bigint[];
  deleted_sessions integer := 0;
  deleted_results integer := 0;
  deleted_splits integer := 0;
  deleted_points integer := 0;
  deleted_images integer := 0;
begin
  if target_round_id is null or target_round_id <= 0 then
    raise exception 'target_round_id must be a positive integer';
  end if;

  if not exists (
    select 1
    from public.rounds
    where id = target_round_id
  ) then
    raise no_data_found using message = format('Round %s was not found.', target_round_id);
  end if;

  select coalesce(array_agg(id), '{}'::bigint[])
  into session_ids
  from public.sessions
  where round_id = target_round_id;

  if cardinality(session_ids) > 0 then
    select coalesce(array_agg(id), '{}'::bigint[])
    into result_ids
    from public.results
    where session_id = any(session_ids);
  end if;

  if cardinality(result_ids) > 0 then
    delete from public.result_points
    where result_id = any(result_ids);
    get diagnostics deleted_points = row_count;

    delete from public.time_splits
    where result_id = any(result_ids);
    get diagnostics deleted_splits = row_count;
  end if;

  if to_regclass('public.raw_imports') is not null then
    if exists (
      select 1
      from information_schema.columns
      where table_schema = 'public'
        and table_name = 'raw_imports'
        and column_name = 'round_id'
    ) then
      execute 'delete from public.raw_imports where round_id = $1'
      using target_round_id;
    end if;

    if cardinality(session_ids) > 0 and exists (
      select 1
      from information_schema.columns
      where table_schema = 'public'
        and table_name = 'raw_imports'
        and column_name = 'session_id'
    ) then
      execute 'delete from public.raw_imports where session_id = any($1)'
      using session_ids;
    end if;
  end if;

  if cardinality(session_ids) > 0 and
     to_regclass('public.session_point_systems') is not null then
    execute 'delete from public.session_point_systems where session_id = any($1)'
    using session_ids;
  end if;

  delete from public.round_images
  where round_id = target_round_id;
  get diagnostics deleted_images = row_count;

  if cardinality(result_ids) > 0 then
    delete from public.results
    where id = any(result_ids);
    get diagnostics deleted_results = row_count;
  end if;

  if cardinality(session_ids) > 0 then
    delete from public.sessions
    where id = any(session_ids);
    get diagnostics deleted_sessions = row_count;
  end if;

  delete from public.round_categories
  where round_id = target_round_id;

  delete from public.rounds
  where id = target_round_id;

  return jsonb_build_object(
    'roundId', target_round_id,
    'sessionsDeleted', deleted_sessions,
    'resultsDeleted', deleted_results,
    'splitsDeleted', deleted_splits,
    'pointsDeleted', deleted_points,
    'imagesDeleted', deleted_images
  );
end;
$$;

revoke all on function public.delete_round_cascade(bigint) from public;
revoke all on function public.delete_round_cascade(bigint) from anon;
revoke all on function public.delete_round_cascade(bigint) from authenticated;
grant execute on function public.delete_round_cascade(bigint) to service_role;

-- Make the new RPC visible immediately to PostgREST.
notify pgrst, 'reload schema';
