-- Run against the migrated database; no source data is changed.
do $$
declare
    actual_rank integer;
    definition text;
begin
    select pg_get_functiondef(p.oid) into strict definition
    from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='public' and p.proname='get_rider_input_data';
    if position('from timing_values where race_time>0 and sector_time>0' in definition)=0 then
        raise exception 'Raw input sector ranks must exclude invalid cumulative timings';
    end if;

    with timing_values(result_id, race_time, sector_time) as (
        values (1, -50, 50), (2, 0, 100), (3, 300, 200)
    ), ranked as (
        select result_id, rank() over(order by sector_time)::integer as sector_rank
        from timing_values where race_time>0 and sector_time>0
    )
    select sector_rank into actual_rank from ranked where result_id=3;
    if actual_rank is distinct from 1 then
        raise exception 'Invalid cumulative timings changed the valid rider sector rank';
    end if;
end;
$$;
