begin;
alter table public.ranking_session_preaggregates
  add column source_revision uuid not null default gen_random_uuid(),
  add column prepared_version text,
  add column prepared_payload jsonb;

create function public.invalidate_prepared_ranking_payload() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.source_revision := gen_random_uuid();
  new.prepared_version := null;
  new.prepared_payload := null;
  return new;
end;
$$;
create trigger invalidate_prepared_ranking_payload before update of payload
on public.ranking_session_preaggregates for each row
execute function public.invalidate_prepared_ranking_payload();

create function public.save_prepared_ranking_session(
 p_session_id bigint, p_source_revision uuid, p_algorithm_version text, p_payload jsonb
) returns boolean language plpgsql security invoker set search_path = '' as $$
declare affected integer;
begin
 if jsonb_typeof(p_payload) <> 'array' or p_algorithm_version is null then
   raise exception 'Invalid prepared payload';
 end if;
 update public.ranking_session_preaggregates
 set prepared_payload = p_payload, prepared_version = p_algorithm_version
 where session_id = p_session_id and source_revision = p_source_revision;
 get diagnostics affected = row_count;
 return affected = 1;
end;
$$;
revoke all on function public.save_prepared_ranking_session(bigint,uuid,text,jsonb) from public, anon, authenticated;
grant execute on function public.save_prepared_ranking_session(bigint,uuid,text,jsonb) to service_role;
revoke all on function public.invalidate_prepared_ranking_payload() from public, anon, authenticated;
commit;
