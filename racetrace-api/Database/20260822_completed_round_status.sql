begin;

alter table public.rounds
  drop constraint if exists rounds_status_check;

alter table public.rounds
  add constraint rounds_status_check
  check (status in ('scheduled', 'confirmed', 'live', 'completed', 'cancelled'));

comment on column public.rounds.status is
  'Administrative lifecycle status: scheduled, confirmed, live, completed, or cancelled.';

commit;
