-- Extend the existing detail read model; do not persist inferred race times as results.
-- Ranking already transports the entire compact field. Detail needs classified rival
-- times/sectors (no names, teams, points or repeated field statistics) in the same RPC.
do $migration$
declare
  definition text;
  marker constant text := '''f'', ''[]''::jsonb,';
  addition constant text := $payload$
  'i', case when p_rider_id is null then '[]'::jsonb else coalesce((
    select jsonb_agg(jsonb_build_array(
      fr.session_id, fr.rider_id, fr.race_time_ms::double precision / 1000.0,
      coalesce(timings.sectors, '[]'::jsonb)
    ) order by fr.session_id, fr.rider_id)
    from field_results fr
    left join (
      select result_id, jsonb_agg(jsonb_build_array(sector_index, time_seconds, sector_rank)
        order by sector_index) sectors
      from sector_ranked group by result_id
    ) timings on timings.result_id = fr.id
    where fr.is_valid_classification and fr.race_time_ms > 0
  ), '[]'::jsonb) end,
  $payload$;
begin
  select pg_get_functiondef(p.oid) into strict definition
  from pg_proc p join pg_namespace n on n.oid=p.pronamespace
  where n.nspname='public' and p.proname='get_rider_detail_data';
  if position('''i'', case when p_rider_id is null' in definition) > 0 then return; end if;
  if (length(definition)-length(replace(definition, marker, ''))) / length(marker) <> 1 then
    raise exception 'Unexpected rider detail definition; inspect before applying incident context migration';
  end if;
  -- Retain security invoker, search_path, timeouts and all existing filtering.
  execute replace(definition, marker, marker || addition);
end;
$migration$;
notify pgrst, 'reload schema';
