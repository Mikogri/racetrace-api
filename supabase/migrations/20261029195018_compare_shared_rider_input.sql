-- Preserve the deployed input contract (including incident annotations and team
-- history). Only the selected rider predicate changes; each session field is
-- read and transported once for the two riders. No scalar RPC is changed.
do $migration$
declare
    source_definition text;
    compare_definition text;
begin
    select pg_get_functiondef(p.oid) into strict source_definition
    from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and p.proname = 'get_rider_input_data';

    if position('p_rider_id bigint DEFAULT NULL::bigint' in source_definition) = 0
       or position('p_rider_id is null or re.rider_id = p_rider_id' in source_definition) = 0
       or position('p_rider_id is null or ri.id = p_rider_id' in source_definition) = 0
       or source_definition ilike '%SECURITY DEFINER%' then
        raise exception 'Unexpected rider input definition; review comparison migration';
    end if;

    compare_definition := replace(source_definition,
        'public.get_rider_input_data(', 'public.get_compare_input_data(');
    compare_definition := replace(compare_definition,
        'p_rider_id bigint DEFAULT NULL::bigint', 'p_rider_ids bigint[] DEFAULT ''{}''::bigint[]');
    compare_definition := replace(compare_definition,
        'p_rider_id is null or re.rider_id = p_rider_id',
        'cardinality(p_rider_ids) = 2 and re.rider_id = any(p_rider_ids)');
    compare_definition := replace(compare_definition,
        'p_rider_id is null or ri.id = p_rider_id',
        'cardinality(p_rider_ids) = 2 and ri.id = any(p_rider_ids)');
    if compare_definition ~ '\mp_rider_id\M' then
        raise exception 'Unhandled scalar rider parameter in comparison input';
    end if;
    execute compare_definition;
end
$migration$;

revoke all on function public.get_compare_input_data(boolean,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint[],bigint[]) from public;
grant execute on function public.get_compare_input_data(boolean,integer[],bigint,bigint[],text[],text[],text,integer[],text[],integer[],text[],text[],text[],date,date,text,bigint[],bigint[]) to anon, authenticated, service_role;
notify pgrst, 'reload schema';
