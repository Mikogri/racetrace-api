-- Read-only regression check. No cache refresh, imports, or data changes.
BEGIN;
SET LOCAL statement_timeout = '90s';
DO $verify$
DECLARE
  raw jsonb;
  compact jsonb;
  actual jsonb;
  expected jsonb;
  test record;
BEGIN
  FOR test IN
    SELECT format('p_category_codes => ARRAY[%L], p_status => %L', c, s) args
    FROM unnest(ARRAY['EM','EW','JM','JW']) c
    CROSS JOIN unnest(ARRAY[NULL::text,'Finished','DNF','DNS','DSQ']) s
    UNION ALL SELECT args FROM (VALUES
      ('p_category_codes => ARRAY[''EM''], p_competition_class_ids => ARRAY[2], p_competition_class_codes => ARRAY[''world_championship'']'),
      ('p_category_codes => ARRAY[''EM''], p_condition_ids => ARRAY[1], p_condition_names => ARRAY[''nonexistent-condition'']'),
      ('p_category_codes => ARRAY[''EM''], p_track_name => ''%Snake%'''),
      ('p_category_codes => ARRAY[''EM''], p_venue_names => ARRAY[''%Sole%'']'),
      ('p_category_codes => ARRAY[''EM''], p_competition_class_codes => ARRAY[''world_championship'']'),
      ('p_category_codes => ARRAY[''EM''], p_season_years => ARRAY[2025,2026], p_country_codes => ARRAY[''ITA''], p_session_types => ARRAY[''FINAL'']'),
      ('p_category_codes => ARRAY[''EW''], p_date_from => ''2024-01-01''::date, p_date_to => ''2025-07-01''::date'),
      ('p_category_codes => ARRAY[''JM''], p_session_ids => ARRAY[1,1,2]::bigint[]'),
      ('p_season_years => ARRAY[2099]')
    ) cases(args)
  LOOP
    -- args is a fixed test fixture above, never request input.
    EXECUTE 'SELECT public.get_ranking_data(' || test.args || ')' INTO raw;
    EXECUTE 'SELECT public.get_ranking_points_raw_summary(' || test.args || ')' INTO compact;
    WITH totals AS (
      SELECT r->>1 rider_id, sum(coalesce((r->>8)::numeric,0)) points
      FROM jsonb_array_elements(raw->'r') r GROUP BY r->>1
    ), identities AS (
      SELECT d->>0 rider_id, jsonb_build_object('first_name',d->2,'last_name',d->3) rider
      FROM jsonb_array_elements(raw->'d') d
    )
    SELECT coalesce(jsonb_agg(jsonb_build_object(
      'rider_id', t.rider_id, 'points', t.points, 'rider', i.rider
    ) ORDER BY t.rider_id),'[]'::jsonb)
    INTO expected FROM totals t LEFT JOIN identities i USING(rider_id);

    SELECT coalesce(jsonb_agg(r ORDER BY r->>'rider_id'),'[]'::jsonb)
    INTO actual FROM jsonb_array_elements(compact->'rows') r;
    IF expected IS DISTINCT FROM actual THEN
      RAISE EXCEPTION 'Points parity failed: %',test.args;
    END IF;
  END LOOP;
END;
$verify$;
ROLLBACK;
SELECT '29 raw points-summary cases passed (all riders, exact points and names)' AS verification;
