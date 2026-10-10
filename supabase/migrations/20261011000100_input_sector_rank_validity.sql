-- Match the display-rank population used by cached split data.
do $$
declare
    definition text;
begin
    select pg_get_functiondef(p.oid) into strict definition
    from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and p.proname = 'get_rider_input_data';

    if position('from timing_values where sector_time>0' in definition) = 0 then
        raise exception 'Unexpected get_rider_input_data definition';
    end if;

    execute replace(definition,
        'from timing_values where sector_time>0',
        'from timing_values where race_time>0 and sector_time>0');
end;
$$;
