begin;

do $migration$
declare
  function_sql text;
  updated_sql text;
begin
  select pg_get_functiondef(proc.oid)
    into function_sql
  from pg_proc proc
  join pg_namespace namespace on namespace.oid = proc.pronamespace
  where namespace.nspname = 'public'
    and proc.proname = 'get_rider_detail_data'
  order by proc.oid desc
  limit 1;

  if function_sql is null then
    raise exception 'public.get_rider_detail_data was not found';
  end if;

  if position(
      'where (p_status is null or re.status = p_status)' in function_sql
    ) = 0 then
    updated_sql := replace(
      function_sql,
      'where p_status is null or re.status = p_status',
      'where (p_status is null or re.status = p_status)' || E'\n' ||
      '    and upper(btrim(coalesce(re.status, ''''))) <> ''DSQ''');

    if updated_sql = function_sql then
      raise exception 'Could not locate field_results status filter in get_rider_detail_data';
    end if;

    execute updated_sql;
  end if;
end;
$migration$;

do $refresh$
declare
  target_session record;
begin
  for target_session in
    select distinct result.session_id
    from public.results result
    where upper(btrim(coalesce(result.status, ''))) = 'DSQ'
    order by result.session_id
  loop
    perform public.refresh_ranking_session_preaggregate(target_session.session_id);
  end loop;
end;
$refresh$;

commit;
