-- Derived annotations are invalidated with their source package. Raw results,
-- splits, points and the calculator-neutral measurement package stay untouched.
alter table public.ranking_session_preaggregates
  add column incident_version text,
  add column incident_payload text,
  add column incident_computed_at timestamptz;

create or replace function public.invalidate_prepared_ranking_payload()
returns trigger language plpgsql security invoker set search_path='' as $$
begin
  new.source_revision := gen_random_uuid();
  new.prepared_version := null;
  new.prepared_payload := null;
  new.incident_version := null;
  new.incident_payload := null;
  new.incident_computed_at := null;
  return new;
end;
$$;
drop trigger invalidate_prepared_ranking_payload on public.ranking_session_preaggregates;
create trigger invalidate_prepared_ranking_payload before update of payload,input_splits
on public.ranking_session_preaggregates for each row
execute function public.invalidate_prepared_ranking_payload();
revoke all on function public.invalidate_prepared_ranking_payload() from public,anon,authenticated;

create function public.save_ranking_incident_annotations(
  p_session_id bigint,p_source_revision uuid,p_version text,p_payload text)
returns boolean language plpgsql security invoker set search_path='' as $$
declare affected integer;
begin
  if p_version is null or p_version !~ '^incident-annotations-v1:[0-9a-f]{64}$' or
     p_payload is null or length(p_payload)=0 or length(p_payload)>8388608 then
    raise exception 'Invalid incident annotations';
  end if;
  update public.ranking_session_preaggregates
  set incident_version=p_version,incident_payload=p_payload,incident_computed_at=clock_timestamp()
  where session_id=p_session_id and source_revision=p_source_revision;
  get diagnostics affected=row_count;
  return affected=1;
end;
$$;
revoke all on function public.save_ranking_incident_annotations(bigint,uuid,text,text) from public,anon,authenticated;
grant execute on function public.save_ranking_incident_annotations(bigint,uuid,text,text) to service_role;

-- Retain all current identity, privacy, ranking and status logic in the RPCs.
do $migration$
declare definition text; marker text; addition text;
begin
  select pg_get_functiondef('public.get_ranking_input_packages(bigint[],boolean)'::regprocedure) into definition;
  marker := '''ix'',c.input_splits';
  addition := '''ia'',case when c.incident_payload is not null then jsonb_build_array(jsonb_build_array(c.session_id,c.incident_version,c.incident_payload)) else ''[]''::jsonb end,';
  if (length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 then
    raise exception 'Unexpected ranking input function; inspect before migrating';
  end if;
  execute replace(definition,marker,addition||marker);

  select pg_get_functiondef(p.oid) into strict definition from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='public' and p.proname='get_rider_input_data';
  marker := '''v'',3,';
  addition := '''ia'',case when (select cached from validity) then coalesce((select jsonb_agg(jsonb_build_array(c.session_id,c.incident_version,c.incident_payload)) from cache c where c.incident_payload is not null),''[]''::jsonb) else ''[]''::jsonb end,';
  if (length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 then
    raise exception 'Unexpected rider input function; inspect before migrating';
  end if;
  execute replace(definition,marker,marker||addition);
end;
$migration$;
notify pgrst,'reload schema';
