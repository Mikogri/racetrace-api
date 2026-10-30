-- Preserve global input-package invalidation with pg_safeupdate enabled.
-- Applied to RaceTrace; version matches the database migration history.
-- session_id is the non-null key, so this matches the previous intended scope.
DO $$
DECLARE definition text;
BEGIN
    SELECT pg_get_functiondef('racetrace_private.calculation_source_changed()'::regprocedure) INTO definition;
    IF position('delete from public.ranking_session_preaggregates;' IN definition) > 0 THEN
        EXECUTE replace(definition,
            'delete from public.ranking_session_preaggregates;',
            'delete from public.ranking_session_preaggregates where session_id is not null;');
    ELSIF position('delete from public.ranking_session_preaggregates where session_id is not null;' IN definition) = 0 THEN
        RAISE EXCEPTION 'Unexpected calculation_source_changed definition';
    END IF;
END $$;
