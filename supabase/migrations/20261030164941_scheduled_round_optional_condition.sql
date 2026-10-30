-- Future scheduled rounds have no known racing conditions yet.
-- Applied to RaceTrace; version matches the database migration history.
ALTER TABLE public.rounds ALTER COLUMN condition_id DROP NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.rounds'::regclass
        AND conname = 'rounds_condition_required_unless_scheduled') THEN
        ALTER TABLE public.rounds ADD CONSTRAINT rounds_condition_required_unless_scheduled
            CHECK (condition_id IS NOT NULL OR coalesce(status, '') = 'scheduled');
    END IF;
END $$;
