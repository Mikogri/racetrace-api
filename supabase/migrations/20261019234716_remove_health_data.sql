-- APPROVAL REQUIRED: irreversible erasure. Stop all API/import/background writers first.
-- Deploy the matching API/frontend together. Do not run automatically at startup.
begin;
set local lock_timeout = '5s';

-- Preserve the compact wire layout: retired slot 10 stays null, never a health flag.
-- Rewrite the five installed read functions in place, retaining signatures and grants.
do $$
declare item record; definition text;
begin
  for item in
    select p.oid from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and p.proname in (
      'get_prepared_rider_detail_data', 'get_ranking_input_packages',
      'get_rider_input_data', 'get_ranking_data', 'get_rider_detail_data')
  loop
    definition := pg_get_functiondef(item.oid);
    definition := regexp_replace(definition,
      '(?m)^([ \t]*)ri\.has_injury,', '\1null::boolean as retired_slot,', 'g');
    definition := replace(replace(definition, 'rd.has_injury', 'null::boolean'), 'ri.has_injury', 'null::boolean');
    execute definition;
  end loop;
end $$;

-- Remove named legacy keys at every nesting level, including the reserved compact slot.
create function pg_temp.sport_only(value jsonb) returns jsonb
language plpgsql immutable as $$
declare result jsonb; pair record; children jsonb;
begin
  if jsonb_typeof(value) = 'object' then
    result := '{}'::jsonb;
    for pair in select * from jsonb_each(value) loop
      if lower(replace(pair.key, '_', '')) in ('hasinjury','injury','injuryflag','health','healthstatus','medical') then continue; end if;
      if pair.key = 'd' and jsonb_typeof(pair.value) = 'array' then
        children := (select coalesce(jsonb_agg(case when jsonb_typeof(d) = 'array' and jsonb_array_length(d) > 10
          then jsonb_set(d, '{10}', 'null'::jsonb) else d end order by ord), '[]'::jsonb)
          from jsonb_array_elements(pair.value) with ordinality rows(d, ord));
        result := result || jsonb_build_object(pair.key, pg_temp.sport_only(children));
      else result := result || jsonb_build_object(pair.key, pg_temp.sport_only(pair.value)); end if;
    end loop;
    return result;
  elsif jsonb_typeof(value) = 'array' then
    return (select coalesce(jsonb_agg(pg_temp.sport_only(item) order by ord), '[]'::jsonb)
      from jsonb_array_elements(value) with ordinality rows(item, ord));
  end if;
  return value;
end $$;

-- Measurements and calculator inputs stay unchanged; only identity metadata is scrubbed.
update public.ranking_session_preaggregates
set payload = pg_temp.sport_only(payload), prepared_payload = null;

-- Forecasts and prior optimization outputs may reflect the retired exclusion rule.
-- Remove those derived artifacts; sanitize price catalogues, retaining prices/mappings/history.
do $$
begin
  if to_regclass('racetrace_jobs.runs') is not null then
    delete from racetrace_jobs.runs where artifact_kind = 'forecast';
    update racetrace_jobs.runs set payload = jsonb_set(pg_temp.sport_only(payload), '{dataVersion}', '1')
      where artifact_kind = 'prices' and payload is not null;
  end if;
end $$;

-- Deliberately no CASCADE: an unknown dependent object must abort rather than disappear.
alter table public.riders drop column if exists has_injury;
drop function pg_temp.sport_only(jsonb);
commit;
